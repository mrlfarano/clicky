using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// The machine-readable record of one interaction, written as JSON after every run (replay or live). Field
/// meanings are in docs/02-parity-spec.md, "Replay report fields this spec relies on". Times are milliseconds
/// since the first SSE line was read; eventTMs is when the line that caused a bubble change was read, so
/// tMs - eventTMs is the render latency ADR 001 acceptance 9 bounds.
/// </summary>
internal sealed class TracerRunReport
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public int Schema => 1;
    public int ExitCode { get; set; }
    public required string Mode { get; init; }
    public string? Fixture { get; init; }
    public string? SpokenText { get; set; }
    public string? ResponseText { get; set; }
    public List<DisplayTimelineEntry> DisplayTimeline { get; } = [];
    public long? LastEventTMs { get; set; }
    public PointReport? Point { get; set; }

    /// <summary>
    /// How many SetWindowPos calls the overlay issued for its own placement (WPF's internal calls
    /// excluded). Every call follows a real placement change; stationary rendering frames issue none —
    /// the small-moving-window design's answer to audit risk 1 (T-004).
    /// </summary>
    public int SetWindowPosCalls { get; set; }

    public List<MonitorReport> Monitors { get; } = [];
    public List<string> CaptureErrors { get; } = [];

    /// <summary>
    /// The virtual-clock tMs of every screen capture, one per submitted transcript (parity spec CLA-01). Unlike
    /// monitors and requestBody, which describe the latest exchange, it accumulates over the whole run.
    /// </summary>
    public List<long> CaptureTimeline { get; } = [];
    public JsonNode? RequestBody { get; set; }
    public string? Error { get; set; }

    // The phase-6 report fields (WS-04 part 2): present as arrays in every report, populated only by the
    // pieces that own them. voiceStateTimeline holds every voice-state change CompanionCoordinator's reducer
    // made; the three sink-backed arrays hold what the in-memory stub sinks recorded (T-029 and T-044
    // replace the stubs with the real TTS and PostHog sinks).
    public List<VoiceStateTimelineEntry> VoiceStateTimeline { get; } = [];
    public List<AnalyticsEventReport> AnalyticsEvents { get; } = [];
    public List<TtsRequestReport> TtsRequests { get; } = [];
    public List<string> FallbackUtterances { get; } = [];

    /// <summary>The reducer's conversation history when the report was written (CLA-06: a failed reply adds none).</summary>
    public List<ConversationExchange> ConversationHistory { get; } = [];

    /// <summary>
    /// Every request the replay handler received, in arrival order, answered or not: the TLS warm-up HEAD and
    /// each /chat attempt, so a retry shows as a second POST (CLA-06/G2/G3). Empty in live mode.
    /// </summary>
    public List<HttpRequestReport> HttpRequests { get; } = [];

    /// <summary>
    /// The user settings this run actually used: the settings the app loaded with every --settings-override
    /// pair applied on top. Shown even when nothing was overridden, so a report always states its settings
    /// the way the parity scenarios read them (CLA-08, ONB-03).
    /// </summary>
    public required EffectiveSettingsReport EffectiveSettings { get; init; }

    public void WriteTo(string reportPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(this, ReportJsonOptions));
    }

    /// <summary>
    /// The outgoing /chat body with each image's base64 swapped for a fingerprint, so reports stay small and
    /// comparable. <paramref name="screenshotSizes"/> is in the same order as the images in the body.
    /// </summary>
    public static JsonNode SummarizeRequestBody(JsonObject requestBody, IReadOnlyList<PixelSize> screenshotSizes)
    {
        JsonNode summarizedBody = requestBody.DeepClone();
        int imageIndex = 0;
        foreach (JsonNode? contentBlock in summarizedBody["messages"]![0]!["content"]!.AsArray())
        {
            if ((string?)contentBlock!["type"] != "image")
            {
                continue;
            }
            JsonNode imageSource = contentBlock["source"]!;
            byte[] imageBytes = Convert.FromBase64String((string)imageSource["data"]!);
            imageSource["data"] = new JsonObject
            {
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(imageBytes)),
                ["width"] = screenshotSizes[imageIndex].Width,
                ["height"] = screenshotSizes[imageIndex].Height,
                ["bytes"] = imageBytes.Length,
            };
            imageIndex++;
        }
        return summarizedBody;
    }
}

