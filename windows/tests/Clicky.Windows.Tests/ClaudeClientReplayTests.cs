using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clicky.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-027 (WS-16 part 2, audit G2/G3, parity spec CLA-06 and CLA-08): ClaudeClient's model wiring, TLS warm-up,
/// deadlines, connect retry and non-2xx handling. What the report shows runs through the real Clicky.exe
/// replay harness; the mid-request model change and the stalled server are driven in-process, the stall on a
/// FakeTimeProvider.
/// </summary>
[Collection(ClickyProcessReplayCollection.Name)]
public sealed class ClaudeClientReplayTests
{
    private const string OpusModel = "claude-opus-4-6";
    private static readonly Uri ChatEndpoint = new("http://replay.clicky.invalid/chat");

    [Fact]
    public async Task OpusOverrideSetsModel()
    {
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync("point-basic.sse", "--settings-override", $"selectedClaudeModel={OpusModel}");

        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");
        Assert.Equal(OpusModel, (string?)replayRun.Report["requestBody"]!["model"]);
        Assert.Equal(OpusModel, (string?)replayRun.Report["effectiveSettings"]!["selectedClaudeModel"]);
    }

    [Fact]
    public async Task InFlightKeepsModel()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;
        var firstAttemptArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentModels = new ConcurrentQueue<string>();
        var chatHandler = new ChatTestHandler(async (chatRequest, cancellationToken) =>
        {
            JsonNode sentBody = JsonNode.Parse(await chatRequest.Content!.ReadAsStringAsync(cancellationToken))!;
            sentModels.Enqueue((string)sentBody["model"]!);
            if (sentModels.Count == 1)
            {
                // The first attempt stays in flight until the test has changed the model, then drops its connect,
                // so the retry is sent after the change too.
                firstAttemptArrived.SetResult();
                await releaseFirstAttempt.Task.WaitAsync(cancellationToken);
                throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused");
            }
            return ChatTestHandler.EndOfStreamResponse();
        });
        var claudeClient = new ClaudeClient(chatHandler.CreateHttpClient(), ChatEndpoint, CompanionSettings.DefaultClaudeModel, TimeProvider.System);

