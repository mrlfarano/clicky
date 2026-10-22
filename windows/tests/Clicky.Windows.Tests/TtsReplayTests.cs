using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-029 (WS-17 part 2): parity spec TTS-01, TTS-03, TTS-04 and the playback half of PTT-02. Every test runs the
/// real Clicky.exe against a manifest that maps /chat to point-basic.sse and /tts to a parity fixture. Replay
/// plays through the silent fake audio output, which lasts the MP3's real duration on the app's clock, so
/// these runs open no audio device; the WASAPI and System.Speech paths are live-mode evidence.
/// </summary>
[Collection(ClickyProcessReplayCollection.Name)]
public sealed class TtsReplayTests
{
    private const string CreditsErrorFallbackUtterance =
        "I'm all out of credits. Please DM Farza and tell him to bring me back to life.";

    private static readonly string ParityFixturesDirectory =
        Path.Combine(ClickyReplayProcess.RepositoryRootDirectory, "shared", "fixtures", "parity");

    [Fact]
    public async Task OkPlaysAfterFullDownload()
    {
        ReplayRun replayRun = await RunWithTtsRouteAsync(LoadParityRoute("tts-ok.json"));
        JsonNode report = replayRun.Report;
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");
        Assert.Null(report["error"]);

        JsonNode ttsRequest = Assert.Single(report["ttsRequests"]!.AsArray())!;
        Assert.Equal((string?)report["spokenText"], (string?)ttsRequest["text"]);
        Assert.Empty(report["fallbackUtterances"]!.AsArray());

        // The whole MP3 is handed to the output before playback starts (ElevenLabsTTSClient.swift:50, 65-67).
        long mp3ByteCount = new FileInfo(Path.Combine(ParityFixturesDirectory, "tts-ok.mp3")).Length;
        AssertLogOrder(replayRun.ProcessOutput, $"tts bytes={mp3ByteCount}", "tts playing", "tts playback ended");

        // processing until playback starts, responding while it plays, idle when it ends (TTS-01). The idle
        // after processing is point-basic's pointing flight (CompanionManager.swift:633-636).
        Assert.Equal(
            ["processing", "idle", "responding", "idle"],
            report["voiceStateTimeline"]!.AsArray().Select(timelineEntry => (string?)timelineEntry!["state"]));
    }

    [Fact]
    public async Task Http402SpeaksFallback()
    {
        ReplayRun replayRun = await RunWithTtsRouteAsync(LoadParityRoute("tts-http-402.json"));
        JsonNode report = replayRun.Report;
        // A TTS failure is spoken around, not an interaction failure (CompanionManager.swift:707-711).
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");
        Assert.Null(report["error"]);

        Assert.Equal(
            [CreditsErrorFallbackUtterance],
            report["fallbackUtterances"]!.AsArray().Select(fallbackUtterance => (string?)fallbackUtterance));
        Assert.Contains(report["analyticsEvents"]!.AsArray(), analyticsEvent => (string?)analyticsEvent!["name"] == "tts_error");
        AssertLogOrder(replayRun.ProcessOutput, "tts failed: TTS API error (402)", "fallback voice spoken");
        Assert.DoesNotContain("tts playing", replayRun.ProcessOutput);

        // The fallback voice is a response too (CompanionManager.swift:765), and the reply ends with it.
        Assert.Equal(
            ["processing", "idle", "responding", "idle"],
            report["voiceStateTimeline"]!.AsArray().Select(timelineEntry => (string?)timelineEntry!["state"]));
    }

    [Fact]
    public async Task RepressStopsPlayback()
    {
        // The press at 300 ms cancels the first exchange while point-basic (200 ms per event) still streams;
        // the release at 400 ms submits again, and that exchange's ~12.5 s of audio is still playing when the
        // press at 7000 ms lands.
        ReplayRun replayRun = await RunWithTtsRouteAsync(
            StageRepeatedTtsOkRoute(repeatCount: 48),
            """{ "events": [ { "tMs": 300, "type": "press" }, { "tMs": 400, "type": "release" }, { "tMs": 7000, "type": "press" } ] }""",
            "--event-delay-ms", "200");
        JsonNode report = replayRun.Report;
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");
        Assert.Null(report["error"]);

        // TTS-04: the press stops playback at once, and the stopped audio never reports its natural end.
        AssertLogOrder(replayRun.ProcessOutput, "tts playing", "script: pressed delivered at 7000 ms", "tts stopped (preempted)");
        Assert.DoesNotContain("tts playback ended", replayRun.ProcessOutput);

        // PTT-02a: the exchange cancelled while streaming asked for no speech; the one request is the second
        // exchange's, whose playback the later press stopped.
        Assert.Contains("interaction cancelled by a newer press", replayRun.ProcessOutput);
        JsonNode ttsRequest = Assert.Single(report["ttsRequests"]!.AsArray())!;
        Assert.Equal((string?)report["spokenText"], (string?)ttsRequest["text"]);

        // The press came while responding, and no TtsEnded idle slipped in before the new recording.
        Assert.Equal(
            ["responding", "processing", "listening"],
            report["voiceStateTimeline"]!.AsArray().TakeLast(3).Select(timelineEntry => (string?)timelineEntry!["state"]));
    }

