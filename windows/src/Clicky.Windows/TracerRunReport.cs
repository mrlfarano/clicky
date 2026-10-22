using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public JsonNode? RequestBody { get; set; }
    public string? Error { get; set; }

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