        Task inFlightRequest = StreamToEndAsync(claudeClient, BuildRequestBody(claudeClient), testCancellationToken);
        await firstAttemptArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), testCancellationToken);
        // CLA-08: the user picks Opus in the panel while the request is in flight.
        claudeClient.Model = OpusModel;
        releaseFirstAttempt.SetResult();
        await inFlightRequest.WaitAsync(TimeSpan.FromSeconds(10), testCancellationToken);
        await StreamToEndAsync(claudeClient, BuildRequestBody(claudeClient), testCancellationToken);

        // Both attempts of the in-flight request carry the original model; only the next request uses Opus.
        Assert.Equal([CompanionSettings.DefaultClaudeModel, CompanionSettings.DefaultClaudeModel, OpusModel], sentModels);
    }

    [Fact]
    public async Task Http529FallsBack()
    {
        JsonNode chatRoute = JsonNode.Parse(File.ReadAllText(
            Path.Combine(ClickyReplayProcess.RepositoryRootDirectory, "shared", "fixtures", "parity", "chat-http-529.json")))!;
        ReplayRun replayRun = await RunManifestWithChatRouteAsync(chatRoute);
        JsonNode report = replayRun.Report;

        // CLA-06: the error is built from the status and the whole body (ClaudeAPI.swift:166-178).
        Assert.Contains($"API Error (529): {(string)chatRoute["body"]!}", (string?)report["error"] ?? "");
        Assert.Equal(TtsRequest.CreditsErrorFallbackUtterance, (string?)report["fallbackUtterances"]![0]);
        Assert.Contains("response_error", report["analyticsEvents"]!.AsArray().Select(analyticsEvent => (string?)analyticsEvent!["name"]));
        Assert.Empty(report["conversationHistory"]!.AsArray());
        Assert.Empty(report["ttsRequests"]!.AsArray());
        Assert.Equal(
            ["responding", "idle"],
            report["voiceStateTimeline"]!.AsArray().TakeLast(2).Select(timelineEntry => (string?)timelineEntry!["state"]));
    }

    [Fact]
    public async Task RetriesOnceOnConnectFailure()
    {
        string pointBasicFixturePath = Path.Combine(ClickyReplayProcess.RepositoryRootDirectory, "shared", "fixtures", "sse", "point-basic.sse");
        ReplayRun replayRun = await RunManifestWithChatRouteAsync(new JsonObject { ["bodyFile"] = pointBasicFixturePath, ["connectFailures"] = 1 });

        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");
        Assert.Null(replayRun.Report["error"]);
        Assert.False(string.IsNullOrEmpty((string?)replayRun.Report["spokenText"]));
        Assert.Equal(2, CountRequests(replayRun.Report, "POST", "/chat"));

        // A stalled Worker: headers never arrive, and the idle deadline fires on the virtual clock alone.
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;
        var fakeTimeProvider = new FakeTimeProvider();
        var stalledRequestArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalledHandler = new ChatTestHandler(async (_, cancellationToken) =>
        {
            stalledRequestArrived.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        });
        var stalledClaudeClient = new ClaudeClient(stalledHandler.CreateHttpClient(), ChatEndpoint, CompanionSettings.DefaultClaudeModel, fakeTimeProvider);
        Task stalledRequest = StreamToEndAsync(stalledClaudeClient, BuildRequestBody(stalledClaudeClient), testCancellationToken);
        await stalledRequestArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), testCancellationToken);

        fakeTimeProvider.Advance(ClaudeClient.RequestIdleTimeout - TimeSpan.FromMilliseconds(1));
        Assert.False(stalledRequest.IsCompleted, "the request ended before its deadline on the virtual clock");
        fakeTimeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => stalledRequest.WaitAsync(TimeSpan.FromSeconds(10), testCancellationToken));
    }

    [Fact]
    public async Task WarmupHeadSent()
    {
        ReplayRun replayRun = await ClickyReplayProcess.RunAsync("point-basic.sse");

        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");
        // ClaudeAPI.swift:76-95: one HEAD to the Worker host's root, and the chat request itself is sent once.
        JsonNode warmupRequest = Assert.Single(replayRun.Report["httpRequests"]!.AsArray(), httpRequest => (string?)httpRequest!["method"] == "HEAD")!;
        Assert.Equal("/", (string?)warmupRequest["path"]);
        Assert.Equal(1, CountRequests(replayRun.Report, "POST", "/chat"));
    }

    private static int CountRequests(JsonNode report, string method, string path) =>
        report["httpRequests"]!.AsArray().Count(httpRequest => (string?)httpRequest!["method"] == method && (string?)httpRequest["path"] == path);

    private static async Task<ReplayRun> RunManifestWithChatRouteAsync(JsonNode chatRoute)
    {
        string manifestPath = Path.Combine(Path.GetTempPath(), $"clicky-replay-manifest-{Guid.NewGuid():N}.json");
        File.WriteAllText(manifestPath, new JsonObject { ["routes"] = new JsonObject { ["/chat"] = chatRoute.DeepClone() } }.ToJsonString());
        try
        {
            return await ClickyReplayProcess.RunManifestAsync(manifestPath);
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    private static JsonObject BuildRequestBody(ClaudeClient claudeClient) =>
        claudeClient.BuildStreamingRequestBody("system prompt", [], [], "what is on my screen?");

    private static Task StreamToEndAsync(ClaudeClient claudeClient, JsonObject requestBody, CancellationToken cancellationToken) =>
        claudeClient.StreamResponseLinesAsync(requestBody, _ => Task.FromResult(true), cancellationToken);

    /// <summary>
    /// Answers /chat through the given function. The warm-up HEAD (sent by whichever client the test process
    /// creates first) is answered 200 and kept away from the function.
    /// </summary>
    private sealed class ChatTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answerChatRequest) : HttpMessageHandler
    {
        public static HttpResponseMessage EndOfStreamResponse() => new(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n") };

        // As in Program.cs: no HttpClient timeout, so only ClaudeClient's deadlines can end a request.
        public HttpClient CreateHttpClient() => new(this) { Timeout = Timeout.InfiniteTimeSpan };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.Method == HttpMethod.Head
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
                : answerChatRequest(request, cancellationToken);
    }
}