internal sealed record DisplayTimelineEntry(long TMs, long EventTMs, string Text);

/// <summary>
/// One voiceState change with its milliseconds on the virtual clock that times the interaction (the same
/// TimeProvider that paces the SSE stream), recorded by CompanionCoordinator whenever the session reducer
/// changes it. A change caused by a scripted input event carries that event's scheduled tMs.
/// </summary>
internal sealed record VoiceStateTimelineEntry(long TMs, string State);

/// <summary>One analytics event exactly as it was handed to the analytics sink (parity spec AN-01).</summary>
internal sealed record AnalyticsEventReport(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? Properties)
{
    /// <summary>A copy of the catalogue event; an event without properties reports none, as before T-044.</summary>
    public static AnalyticsEventReport FromAnalyticsEvent(AnalyticsEvent analyticsEvent) => new(
        analyticsEvent.EventName,
        analyticsEvent.EventProperties.Count == 0 ? null : (JsonObject)analyticsEvent.EventProperties.DeepClone());
}

/// <summary>One request as the replay handler received it: the HTTP method and the URL's absolute path.</summary>
internal sealed record HttpRequestReport(string Method, string Path);

/// <summary>One text handed to the TTS sink to be spoken; the /tts request body the real sink would send.</summary>
internal sealed record TtsRequestReport(string Text);

/// <summary>
/// The effectiveSettings report section: the effective user settings of the run, with every value shown
/// under its settings-file key name (the ONB-04 key set) so reports and settings files read the same way.
/// </summary>
internal sealed record EffectiveSettingsReport(
    string SelectedClaudeModel,
    bool IsClickyCursorEnabled,
    bool HasCompletedOnboarding,
    bool HasSubmittedEmail,
    string PushToTalkShortcut,
    string? AnalyticsDistinctId)
{
    public static EffectiveSettingsReport FromCompanionSettings(CompanionSettings companionSettings)
    {
        // The persisted settings-file name (e.g. "controlOption"), not the enum spelling "ControlOption".
        string shortcutSettingsFileName = companionSettings.PushToTalkShortcut.ToString();
        shortcutSettingsFileName = char.ToLowerInvariant(shortcutSettingsFileName[0]) + shortcutSettingsFileName[1..];
        return new EffectiveSettingsReport(
            companionSettings.SelectedClaudeModel,
            companionSettings.IsClickyCursorEnabled,
            companionSettings.HasCompletedOnboarding,
            companionSettings.HasSubmittedEmail,
            shortcutSettingsFileName,
            companionSettings.AnalyticsDistinctId);
    }
}

internal sealed record PhysicalPixelPointReport(int X, int Y);

/// <param name="ScreenIndex">0-based index into <see cref="TracerRunReport.Monitors"/> of the monitor pointed at.</param>
/// <param name="TargetPx">Where ScreenshotToScreenMapper put the point.</param>
/// <param name="HotspotPx">Where the overlay's hotspot actually is, read back with GetWindowRect.</param>
internal sealed record PointReport(
    long? ScreenNumber,
    int ScreenIndex,
    string? Label,
    ScreenshotPoint ScreenshotPoint,
    string MonitorDevice,
    PhysicalPixelPointReport TargetPx,
    PhysicalPixelPointReport HotspotPx);

/// <param name="NonBlackPixelRatio">Null when the capture failed (see <see cref="TracerRunReport.CaptureErrors"/>).</param>
internal sealed record MonitorReport(
    int Index,
    string Device,
    PhysicalPixelRect RectPx,
    uint Dpi,
    bool IsCursorMonitor,
    PixelSize Screenshot,
    string Label,
    double? NonBlackPixelRatio);
