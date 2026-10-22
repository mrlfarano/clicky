using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// WS-04 part 2: the parity-harness flags --settings-override, --script and --debug-trigger-onboarding-demo
/// (task T-002), and the phase-6 report fields they land with: voiceStateTimeline, analyticsEvents,
/// ttsRequests and fallbackUtterances, populated by the in-memory stub sinks (the real TTS, fallback and
/// PostHog sinks are tasks T-029 and T-044). Every test runs the real Clicky.exe, as in ReplayManifestTests.
/// </summary>
[Collection(ClickyProcessReplayCollection.Name)]
public sealed class ReplayHarnessFlagsTests
{
    /// <summary>
    /// One timer tick on the delivery clock, plus scheduling slack: the scripted-press timer goes through
    /// the app's TimeProvider, which never fires a delay early, so the tolerance is all on the late side.
    /// </summary>
    private const int ScriptedEventDeliveryToleranceMs = 100;

    private const string OnboardingDemoUserPromptText = "look around my screen and find something interesting to point at";

    [Fact]
    public async Task SettingsOverrideAppliesToRequest()
    {
        // Every key this test asserts on is overridden, so the result cannot depend on a settings.json
        // happening to exist in the launching machine's real data directory (the app loads it, T-018
        // writes it). Multiple pairs also exercise the flag's comma-separated key=value,... format.
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync(
            "point-basic.sse",
            "--settings-override", "selectedClaudeModel=claude-opus-4-6,isClickyCursorEnabled=true,hasCompletedOnboarding=false");
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        // The override is visible in the report's effective-settings section...
        JsonNode effectiveSettings = replayRun.Report["effectiveSettings"]!;
        Assert.Equal("claude-opus-4-6", (string?)effectiveSettings["selectedClaudeModel"]);
        Assert.True((bool)effectiveSettings["isClickyCursorEnabled"]!);
        Assert.False((bool)effectiveSettings["hasCompletedOnboarding"]!);
        // ...and it reaches the request, as parity spec CLA-08 relies on (requestBody.model).
        Assert.Equal("claude-opus-4-6", (string?)replayRun.Report["requestBody"]!["model"]!);
    }

    [Fact]
    public async Task ScriptedPressIsDelivered()
    {
        string scriptPath = WriteTemporaryScript("""{ "events": [ { "tMs": 300, "type": "press" } ] }""");
        ReplayRun replayRun;
        try
        {
            replayRun = await ClickyReplayProcess.RunAsync("point-basic.sse", "--script", scriptPath);
        }
        finally
        {
            File.Delete(scriptPath);
        }
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        // The press lands in voiceStateTimeline at its scripted time on the virtual clock (the same
        // TimeProvider that paces the SSE stream). A press enters "listening" once recording starts (PTT-01),
        // and with nothing scripted after it, that is where the run ends; the entries before it are the
        // tracer exchange's own, CoordinatorReplayTests.VoiceStateTimelineRecorded pins those.
        JsonNode timelineEntry = replayRun.Report["voiceStateTimeline"]!.AsArray()[^1]!;
        Assert.Equal("listening", (string?)timelineEntry!["state"]);
        long deliveredTMs = (long)timelineEntry["tMs"]!;
        Assert.InRange(deliveredTMs, 300, 300 + ScriptedEventDeliveryToleranceMs);
    }

    [Fact]
    public async Task ReportHasNewFields()
    {
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync("point-basic.sse");
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        // Every report carries them as arrays, so the parity scenarios can assert on them without a schema
        // special case. Since T-012 the coordinator fills the first three on a plain replay; nothing asks for
        // a fallback utterance until TTS can fail (T-029).
        foreach (string reportFieldName in new[] { "voiceStateTimeline", "analyticsEvents", "ttsRequests", "fallbackUtterances" })
        {
            Assert.True(replayRun.Report[reportFieldName] is not null, $"report field '{reportFieldName}' is missing");
            Assert.Equal(JsonValueKind.Array, replayRun.Report[reportFieldName]!.GetValueKind());
        }
        Assert.Empty(replayRun.Report["fallbackUtterances"]!.AsArray());
    }

