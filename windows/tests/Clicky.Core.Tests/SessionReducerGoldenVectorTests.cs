using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class SessionReducerGoldenVectorTests
{
    private const string SeamName = "session-reducer";

    public static TheoryData<string> CaseIds => GoldenVectors.CaseIds(SeamName);

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

        var actualVoiceStates = new List<string>();
        var actualEffectsPerEvent = new List<List<string>>();
        foreach (JsonNode? sessionEventNode in input["events"]!.AsArray())
        {
            CompanionSessionTransition transition = CompanionSession.Reduce(sessionState, ReadSessionEvent(sessionEventNode!));
            sessionState = transition.State;
            actualVoiceStates.Add(sessionState.VoiceState.ToString().ToLowerInvariant());
            actualEffectsPerEvent.Add(transition.Effects.Select(DescribeEffect).ToList());
        }

        Assert.Equal(ReadStrings(expected["voiceStates"]!), actualVoiceStates);
        List<List<string>> expectedEffectsPerEvent = expected["effects"]!.AsArray()
            .Select(effectsForOneEvent => ReadStrings(effectsForOneEvent!))
            .ToList();
        Assert.Equal(expectedEffectsPerEvent, actualEffectsPerEvent);
        Assert.Equal(ReadConversationHistory(expected["history"]!.AsArray()), sessionState.ConversationHistory);
    }

    // The case ids the T-010 acceptance criteria and parity spec PTT-02, PTT-03, CLA-07 and AN-02 name,
    // plus one case per reducer branch, so neither can be dropped from the vector file unnoticed.
    private static readonly string[] AcceptanceCaseIds =
    [
        "repress-during-response-cancels",
        "repress-during-finalize-ignored",
        "ignored-press-still-emits-release",
        "tap-without-speech",
        "history-cap-10",
        "history-stores-spoken-text",
        "cancelled-not-stored",
    ];

    private static readonly string[] ReducerBranchIds =
    [
        "exchange-with-pointing",
        "press-cancels-in-flight-request",
        "press-stops-tts",
        "press-ignored-while-finalizing",
        "release-always-tracked",
        "release-before-recording",
        "empty-transcript",
        "history-cap",
        "history-strips-point-tag",
        "blank-spoken-text",
        "stale-response-ignored",
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

    private static CompanionSessionEvent ReadSessionEvent(JsonNode sessionEventNode) => (string)sessionEventNode["type"]! switch
    {
        "shortcutPressed" => new CompanionSessionEvent.ShortcutPressed(),
        "shortcutReleased" => new CompanionSessionEvent.ShortcutReleased(),
        "recordingStarted" => new CompanionSessionEvent.RecordingStarted(),
        "transcriptFinal" => new CompanionSessionEvent.TranscriptFinal((string)sessionEventNode["text"]!),
        "responseReady" => new CompanionSessionEvent.ResponseReady((int)sessionEventNode["requestId"]!, (string)sessionEventNode["text"]!),
        "ttsStarted" => new CompanionSessionEvent.TtsStarted(),
        "ttsEnded" => new CompanionSessionEvent.TtsEnded(),
        string unknownEventType => throw new InvalidDataException($"Unknown session event type '{unknownEventType}'"),
    };

    /// <summary>The compact effect notation the vector file's description documents.</summary>
    private static string DescribeEffect(CompanionSessionEffect sessionEffect) => sessionEffect switch
    {
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
