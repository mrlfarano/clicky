using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// WS-04 part 1: --replay-manifest routing (task T-001). A manifest maps Worker routes to recorded
/// responses; routes it does not map fail the run instead of reaching the network, and every replay-path
/// timer runs on an injected TimeProvider so tests fire it by advancing a fake clock.
/// </summary>
[Collection(ClickyProcessReplayCollection.Name)]
public sealed class ReplayManifestTests
{
    private const string PointBasicSpokenText = "that's the save button up in the toolbar. click it and you're done.";

    [Fact]
    public async Task ChatRouteServesSse()
    {
        string pointBasicFixturePath = Path.Combine(
            ClickyReplayProcess.RepositoryRootDirectory, "shared", "fixtures", "sse", "point-basic.sse");
        string manifestPath = WriteTemporaryManifest($$"""
            {
              "routes": {
                "/chat": { "bodyFile": {{JsonSerializer.Serialize(pointBasicFixturePath)}} }
              }
            }
            """);

        ReplayRun replayRun;
        try
        {
            replayRun = await ClickyReplayProcess.RunManifestAsync(manifestPath);
        }
        finally
        {
            File.Delete(manifestPath);
        }

        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");
        Assert.Null((string?)replayRun.Report["error"]);
        Assert.Equal(PointBasicSpokenText, (string?)replayRun.Report["spokenText"]);
    }

    [Fact]
    public async Task BadManifestExitsTwo()
    {
        string manifestPath = WriteTemporaryManifest("{ \"routes\": this is not valid json ]]");
        ClickyStartupFailureRun clickyProcessRun;
        try
        {
            clickyProcessRun = await ClickyReplayProcess.RunArgumentsAsync("--replay-manifest", manifestPath);
        }
        finally
        {
            File.Delete(manifestPath);
        }

        Assert.Equal(2, clickyProcessRun.ExitCode);
        // Exit 2 must come from the startup-error path, not from a crash with the same code by coincidence.
        Assert.Contains("startup error", clickyProcessRun.ProcessOutput);
    }

    [Fact]
    public async Task UnmappedRouteFailsClosed()
    {
        // Deliberately maps only /tts: the app's /chat request has no route, so the run must fail closed.
        string manifestPath = WriteTemporaryManifest("""
            {
              "routes": {
                "/tts": { "status": 200, "body": "{}" }
              }
            }
            """);

        ReplayRun replayRun;
        try
        {
            replayRun = await ClickyReplayProcess.RunManifestAsync(manifestPath);
        }
        finally
        {
            File.Delete(manifestPath);
        }

        Assert.Equal(1, replayRun.ExitCode);
        Assert.Equal(1, (int)replayRun.Report["exitCode"]!);
        string interactionError = (string?)replayRun.Report["error"] ?? "";
        Assert.Contains("/chat", interactionError);
        Assert.Contains("fails closed", interactionError);

        // No socket was opened: replay addresses the reserved replay.clicky.invalid host, so a live attempt
        // would die in DNS with a SocketException ("No such host is known") — the error above is instead the
        // manifest handler's own refusal, which proves the request never left the process.
        Assert.DoesNotContain("SocketException", replayRun.ProcessOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No such host", replayRun.ProcessOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TimersUseInjectedClock()
    {
        var fakeTimeProvider = new FakeTimeProvider();
        string pointBasicFixturePath = Path.Combine(
            ClickyReplayProcess.RepositoryRootDirectory, "shared", "fixtures", "sse", "point-basic.sse");
        // The shell's SSE pacing (and its linger) go through the injected TimeProvider, so constructing the
        // handler with a fake clock here is the same wiring Clicky.exe uses with TimeProvider.System.
        using var replayHttpClient = new HttpClient(new ReplayHttpMessageHandler(
            ReplayManifest.CreateForSingleChatFixture(pointBasicFixturePath),
            delayBeforeEachSseEvent: TimeSpan.FromSeconds(1.4),
            timeProvider: fakeTimeProvider));

        using var chatRequest = new HttpRequestMessage(HttpMethod.Post, "http://replay.clicky.invalid/chat");
        using HttpResponseMessage chatResponse = await replayHttpClient.SendAsync(
            chatRequest, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, chatResponse.StatusCode);
        await using Stream replaySseStream = await chatResponse.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        var wallClockElapsedSinceArm = Stopwatch.StartNew();
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;
        byte[] sseEventReadBuffer = new byte[64 * 1024];
        ValueTask<int> firstSseEventRead = replaySseStream.ReadAsync(sseEventReadBuffer, testCancellationToken);

        // While wall time runs, the 1.4 s pacing timer stays armed and the read stays pending...
        await Task.Delay(TimeSpan.FromMilliseconds(200), testCancellationToken);
        Assert.False(firstSseEventRead.IsCompleted, "the SSE pacing delay fired without the injected clock advancing");

        // ...and only advancing the fake clock by the armed delay delivers the event, with no wall-clock wait.
        fakeTimeProvider.Advance(TimeSpan.FromSeconds(1.4));
        int firstSseEventByteCount = await firstSseEventRead.AsTask().WaitAsync(TimeSpan.FromSeconds(5), testCancellationToken);
        Assert.True(firstSseEventByteCount > 0, "advancing the fake clock delivered no SSE event");
        Assert.True(
            wallClockElapsedSinceArm.Elapsed < TimeSpan.FromSeconds(1.4),
            $"the timer fired on the fake clock but {wallClockElapsedSinceArm.ElapsedMilliseconds} ms of wall clock elapsed anyway");
    }

    /// <summary>Manifests reference fixtures from wherever the test staged them, so each test gets its own file.</summary>
    private static string WriteTemporaryManifest(string manifestJson)
    {
        string manifestPath = Path.Combine(Path.GetTempPath(), $"clicky-replay-manifest-{Guid.NewGuid():N}.json");
        File.WriteAllText(manifestPath, manifestJson);
        return manifestPath;
    }
}
