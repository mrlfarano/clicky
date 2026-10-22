using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class AnalyticsEventsGoldenVectorTests
{
    private const string SeamName = "analytics-events";

    /// <summary>Parity spec AN-01 table, every event plus identify.</summary>
    private static readonly string[] An01EventNames =
    [
        "app_opened", "onboarding_started", "onboarding_replayed", "onboarding_video_completed",
        "onboarding_demo_triggered", "permission_granted", "all_permissions_granted", "push_to_talk_started",
        "push_to_talk_released", "user_message_sent", "ai_response_received", "element_pointed",
        "response_error", "tts_error", "identify",
    ];

    public static TheoryData<string> CaseIds => GoldenVectors.CaseIds(SeamName);

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void EventNameAndPropertiesMatchGoldenVector(string caseId)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);
        string swiftFunction = (string)vectorCase["input"]!["swiftFunction"]!;
        JsonObject swiftArguments = vectorCase["input"]!["arguments"]!.AsObject();
        JsonNode expected = vectorCase["expected"]!;
        var analyticsEvents = new AnalyticsEvents();

        if (swiftFunction == "identify")
        {
            AnalyticsIdentify analyticsIdentify = analyticsEvents.Identify((string)swiftArguments["email"]!);
            Assert.Equal((string)expected["distinctId"]!, analyticsIdentify.DistinctId);
            AssertJsonEqual(expected["properties"]!, analyticsIdentify.PersonProperties);
            return;
        }

        AnalyticsEvent analyticsEvent = BuildEventForSwiftFunction(analyticsEvents, swiftFunction, swiftArguments);
        Assert.Equal((string)expected["name"]!, analyticsEvent.EventName);
        AssertJsonEqual(expected["properties"]!, analyticsEvent.EventProperties);
    }

    [Fact]
    public void VectorHasACaseForEveryAn01EventPlusIdentify()
    {
        HashSet<string> vectorEventNames = GoldenVectors.LoadAllCases(SeamName)
            .Select(vectorCase => (string?)vectorCase["expected"]!["name"] ?? (string)vectorCase["input"]!["swiftFunction"]!)
            .ToHashSet();

        Assert.Equal(An01EventNames.Order(), vectorEventNames.Order());
    }

    [Fact]
    public void CharacterCountEmojiCountsGraphemeClusters()
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, "character-count-emoji");
        string transcript = (string)vectorCase["input"]!["arguments"]!["transcript"]!;

        Assert.Equal("👍🏽ok", transcript);
        Assert.Equal(6, transcript.Length);
        Assert.Equal(3, new StringInfo(transcript).LengthInTextElements);
        Assert.Equal(3, AnalyticsEvents.CountCharacters(transcript));
        Assert.Equal(3, (int)new AnalyticsEvents().UserMessageSent(transcript).EventProperties["character_count"]!);
    }

    [Theory]
    [InlineData("transcript-included", "transcript")]
    [InlineData("response-included", "response")]
    public void ContentIncludedCasesSendFullText(string caseId, string contentPropertyKey)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);
        string content = (string)vectorCase["input"]!["arguments"]![contentPropertyKey]!;
        var analyticsEvents = new AnalyticsEvents();

        Assert.True(analyticsEvents.IncludesTranscriptAndResponseContent);
        AnalyticsEvent analyticsEvent = contentPropertyKey == "transcript"
            ? analyticsEvents.UserMessageSent(content)
            : analyticsEvents.AiResponseReceived(content);
        Assert.Equal(content, (string)analyticsEvent.EventProperties[contentPropertyKey]!);
        Assert.Equal(content, (string)vectorCase["expected"]!["properties"]![contentPropertyKey]!);
    }

    [Fact]
    public void OneSwitchStripsContent()
    {
        var contentStrippedAnalyticsEvents = new AnalyticsEvents { IncludesTranscriptAndResponseContent = false };

        AnalyticsEvent userMessageSent = contentStrippedAnalyticsEvents.UserMessageSent("👍🏽ok");
        AnalyticsEvent aiResponseReceived = contentStrippedAnalyticsEvents.AiResponseReceived("click save.");

        // Only the content goes; the name and character_count stay so the dashboards keep working.
        Assert.Equal("user_message_sent", userMessageSent.EventName);
        AssertJsonEqual(new JsonObject { ["character_count"] = 3 }, userMessageSent.EventProperties);
        Assert.Equal("ai_response_received", aiResponseReceived.EventName);
        AssertJsonEqual(new JsonObject { ["character_count"] = 11 }, aiResponseReceived.EventProperties);
    }

    private static AnalyticsEvent BuildEventForSwiftFunction(
        AnalyticsEvents analyticsEvents, string swiftFunction, JsonObject swiftArguments) => swiftFunction switch
    {
        "trackAppOpened" => analyticsEvents.AppOpened((string)swiftArguments["appVersion"]!),
        "trackOnboardingStarted" => analyticsEvents.OnboardingStarted(),
        "trackOnboardingReplayed" => analyticsEvents.OnboardingReplayed(),
        "trackOnboardingVideoCompleted" => analyticsEvents.OnboardingVideoCompleted(),
        "trackOnboardingDemoTriggered" => analyticsEvents.OnboardingDemoTriggered(),
        "trackAllPermissionsGranted" => analyticsEvents.AllPermissionsGranted(),
        "trackPermissionGranted" => analyticsEvents.PermissionGranted((string)swiftArguments["permission"]!),
        "trackPushToTalkStarted" => analyticsEvents.PushToTalkStarted(),
        "trackPushToTalkReleased" => analyticsEvents.PushToTalkReleased(),
        "trackUserMessageSent" => analyticsEvents.UserMessageSent((string)swiftArguments["transcript"]!),
        "trackAIResponseReceived" => analyticsEvents.AiResponseReceived((string)swiftArguments["response"]!),
        "trackElementPointed" => analyticsEvents.ElementPointed((string?)swiftArguments["elementLabel"]),
        "trackResponseError" => analyticsEvents.ResponseError((string)swiftArguments["error"]!),
        "trackTTSError" => analyticsEvents.TtsError((string)swiftArguments["error"]!),
        _ => throw new ArgumentOutOfRangeException(nameof(swiftFunction), swiftFunction, "No catalogue method for this Swift function."),
    };

    private static void AssertJsonEqual(JsonNode expectedJson, JsonNode actualJson) =>
        Assert.True(JsonNode.DeepEquals(expectedJson, actualJson), $"expected {expectedJson.ToJsonString()}, got {actualJson.ToJsonString()}");
}
