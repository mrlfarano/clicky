using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class SessionReducerGoldenVectorTests
{
    private const string SeamName = "session-reducer";
    private const string PanelStatusGroup = "panel-status";

    public static TheoryData<string> CaseIds => CaseIdsInGroup(groupName: null);

    public static TheoryData<string> PanelStatusCaseIds => CaseIdsInGroup(PanelStatusGroup);

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void StatesEffectsAndHistoryMatchGoldenVector(string caseId)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);
        JsonNode input = vectorCase["input"]!;
        JsonNode expected = vectorCase["expected"]!;

        CompanionSessionState sessionState = CompanionSessionState.Initial;
        if (input["initialHistory"] is JsonArray initialHistory)
        {
            sessionState = sessionState with { ConversationHistory = ReadConversationHistory(initialHistory) };
        }

        // The virtual clock: ScheduleTransientHide arms a FakeTimeProvider timer, as the shell arms a real one,
        // and an "advance" step moves the clock and then reduces whatever the timers reported.
        var fakeTimeProvider = new FakeTimeProvider();
        var elapsedTimerEvents = new Queue<CompanionSessionEvent>();
        var actualVoiceStates = new List<string>();
        var actualEffectsPerEvent = new List<List<string>>();
        foreach (JsonNode? sessionEventNode in input["events"]!.AsArray())
        {
            var effectsForThisStep = new List<string>();
            if ((string)sessionEventNode!["type"]! == "advance")
            {
                fakeTimeProvider.Advance(TimeSpan.FromMilliseconds((int)sessionEventNode["ms"]!));
                while (elapsedTimerEvents.TryDequeue(out CompanionSessionEvent? elapsedTimerEvent))
                {
                    ReduceOneEvent(elapsedTimerEvent);
                }
            }
            else
            {
                ReduceOneEvent(ReadSessionEvent(sessionEventNode));
            }
            actualVoiceStates.Add(sessionState.VoiceState.ToString().ToLowerInvariant());
            actualEffectsPerEvent.Add(effectsForThisStep);

            void ReduceOneEvent(CompanionSessionEvent sessionEvent)
            {
                CompanionSessionTransition transition = CompanionSession.Reduce(sessionState, sessionEvent);
                sessionState = transition.State;
                effectsForThisStep.AddRange(transition.Effects.Select(DescribeEffect));
                foreach (CompanionSessionEffect.ScheduleTransientHide scheduleTransientHide in transition.Effects.OfType<CompanionSessionEffect.ScheduleTransientHide>())
                {
                    fakeTimeProvider.CreateTimer(
                        _ => elapsedTimerEvents.Enqueue(new CompanionSessionEvent.TransientHideElapsed(scheduleTransientHide.TransientHideId)),
                        state: null,
                        scheduleTransientHide.Delay,
                        Timeout.InfiniteTimeSpan);
                }
            }
        }

        Assert.Equal(ReadStrings(expected["voiceStates"]!), actualVoiceStates);
        List<List<string>> expectedEffectsPerEvent = expected["effects"]!.AsArray()
            .Select(effectsForOneEvent => ReadStrings(effectsForOneEvent!))
            .ToList();
        Assert.Equal(expectedEffectsPerEvent, actualEffectsPerEvent);
        Assert.Equal(ReadConversationHistory(expected["history"]!.AsArray()), sessionState.ConversationHistory);
    }

    [Theory]
    [MemberData(nameof(PanelStatusCaseIds))]
    public void PanelStatusMatchesGoldenVector(string caseId)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);
        JsonNode input = vectorCase["input"]!;
        JsonNode expected = vectorCase["expected"]!;

        foreach (CompanionVoiceState voiceState in Enum.GetValues<CompanionVoiceState>())
        {
            CompanionPanelStatus panelStatus = CompanionPanelStatus.From(
                (bool)input["hasCompletedOnboarding"]!,
                (bool)input["hasMicrophonePermission"]!,
                (bool)input["isOverlayVisible"]!,
                voiceState);
            JsonNode expectedForVoiceState = expected["byVoiceState"]![voiceState.ToString().ToLowerInvariant()]!;

            Assert.Equal((string)expectedForVoiceState["statusText"]!, panelStatus.StatusText);
            Assert.Equal((string)expectedForVoiceState["dotColor"]!, JsonNamingPolicy.CamelCase.ConvertName(panelStatus.StatusDotColor.ToString()));
            Assert.Equal((string?)expected["hintText"], panelStatus.PushToTalkHintText);
        }
    }

    // The case ids the T-010 and T-011 acceptance criteria and parity spec PTT-02, PTT-03, CLA-07, AN-02, TTS-02
    // and ONB-03 name, plus one case per reducer branch, so neither can be dropped from the vector file unnoticed.
    private static readonly string[] AcceptanceCaseIds =
    [
        "repress-during-response-cancels",
        "repress-during-finalize-ignored",
        "ignored-press-still-emits-release",
        "tap-without-speech",
        "history-cap-10",
        "history-stores-spoken-text",
        "cancelled-not-stored",
        "transient-hide-after-tts-plus-1s",
        "transient-hide-waits-for-pointing",
        "transient-hide-cancelled-by-press",
        "persistent-shows-on-start",
        "transient-hidden-on-start",
    ];

    private static readonly string[] ReducerBranchIds =
    [
        "exchange-with-pointing",
        "press-cancels-in-flight-request",
        "press-stops-tts",
        "press-ignored-while-finalizing",
        "release-always-tracked",
        "release-before-recording",
        "dictation-cancelled",
        "empty-transcript",
        "history-cap",
        "history-strips-point-tag",
        "blank-spoken-text",
        "stale-response-ignored",
        "response-failed",
        "persistent-start",
        "transient-start",
        "start-before-setup",
        "transient-hide-after-tts",
        "transient-hide-after-pointing",
        "press-cancels-transient-hide",
        "status-by-voice-state",
        "status-ready",
        "status-setup",
        "status-setup-dot-follows-overlay",
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

        // PNL-06: the panel-status group covers onboarded × mic × overlay; each case checks every voiceState.
        HashSet<string> panelStatusInputsInFile = allVectorCases
            .Where(vectorCase => (string?)vectorCase["group"] == PanelStatusGroup)
            .Select(vectorCase => vectorCase["input"]!)
            .Select(input => $"{(bool)input["hasCompletedOnboarding"]!} {(bool)input["hasMicrophonePermission"]!} {(bool)input["isOverlayVisible"]!}")
            .ToHashSet();
        Assert.Equal(8, panelStatusInputsInFile.Count);
    }

    private static TheoryData<string> CaseIdsInGroup(string? groupName)
    {
        var caseIds = new TheoryData<string>();
        foreach (JsonObject vectorCase in GoldenVectors.LoadAllCases(SeamName).Where(vectorCase => (string?)vectorCase["group"] == groupName))
        {
            caseIds.Add((string)vectorCase["id"]!);
        }
        return caseIds;
    }

    private static CompanionSessionEvent ReadSessionEvent(JsonNode sessionEventNode) => (string)sessionEventNode["type"]! switch
    {
        "started" => new CompanionSessionEvent.Started(
            (bool)sessionEventNode["isClickyCursorEnabled"]!,
            (bool)sessionEventNode["hasCompletedOnboarding"]!,
            (bool)sessionEventNode["hasMicrophonePermission"]!),
        "pointingFinished" => new CompanionSessionEvent.PointingFinished(),
        "shortcutPressed" => new CompanionSessionEvent.ShortcutPressed(),
        "shortcutReleased" => new CompanionSessionEvent.ShortcutReleased(),
        "recordingStarted" => new CompanionSessionEvent.RecordingStarted(),
        "transcriptFinal" => new CompanionSessionEvent.TranscriptFinal((string)sessionEventNode["text"]!),
        "dictationCancelled" => new CompanionSessionEvent.DictationCancelled(),
        "responseReady" => new CompanionSessionEvent.ResponseReady((int)sessionEventNode["requestId"]!, (string)sessionEventNode["text"]!),
        "responseFailed" => new CompanionSessionEvent.ResponseFailed((int)sessionEventNode["requestId"]!, (string)sessionEventNode["error"]!),
        "ttsStarted" => new CompanionSessionEvent.TtsStarted(),
        "ttsEnded" => new CompanionSessionEvent.TtsEnded(),
        string unknownEventType => throw new InvalidDataException($"Unknown session event type '{unknownEventType}'"),
    };

    /// <summary>The compact effect notation the vector file's description documents.</summary>
    private static string DescribeEffect(CompanionSessionEffect sessionEffect) => sessionEffect switch
    {
        CompanionSessionEffect.ShowOverlay => "showOverlay",
        CompanionSessionEffect.HideOverlay => "hideOverlay",
        CompanionSessionEffect.ScheduleTransientHide scheduleTransientHide => string.Create(
            CultureInfo.InvariantCulture,
            $"scheduleTransientHide {scheduleTransientHide.TransientHideId} {scheduleTransientHide.Delay.TotalMilliseconds}ms"),
        CompanionSessionEffect.DismissPanel => "dismissPanel",
        CompanionSessionEffect.CancelChatRequest cancelChatRequest => $"cancelChatRequest {cancelChatRequest.ChatRequestId}",
        CompanionSessionEffect.StopTtsPlayback => "stopTtsPlayback",
        CompanionSessionEffect.ClearPointingTarget => "clearPointingTarget",
        CompanionSessionEffect.StartRecording => "startRecording",
        CompanionSessionEffect.StopRecording => "stopRecording",
        CompanionSessionEffect.StartChatRequest startChatRequest =>
            $"startChatRequest {startChatRequest.ChatRequestId} history={startChatRequest.ConversationHistory.Count}",
        CompanionSessionEffect.PointAtElement pointAtElement => string.Create(
            CultureInfo.InvariantCulture,
            $"pointAtElement {pointAtElement.PointTagParseResult.Coordinate!.Value.X},{pointAtElement.PointTagParseResult.Coordinate.Value.Y} {pointAtElement.PointTagParseResult.ElementLabel}"),
        CompanionSessionEffect.SpeakResponse speakResponse => $"speakResponse {speakResponse.SpokenText}",
        CompanionSessionEffect.SpeakFallbackUtterance speakFallbackUtterance => $"speakFallbackUtterance {speakFallbackUtterance.Utterance}",
        CompanionSessionEffect.TrackAnalyticsEvent { EventContent: null } trackAnalyticsEvent => $"analytics {trackAnalyticsEvent.EventName}",
        CompanionSessionEffect.TrackAnalyticsEvent trackAnalyticsEvent => $"analytics {trackAnalyticsEvent.EventName}: {trackAnalyticsEvent.EventContent}",
        _ => throw new InvalidOperationException($"No notation for effect {sessionEffect}"),
    };

    private static List<ConversationExchange> ReadConversationHistory(JsonArray historyArray) =>
        historyArray
            .Select(exchangeNode => new ConversationExchange((string)exchangeNode!["userTranscript"]!, (string)exchangeNode["assistantResponse"]!))
            .ToList();

    private static List<string> ReadStrings(JsonNode arrayNode) =>
        arrayNode.AsArray().Select(stringNode => (string)stringNode!).ToList();
}