    [Fact]
    public async Task SinksAreStubbed()
    {
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync("point-basic.sse");
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");
        Assert.Null((string?)replayRun.Report["error"]);

        // No outbound request to posthog.com: replay's only HTTP handler is the fail-closed manifest
        // handler, so an attempted POST would have failed the run instead of exiting zero, and the
        // in-memory analytics sink records events rather than sending them anywhere. What it recorded is one
        // full exchange's events in reducer order (AN-01).
        Assert.DoesNotContain("posthog.com", replayRun.ProcessOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["user_message_sent", "element_pointed", "ai_response_received"],
            replayRun.Report["analyticsEvents"]!.AsArray().Select(analyticsEvent => (string?)analyticsEvent!["name"]));

        // No audio device is opened: the TTS sink records the one text it was asked to speak and synthesizes
        // nothing, and the fallback sink is never asked, so the run performs no audio activity at all.
        JsonNode ttsRequest = Assert.Single(replayRun.Report["ttsRequests"]!.AsArray())!;
        Assert.Equal((string?)replayRun.Report["spokenText"], (string?)ttsRequest!["text"]);
        Assert.Empty(replayRun.Report["fallbackUtterances"]!.AsArray());
    }

    [Fact]
    public async Task DebugTriggerOnboardingDemoRunsTheDemoInteraction()
    {
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync("point-basic.sse", "--debug-trigger-onboarding-demo");
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        // ONB-02: the demo interaction uses the onboarding-demo system prompt and its fixed user text
        // instead of the tracer prompt.
        string sharedOnboardingDemoPrompt = File.ReadAllText(
            Path.Combine(ClickyReplayProcess.RepositoryRootDirectory, "shared", "prompts", "onboarding-demo.txt"));
        JsonNode requestBody = replayRun.Report["requestBody"]!;
        Assert.Equal(sharedOnboardingDemoPrompt, (string?)requestBody["system"]);
        JsonArray contentBlocks = requestBody["messages"]![0]!["content"]!.AsArray();
        Assert.Equal(OnboardingDemoUserPromptText, (string?)contentBlocks[^1]!["text"]);

        // ONB-02: only the cursor's monitor is captured and sent, so the point lands on screen index 0.
        JsonArray monitors = replayRun.Report["monitors"]!.AsArray();
        JsonNode cursorMonitor = Assert.Single(monitors)!;
        Assert.True((bool)cursorMonitor!["isCursorMonitor"]!);
        Assert.Equal(0, (int)replayRun.Report["point"]!["screenIndex"]!);
        int capturedMonitorCount = monitors.Count(monitor => monitor!["nonBlackPixelRatio"] is not null);
        int sentImageCount = contentBlocks.Count(contentBlock => (string?)contentBlock!["type"] == "image");
        Assert.Equal(capturedMonitorCount, sentImageCount);
        Assert.True(sentImageCount <= 1, "the demo request must carry at most the cursor monitor's image");

        // The demo fires its own analytics event (AN-01), recorded by the stub sink into the report.
        Assert.Contains(replayRun.Report["analyticsEvents"]!.AsArray(),
            analyticsEvent => (string?)analyticsEvent!["name"] == "onboarding_demo_triggered");
    }

    [Fact]
    public async Task UnknownSettingsOverrideKeyExitsTwo()
    {
        string pointBasicFixturePath = Path.Combine(
            ClickyReplayProcess.RepositoryRootDirectory, "shared", "fixtures", "sse", "point-basic.sse");
        ClickyStartupFailureRun clickyProcessRun = await ClickyReplayProcess.RunArgumentsAsync(
            "--replay", pointBasicFixturePath, "--settings-override", "definitelyNotASetting=value");

        Assert.Equal(2, clickyProcessRun.ExitCode);
        // Exit 2 must come from the startup-error path, not from a crash with the same code by coincidence.
        Assert.Contains("startup error", clickyProcessRun.ProcessOutput);
        Assert.Contains("Unknown settings key", clickyProcessRun.ProcessOutput);
    }

    [Fact]
    public async Task InvalidScriptExitsTwo()
    {
        string pointBasicFixturePath = Path.Combine(
            ClickyReplayProcess.RepositoryRootDirectory, "shared", "fixtures", "sse", "point-basic.sse");
        string scriptPath = WriteTemporaryScript("""{ "events": [ { "tMs": 300, "type": "shout" } ] }""");
        ClickyStartupFailureRun clickyProcessRun;
        try
        {
            clickyProcessRun = await ClickyReplayProcess.RunArgumentsAsync(
                "--replay", pointBasicFixturePath, "--script", scriptPath);
        }
        finally
        {
            File.Delete(scriptPath);
        }

        Assert.Equal(2, clickyProcessRun.ExitCode);
        Assert.Contains("startup error", clickyProcessRun.ProcessOutput);
        Assert.Contains("'press' or 'release'", clickyProcessRun.ProcessOutput);
    }

    /// <summary>Scripts are staged per test, like manifests, so each test gets its own file.</summary>
    private static string WriteTemporaryScript(string scriptJson)
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), $"clicky-replay-script-{Guid.NewGuid():N}.json");
        File.WriteAllText(scriptPath, scriptJson);
        return scriptPath;
    }
}
