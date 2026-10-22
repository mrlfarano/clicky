using System.Globalization;
using System.Text.Json.Nodes;

namespace Clicky.Core;

/// <summary>A PostHog capture: the event name and its properties (empty when the Swift call passes none).</summary>
public sealed record AnalyticsEvent(string EventName, JsonObject EventProperties);

/// <summary>A PostHog identify call: the distinct id and the person properties set with it.</summary>
public sealed record AnalyticsIdentify(string DistinctId, JsonObject PersonProperties);

/// <summary>
/// Seam 8: the AN-01 event catalogue, ported from ClickyAnalytics.swift:27-120 with names and property keys
/// verbatim, plus identify from CompanionManager.swift:161-163. Builds events only; the sink sends them.
/// </summary>
public sealed class AnalyticsEvents
{
    /// <summary>
    /// AN-03, the one switch: true sends the full transcript and response text as the Mac does
    /// (ClickyAnalytics.swift:83-96). The product owner deferred stripping them; false removes both.
    /// </summary>
    public bool IncludesTranscriptAndResponseContent { get; init; } = true;

    /// <summary>
    /// Swift String.count counts grapheme clusters, so "👍🏽ok" is 3; UTF-16 Length would say 6 (AN-01).
    /// </summary>
    public static int CountCharacters(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>ClickyAnalytics.swift:27-32. Swift falls back to "unknown" when the bundle has no version.</summary>
    public AnalyticsEvent AppOpened(string appVersion) =>
        new("app_opened", new JsonObject { ["app_version"] = appVersion });

    public AnalyticsEvent OnboardingStarted() => new("onboarding_started", []);

    public AnalyticsEvent OnboardingReplayed() => new("onboarding_replayed", []);

    public AnalyticsEvent OnboardingVideoCompleted() => new("onboarding_video_completed", []);

    public AnalyticsEvent OnboardingDemoTriggered() => new("onboarding_demo_triggered", []);

    public AnalyticsEvent AllPermissionsGranted() => new("all_permissions_granted", []);

    /// <summary>On Windows only "microphone" can fire (AN-01).</summary>
    public AnalyticsEvent PermissionGranted(string permission) =>
        new("permission_granted", new JsonObject { ["permission"] = permission });

    public AnalyticsEvent PushToTalkStarted() => new("push_to_talk_started", []);

    public AnalyticsEvent PushToTalkReleased() => new("push_to_talk_released", []);

    /// <summary>ClickyAnalytics.swift:83-88.</summary>
    public AnalyticsEvent UserMessageSent(string transcript) =>
        new("user_message_sent", BuildContentProperties("transcript", transcript));

    /// <summary>ClickyAnalytics.swift:91-96. response is the spoken text with the POINT tag stripped.</summary>
    public AnalyticsEvent AiResponseReceived(string response) =>
        new("ai_response_received", BuildContentProperties("response", response));

    /// <summary>ClickyAnalytics.swift:100-104: a nil label is sent as "unknown".</summary>
    public AnalyticsEvent ElementPointed(string? elementLabel) =>
        new("element_pointed", new JsonObject { ["element_label"] = elementLabel ?? "unknown" });

    public AnalyticsEvent ResponseError(string error) =>
        new("response_error", new JsonObject { ["error"] = error });

    public AnalyticsEvent TtsError(string error) =>
        new("tts_error", new JsonObject { ["error"] = error });

    /// <summary>CompanionManager.swift:161-163: the caller passes the already-trimmed email.</summary>
    public AnalyticsIdentify Identify(string email) =>
        new(email, new JsonObject { ["email"] = email });

    private JsonObject BuildContentProperties(string contentPropertyKey, string content)
    {
        var contentProperties = new JsonObject();
        if (IncludesTranscriptAndResponseContent)
        {
            contentProperties[contentPropertyKey] = content;
        }
        contentProperties["character_count"] = CountCharacters(content);
        return contentProperties;
    }
}
