using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// Parity spec ONB-02 (task T-040): the onboarding demo interaction, replayed with
/// --debug-trigger-onboarding-demo against shared/fixtures/sse/onboarding-demo.sse (a reply with a
/// coordinate) and onboarding-demo-no-coordinate.sse (a reply without one).
/// </summary>
[Collection(ClickyProcessReplayCollection.Name)]
public sealed class OnboardingDemoReplayTests
{
    private const string OnboardingDemoUserPromptText = "look around my screen and find something interesting to point at";

    [Fact]
    public async Task DemoSendsOneImageAndPointsAtCursorScreen()
    {
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync("onboarding-demo.sse", "--debug-trigger-onboarding-demo");
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        // Only the cursor's monitor is captured and sent, so the request carries exactly its one image.
        JsonArray contentBlocks = replayRun.Report["requestBody"]!["messages"]![0]!["content"]!.AsArray();
        Assert.True(
            contentBlocks.Count(contentBlock => (string?)contentBlock!["type"] == "image") == 1,
            $"expected one image; captureErrors: {replayRun.Report["captureErrors"]!.ToJsonString()}");

        JsonNode point = replayRun.Report["point"]!;
        Assert.Equal(0, (int)point["screenIndex"]!);
        Assert.Equal("readme.md", (string?)point["label"]);
        Assert.Equal("that readme looks well loved", (string?)replayRun.Report["spokenText"]);
    }

    [Fact]
    public async Task NoCoordinateDoesNothing()
    {
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync("onboarding-demo-no-coordinate.sse", "--debug-trigger-onboarding-demo");
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        Assert.Null(replayRun.Report["point"]);
    }

    [Fact]
    public async Task DemoFiresAnalyticsEventWithFixedUserText()
    {
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync("onboarding-demo.sse", "--debug-trigger-onboarding-demo");
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        Assert.Contains(replayRun.Report["analyticsEvents"]!.AsArray(),
            analyticsEvent => (string?)analyticsEvent!["name"] == "onboarding_demo_triggered");
        JsonArray contentBlocks = replayRun.Report["requestBody"]!["messages"]![0]!["content"]!.AsArray();
        Assert.Equal(OnboardingDemoUserPromptText, (string?)contentBlocks[^1]!["text"]);
    }
}
