using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// ADR 001 acceptance 2, 8 (report half) and 9, and parity spec CLA-02, CLA-04, CLA-05, PNT-04, PNT-05.
/// Screen capture runs for real, so on a runner where Windows.Graphics.Capture is unavailable the request
/// simply carries fewer images; the assertions below allow for that through the report's captureErrors.
/// </summary>
[Collection(ClickyProcessReplayCollection.Name)]
public sealed class ReplayEndToEndTests
{
    private const string PointBasicSpokenText = "that's the save button up in the toolbar. click it and you're done.";
    private const int HotspotTolerancePx = 4; // ADR 001 acceptance 8

    [Fact]
    public async Task PointBasicReplayExitsZeroAndReportsSpokenTextWithTagRemoved()
    {
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync("point-basic.sse");
        JsonNode report = replayRun.Report;

        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");
        Assert.Equal(0, (int)report["exitCode"]!);
        Assert.Null(report["error"]);
        Assert.Equal(PointBasicSpokenText, (string?)report["spokenText"]);

        // The tag is split across two deltas in the fixture; the bubble must never have shown any of it.
        JsonArray displayTimeline = report["displayTimeline"]!.AsArray();
        Assert.All(displayTimeline, timelineEntry => Assert.DoesNotContain("[", (string)timelineEntry!["text"]!));
        Assert.Equal(PointBasicSpokenText, (string?)displayTimeline[^1]!["text"]);

        // No screen number in the tag, so it points at the cursor's monitor.
        JsonArray monitors = report["monitors"]!.AsArray();
        int cursorMonitorIndex = monitors.ToList().FindIndex(monitor => (bool)monitor!["isCursorMonitor"]!);
        Assert.Equal(cursorMonitorIndex, (int)report["point"]!["screenIndex"]!);
        Assert.Equal("save button", (string?)report["point"]!["label"]);
    }

    [Fact]
    public async Task PointBasicRequestBodyMatchesTheMessagesShape()
    {
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync("point-basic.sse");
        JsonNode report = replayRun.Report;
        JsonNode requestBody = report["requestBody"]!;
        JsonNode shippedSettings = JsonNode.Parse(File.ReadAllText(
            Path.Combine(Path.GetDirectoryName(ClickyReplayProcess.ClickyExecutablePath)!, "appsettings.json")))!;
        string sharedSystemPrompt = File.ReadAllText(
            Path.Combine(ClickyReplayProcess.RepositoryRootDirectory, "shared", "prompts", "companion-voice-response.txt"));

        Assert.Equal((string?)shippedSettings["ClaudeModel"], (string?)requestBody["model"]);
        Assert.Equal(1024, (int)requestBody["max_tokens"]!);
        Assert.True((bool)requestBody["stream"]!);
        Assert.Equal(sharedSystemPrompt, (string?)requestBody["system"]);

        JsonArray requestMessages = requestBody["messages"]!.AsArray();
        Assert.Single(requestMessages);
        Assert.Equal("user", (string?)requestMessages[0]!["role"]);

        // Content is [image, label] per successfully captured monitor, cursor's monitor first, then the prompt.
        JsonArray contentBlocks = requestMessages[0]!["content"]!.AsArray();
        Assert.Equal((string?)shippedSettings["TracerPromptText"], (string?)contentBlocks[^1]!["text"]);
        List<JsonNode> capturedMonitors = report["monitors"]!.AsArray()
            .Where(monitor => monitor!["nonBlackPixelRatio"] is not null)
            .Select(monitor => monitor!)
            .ToList();
        Assert.Equal(report["monitors"]!.AsArray().Count, capturedMonitors.Count + report["captureErrors"]!.AsArray().Count);
        Assert.Equal(capturedMonitors.Count * 2 + 1, contentBlocks.Count);
        for (int capturedIndex = 0; capturedIndex < capturedMonitors.Count; capturedIndex++)
        {
            JsonNode imageBlock = contentBlocks[capturedIndex * 2]!;
            JsonNode labelBlock = contentBlocks[capturedIndex * 2 + 1]!;
            Assert.Equal("image", (string?)imageBlock["type"]);
            Assert.Equal("image/jpeg", (string?)imageBlock["source"]!["media_type"]);
            Assert.Equal((int)capturedMonitors[capturedIndex]["screenshot"]!["width"]!, (int)imageBlock["source"]!["data"]!["width"]!);
            Assert.Equal((int)capturedMonitors[capturedIndex]["screenshot"]!["height"]!, (int)imageBlock["source"]!["data"]!["height"]!);
            Assert.Equal((string?)capturedMonitors[capturedIndex]["label"], (string?)labelBlock["text"]);
        }
    }

