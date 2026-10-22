using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class TranscriptFinalizeGoldenVectorTests
{
    private const string SeamName = "transcript-finalize";

    public static TheoryData<string> CaseIds => GoldenVectors.CaseIds(SeamName);

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void EffectsAndFinishTimeMatchGoldenVector(string caseId)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);
        JsonNode input = vectorCase["input"]!;
        JsonNode expected = vectorCase["expected"]!;
        bool isProviderStalled = (bool?)input["stalledProvider"] ?? false;

        // The virtual clock: ScheduleDeadline arms a deadline the test tracks itself and reduces DeadlineElapsed
        // at the exact ms the clock crosses it. The earlier CreateTimer-on-FakeTimeProvider form lost callbacks
        // under load on CI (callbacks are queued, so a busy thread pool from the parallel WPF process tests
        // delivered them after the effects bucket was captured) — tracked deadlines are deterministic everywhere.
        var fakeTimeProvider = new FakeTimeProvider();
        DateTimeOffset clockStartTime = fakeTimeProvider.GetUtcNow();
        TranscriptFinalizeState finalizeState = TranscriptFinalizeState.Initial;
        var finishedAtMilliseconds = new List<double>();
        var actualEffectsPerEvent = new List<List<string>>();
        // A deadline fires during whichever advance step crosses its due time, so its effects belong to that step.
        List<string> effectsForCurrentStep = [];
        var armedDeadlineDueTimes = new List<(TranscriptFinalizeDeadline Deadline, DateTimeOffset DueAt)>();
        foreach (JsonNode? finalizeEventNode in input["events"]!.AsArray())
        {
            effectsForCurrentStep = [];
            if ((string)finalizeEventNode!["type"]! == "advance")
            {
                // Step 1 ms at a time so a deadline fires at its exact due time, not the end of a coarse jump.
                for (int advancedMilliseconds = 0; advancedMilliseconds < (int)finalizeEventNode["ms"]!; advancedMilliseconds++)
                {
                    fakeTimeProvider.Advance(TimeSpan.FromMilliseconds(1));
                    // Copy the armed list: reducing a deadline can schedule another (e.g. grace -> fallback chain).
                    foreach ((TranscriptFinalizeDeadline armedDeadline, DateTimeOffset dueAt) in armedDeadlineDueTimes.ToList())
                    {
                        if (fakeTimeProvider.GetUtcNow() >= dueAt)
                        {
                            armedDeadlineDueTimes.Remove((armedDeadline, dueAt));
                            ReduceOneEvent(new TranscriptFinalizeEvent.DeadlineElapsed(armedDeadline));
                        }
                    }
                }
            }
            else
            {
                ReduceOneEvent(ReadFinalizeEvent(finalizeEventNode));
            }
            actualEffectsPerEvent.Add(effectsForCurrentStep);
        }

        void ReduceOneEvent(TranscriptFinalizeEvent finalizeEvent)
        {
            TranscriptFinalizeTransition transition = TranscriptFinalize.Reduce(finalizeState, finalizeEvent);
            finalizeState = transition.State;
            effectsForCurrentStep.AddRange(transition.Effects.Select(DescribeEffect));
            if (transition.Effects.Any(IsFinishingEffect))
            {
                finishedAtMilliseconds.Add((fakeTimeProvider.GetUtcNow() - clockStartTime).TotalMilliseconds);
            }
            foreach (TranscriptFinalizeEffect.ScheduleDeadline scheduleDeadline in transition.Effects.OfType<TranscriptFinalizeEffect.ScheduleDeadline>())
            {
                if (isProviderStalled && scheduleDeadline.Deadline == TranscriptFinalizeDeadline.ProviderGrace)
                {
                    continue;
                }
                armedDeadlineDueTimes.Add((scheduleDeadline.Deadline, fakeTimeProvider.GetUtcNow() + scheduleDeadline.Delay));
            }
        }

        List<List<string>> expectedEffectsPerEvent = expected["effects"]!.AsArray()
            .Select(effectsForOneEvent => effectsForOneEvent!.AsArray().Select(effectNode => (string)effectNode!).ToList())
            .ToList();
        Assert.Equal(expectedEffectsPerEvent, actualEffectsPerEvent);
        // Exactly one finish: first wins and every later path is a no-op (parity spec STT-05 d).
        Assert.Equal([(double)expected["finishedAtMs"]!], finishedAtMilliseconds);
    }

    // The case ids the T-022 acceptance criteria and parity spec STT-04, STT-05, STT-06 and STT-08 name, plus one case
    // per reducer branch, so neither can be dropped from the vector file unnoticed.
    private static readonly string[] AcceptanceCaseIds =
    [
        "grace-1400ms",
        "session-fallback-2800ms",
        "default-fallback-2400ms",
        "first-wins",
        "end-of-turn-before-grace",
        "termination-delivers",
        "error-no-text-cancels",
        "error-with-partial-submits",
        "empty-final-not-submitted",
    ];

    private static readonly string[] ReducerBranchIds =
    [
        "provider-grace",
        "session-fallback",
        "default-fallback",
        "first-wins",
        "end-of-turn-after-key-up",
        "termination-while-finalizing",
        "error-no-text",
        "error-while-recording",
        "error-with-partial-while-finalizing",
        "error-no-text-while-finalizing",
        "empty-final",
    ];

    [Fact]
    public void EveryAcceptanceCaseAndBranchCovered()
    {
        List<JsonObject> allVectorCases = GoldenVectors.LoadAllCases(SeamName).ToList();
        HashSet<string> caseIdsInFile = allVectorCases.Select(vectorCase => (string)vectorCase["id"]!).ToHashSet();
        HashSet<string?> branchIdsInFile = allVectorCases.Select(vectorCase => (string?)vectorCase["branch"]).ToHashSet();

        foreach (string acceptanceCaseId in AcceptanceCaseIds)
        {
            Assert.Contains(acceptanceCaseId, caseIdsInFile);
        }
        foreach (string reducerBranchId in ReducerBranchIds)
        {
            Assert.Contains(reducerBranchId, branchIdsInFile);
        }
    }

    private static TranscriptFinalizeEvent ReadFinalizeEvent(JsonNode finalizeEventNode) => (string)finalizeEventNode["type"]! switch
    {
        "turn" => new TranscriptFinalizeEvent.TurnReceived((string)finalizeEventNode["transcript"]!, (bool)finalizeEventNode["endsTurn"]!),
        "keyReleased" => new TranscriptFinalizeEvent.KeyReleased((bool)finalizeEventNode["hasActiveTranscriptionSession"]!),
        "termination" => new TranscriptFinalizeEvent.TerminationReceived(),
        "error" => new TranscriptFinalizeEvent.ErrorReceived((string)finalizeEventNode["message"]!),
        string unknownEventType => throw new InvalidDataException($"Unknown finalize event type '{unknownEventType}'"),
    };

    private static bool IsFinishingEffect(TranscriptFinalizeEffect finalizeEffect) =>
        finalizeEffect is TranscriptFinalizeEffect.SubmitTranscript
            or TranscriptFinalizeEffect.DiscardEmptyTranscript
            or TranscriptFinalizeEffect.CancelDictation;

    /// <summary>The compact effect notation the vector file's description documents.</summary>
    private static string DescribeEffect(TranscriptFinalizeEffect finalizeEffect) => finalizeEffect switch
    {
        TranscriptFinalizeEffect.ScheduleDeadline scheduleDeadline =>
            $"scheduleDeadline {JsonNamingPolicy.CamelCase.ConvertName(scheduleDeadline.Deadline.ToString())} {scheduleDeadline.Delay.TotalMilliseconds}ms",
        TranscriptFinalizeEffect.SendForceEndpoint => "sendForceEndpoint",
        TranscriptFinalizeEffect.SendTerminate => "sendTerminate",
        TranscriptFinalizeEffect.SubmitTranscript submitTranscript => $"submitTranscript {submitTranscript.TranscriptText}",
        TranscriptFinalizeEffect.DiscardEmptyTranscript => "discardEmptyTranscript",
        TranscriptFinalizeEffect.CancelDictation cancelDictation => $"cancelDictation {cancelDictation.ErrorMessage}",
        _ => throw new InvalidDataException($"Unknown finalize effect {finalizeEffect}"),
    };
}