    [Fact]
    public async Task PlaybackStoppedSignalsTtsEnded()
    {
        // ~2.1 s of audio: long enough that an idle at the end of it can only come from the fake output's
        // PlaybackStopped, not from the speech request itself.
        JsonObject ttsRoute = StageRepeatedTtsOkRoute(repeatCount: 8);
        TimeSpan playbackDuration = SilentReplayAudioOutput.MeasureMp3Duration(File.ReadAllBytes((string)ttsRoute["bodyFile"]!));
        ReplayRun replayRun = await RunWithTtsRouteAsync(ttsRoute);
        JsonNode report = replayRun.Report;
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        JsonArray voiceStateTimeline = report["voiceStateTimeline"]!.AsArray();
        JsonNode respondingEntry = voiceStateTimeline[^2]!;
        JsonNode idleEntry = voiceStateTimeline[^1]!;
        Assert.Equal("responding", (string?)respondingEntry["state"]);
        Assert.Equal("idle", (string?)idleEntry["state"]);
        Assert.Contains("tts playback ended", replayRun.ProcessOutput);

        // The app's TimeProvider never fires a delay early; the slack covers the few ms between Play and the
        // TtsStarted stamp.
        long respondingForMs = (long)idleEntry["tMs"]! - (long)respondingEntry["tMs"]!;
        Assert.True(
            respondingForMs >= playbackDuration.TotalMilliseconds - 50,
            $"responding lasted {respondingForMs} ms for {playbackDuration.TotalMilliseconds} ms of audio");
    }

    /// <summary>A parity fixture is a manifest route object; its bodyFile is made absolute so a temp manifest can use it.</summary>
    private static JsonObject LoadParityRoute(string fixtureFileName)
    {
        JsonObject ttsRoute = JsonNode.Parse(File.ReadAllText(Path.Combine(ParityFixturesDirectory, fixtureFileName)))!.AsObject();
        if (ttsRoute["bodyFile"] is JsonNode bodyFile)
        {
            ttsRoute["bodyFile"] = Path.Combine(ParityFixturesDirectory, (string)bodyFile!);
        }
        return ttsRoute;
    }

    /// <summary>
    /// tts-ok.mp3 is bare MPEG frames with no ID3 tag, so back-to-back copies are a valid, longer MP3. The test
    /// stages it in the temp directory rather than committing a large fixture.
    /// </summary>
    private static JsonObject StageRepeatedTtsOkRoute(int repeatCount)
    {
        byte[] ttsOkBytes = File.ReadAllBytes(Path.Combine(ParityFixturesDirectory, "tts-ok.mp3"));
        string stagedMp3Path = Path.Combine(Path.GetTempPath(), $"clicky-tts-long-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(stagedMp3Path, Enumerable.Repeat(ttsOkBytes, repeatCount).SelectMany(mp3Bytes => mp3Bytes).ToArray());
        return new JsonObject { ["status"] = 200, ["bodyFile"] = stagedMp3Path };
    }

    private static async Task<ReplayRun> RunWithTtsRouteAsync(JsonObject ttsRoute, string? scriptJson = null, params string[] extraArguments)
    {
        string pointBasicFixturePath = Path.Combine(ClickyReplayProcess.RepositoryRootDirectory, "shared", "fixtures", "sse", "point-basic.sse");
        var manifest = new JsonObject
        {
            ["routes"] = new JsonObject
            {
                ["/chat"] = new JsonObject { ["bodyFile"] = pointBasicFixturePath },
                ["/tts"] = ttsRoute,
            },
        };
        string manifestPath = Path.Combine(Path.GetTempPath(), $"clicky-replay-manifest-{Guid.NewGuid():N}.json");
        string scriptPath = Path.Combine(Path.GetTempPath(), $"clicky-replay-script-{Guid.NewGuid():N}.json");
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        try
        {
            if (scriptJson is null)
            {
                return await ClickyReplayProcess.RunManifestAsync(manifestPath, extraArguments);
            }
            File.WriteAllText(scriptPath, scriptJson);
            return await ClickyReplayProcess.RunManifestAsync(manifestPath, ["--script", scriptPath, .. extraArguments]);
        }
        finally
        {
            File.Delete(manifestPath);
            File.Delete(scriptPath);
            string ttsBodyFilePath = (string?)ttsRoute["bodyFile"] ?? "";
            if (ttsBodyFilePath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(ttsBodyFilePath);
            }
        }
    }

    private static void AssertLogOrder(string processOutput, params string[] expectedLogFragments)
    {
        int searchFromIndex = 0;
        foreach (string expectedLogFragment in expectedLogFragments)
        {
            int fragmentIndex = processOutput.IndexOf(expectedLogFragment, searchFromIndex, StringComparison.Ordinal);
            Assert.True(fragmentIndex >= 0, $"'{expectedLogFragment}' not logged after index {searchFromIndex}. Output:\n{processOutput}");
            searchFromIndex = fragmentIndex + expectedLogFragment.Length;
        }
    }
}