    [Fact]
    public async Task EventDelayReplayRendersTheFirstTokenBeforeTheStreamEnds()
    {
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync("point-basic.sse", "--event-delay-ms", "200");
        JsonNode report = replayRun.Report;
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        JsonArray displayTimeline = report["displayTimeline"]!.AsArray();
        long lastEventTMs = (long)report["lastEventTMs"]!;
        JsonNode firstDisplayChange = displayTimeline[0]!;
        long firstRenderLatencyMs = (long)firstDisplayChange["tMs"]! - (long)firstDisplayChange["eventTMs"]!;

        Assert.True(firstRenderLatencyMs <= 300, $"first token took {firstRenderLatencyMs} ms to render");
        Assert.True((long)firstDisplayChange["tMs"]! < lastEventTMs, "first token rendered only after the last event");
        Assert.True(
            displayTimeline.Count(timelineEntry => (long)timelineEntry!["tMs"]! < lastEventTMs) >= 2,
            "the bubble did not grow while the stream was still arriving");
    }

    [Theory]
    [InlineData("point-quarter-screen1.sse", 1)]
    [InlineData("point-quarter-screen2.sse", 2)]
    public async Task QuarterPointLandsOnTheIndicatedMonitorWithinTolerance(string fixtureFileName, int taggedScreenNumber)
    {
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync(fixtureFileName);
        JsonNode report = replayRun.Report;
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        // Worked out from the report's monitor list, not by ScreenshotToScreenMapper. With fewer monitors than
        // the tag names, the point falls back to the cursor's monitor (parity spec PNT-02).
        JsonArray monitors = report["monitors"]!.AsArray();
        int expectedMonitorIndex = taggedScreenNumber <= monitors.Count
            ? taggedScreenNumber - 1
            : monitors.ToList().FindIndex(monitor => (bool)monitor!["isCursorMonitor"]!);
        JsonNode expectedMonitor = monitors[expectedMonitorIndex]!;
        JsonNode monitorRect = expectedMonitor["rectPx"]!;
        JsonNode screenshotSize = expectedMonitor["screenshot"]!;
        // The fixtures point at (320, 180), which is the quarter mark on a 16:9 monitor's 1280x720 screenshot.
        double expectedHotspotX = (int)monitorRect["left"]! + (int)monitorRect["width"]! * 320.0 / (int)screenshotSize["width"]!;
        double expectedHotspotY = (int)monitorRect["top"]! + (int)monitorRect["height"]! * 180.0 / (int)screenshotSize["height"]!;

        JsonNode point = report["point"]!;
        Assert.Equal(expectedMonitorIndex, (int)point["screenIndex"]!);
        Assert.Equal((string?)expectedMonitor["device"], (string?)point["monitorDevice"]);
        Assert.InRange((double)point["hotspotPx"]!["x"]!, expectedHotspotX - HotspotTolerancePx, expectedHotspotX + HotspotTolerancePx);
        Assert.InRange((double)point["hotspotPx"]!["y"]!, expectedHotspotY - HotspotTolerancePx, expectedHotspotY + HotspotTolerancePx);
    }

    // T-004: the content-sized moving window is positioned so the glyph's tip lands exactly on the mapped
    // target (OverlayPlacement's PNT-04 decision) — the read-back hotspot equals the target with no
    // tolerance at all, on both quarter-screen fixtures.
    [Theory]
    [InlineData("point-quarter-screen1.sse")]
    [InlineData("point-quarter-screen2.sse")]
    public async Task HotspotPxEqualsTargetPx(string fixtureFileName)
    {
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync(fixtureFileName);
        JsonNode report = replayRun.Report;
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        JsonNode point = report["point"]!;
        Assert.Equal((int)point["targetPx"]!["x"]!, (int)point["hotspotPx"]!["x"]!);
        Assert.Equal((int)point["targetPx"]!["y"]!, (int)point["hotspotPx"]!["y"]!);
    }

    // T-004 / parity PNT-05: with a single moving window, "the buddy appears only on monitor K" reduces to
    // the overlay's monitor being the one the tag named. With fewer monitors than the tag names, the point
    // falls back to the cursor's monitor (parity spec PNT-02).
    [Theory]
    [InlineData("point-quarter-screen1.sse", 1)]
    [InlineData("point-quarter-screen2.sse", 2)]
    public async Task MonitorDeviceMatchesTarget(string fixtureFileName, int taggedScreenNumber)
    {
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync(fixtureFileName);
        JsonNode report = replayRun.Report;
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        JsonArray monitors = report["monitors"]!.AsArray();
        int expectedMonitorIndex = taggedScreenNumber <= monitors.Count
            ? taggedScreenNumber - 1
            : monitors.ToList().FindIndex(monitor => (bool)monitor!["isCursorMonitor"]!);

        Assert.Equal((string?)monitors[expectedMonitorIndex]!["device"], (string?)report["point"]!["monitorDevice"]);
    }

    // T-004 / parity CLA-04, ADR 001 acceptance 9: the first token must be visible within 300 ms of its
    // event being delivered, and the bubble must keep streaming afterwards. The 300 ms is measured from the
    // delta's delivery (tMs - eventTMs): the absolute tMs cannot be bounded by 300 ms with --event-delay-ms
    // 200, because point-basic.sse's first text delta is its 4th SSE event, so it arrives ~600 ms after the
    // first line even though the render itself takes only a few ms.
    [Fact]
    public async Task StreamingBubbleVisibleWithin300ms()
    {
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync("point-basic.sse", "--event-delay-ms", "200");
        JsonNode report = replayRun.Report;
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        JsonArray displayTimeline = report["displayTimeline"]!.AsArray();
        long lastEventTMs = (long)report["lastEventTMs"]!;
        JsonNode firstDisplayChange = displayTimeline[0]!;
        long firstRenderLatencyMs = (long)firstDisplayChange["tMs"]! - (long)firstDisplayChange["eventTMs"]!;

        Assert.True(firstRenderLatencyMs <= 300, $"first token took {firstRenderLatencyMs} ms to render after delivery");
        Assert.True((long)firstDisplayChange["tMs"]! < lastEventTMs, "first token rendered only after the last event");
        // The bubble keeps streaming while the interaction is still processing: voiceState reaches the report
        // with T-002's voiceStateTimeline, and until then the still-arriving stream is the observable half
        // of "processing", so every delta must extend the bubble before the final event, each within 300 ms.
        Assert.All(displayTimeline, timelineEntry => Assert.True(
            (long)timelineEntry!["tMs"]! - (long)timelineEntry!["eventTMs"]! <= 300,
            $"bubble update at tMs {(long)timelineEntry!["tMs"]!} rendered slower than 300 ms after its event"));
        Assert.True(
            displayTimeline.Count(timelineEntry => (long)timelineEntry!["tMs"]! < lastEventTMs) >= 2,
            "the bubble did not keep streaming while the stream was still arriving");
    }
}
