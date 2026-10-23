using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Clicky.Core;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-044 (WS-24 part 2): the live PostHog sink, in process against a fake handler so nothing reaches
/// posthog.com. It is fire-and-forget like PostHogSDK.capture (AN-02 relies on analytics never changing what
/// the app does), and it sends under the settings analyticsDistinctId until identify, then the email (AN-01).
/// </summary>
public sealed class AnalyticsSinkTests
{
    private static readonly TimeSpan HandlerWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly Uri PostHogHost = new("https://us.i.posthog.com");

    [Fact]
    public async Task FailureIsDroppedAndNeverBlocks()
    {
        var failingHandler = new GatedFailingHttpMessageHandler();
        var droppedCaptureLogLines = new ConcurrentQueue<string>();
        var droppedCaptureLogged = new SemaphoreSlim(0);
        var postHogAnalyticsSink = new PostHogAnalyticsSink(
            new HttpClient(failingHandler), PostHogHost, "phc_test", "settings-distinct-id",
            logLine =>
            {
                droppedCaptureLogLines.Enqueue(logLine);
                droppedCaptureLogged.Release();
            });
        var analyticsEvents = new AnalyticsEvents();

        // The handler holds the first capture until released, so every call below returns while a send is stuck:
        // a caller that waited on the network would never get past them.
        postHogAnalyticsSink.TrackEvent(analyticsEvents.PushToTalkStarted());
        Assert.True(await failingHandler.RequestReceived.WaitAsync(HandlerWaitTimeout, TestContext.Current.CancellationToken));
        postHogAnalyticsSink.TrackEvent(analyticsEvents.PushToTalkReleased());
        postHogAnalyticsSink.TrackEvent(analyticsEvents.UserMessageSent("click save"));
        Assert.False(failingHandler.ReleaseGate.Task.IsCompleted);

        // Released, every send throws; each failure is logged and dropped and the queue keeps draining.
        failingHandler.ReleaseGate.SetResult();
        for (int droppedCaptureIndex = 0; droppedCaptureIndex < 3; droppedCaptureIndex++)
        {
            Assert.True(await droppedCaptureLogged.WaitAsync(HandlerWaitTimeout, TestContext.Current.CancellationToken));
        }
        Assert.Equal(
            [
                "analytics: push_to_talk_started dropped (HttpRequestException)",
                "analytics: push_to_talk_released dropped (HttpRequestException)",
                // The event name only: the transcript never reaches the log (WS-11).
                "analytics: user_message_sent dropped (HttpRequestException)",
            ],
            droppedCaptureLogLines);
        Assert.Equal(
            ["push_to_talk_started", "push_to_talk_released", "user_message_sent"],
            postHogAnalyticsSink.RecordedEvents.Select(recordedEvent => recordedEvent.Name));
    }

    [Fact]
    public async Task UsesAnalyticsDistinctIdUntilIdentify()
    {
        var capturingHandler = new CapturingHttpMessageHandler();
        var postHogAnalyticsSink = new PostHogAnalyticsSink(
            new HttpClient(capturingHandler), PostHogHost, "phc_test", "settings-distinct-id", _ => { });
        var analyticsEvents = new AnalyticsEvents();

        postHogAnalyticsSink.TrackEvent(analyticsEvents.PushToTalkStarted());
        (Uri captureUri, JsonNode captureBeforeIdentify) = await ReadNextCaptureAsync(capturingHandler);
        Assert.Equal("https://us.i.posthog.com/i/v0/e/", captureUri.AbsoluteUri);
        Assert.Equal("phc_test", (string?)captureBeforeIdentify["api_key"]);
        Assert.Equal("push_to_talk_started", (string?)captureBeforeIdentify["event"]);
        Assert.Equal("settings-distinct-id", (string?)captureBeforeIdentify["distinct_id"]);

        // posthog-ios's identify: an $identify under the email, linked to the anonymous id, with {email} as $set.
        postHogAnalyticsSink.Identify(analyticsEvents.Identify("ada@example.com"));
        (_, JsonNode identifyCapture) = await ReadNextCaptureAsync(capturingHandler);
        Assert.Equal("$identify", (string?)identifyCapture["event"]);
        Assert.Equal("ada@example.com", (string?)identifyCapture["distinct_id"]);
        Assert.Equal("settings-distinct-id", (string?)identifyCapture["properties"]!["$anon_distinct_id"]);
        Assert.Equal("ada@example.com", (string?)identifyCapture["properties"]!["$set"]!["email"]);

        postHogAnalyticsSink.TrackEvent(analyticsEvents.PushToTalkReleased());
        (_, JsonNode captureAfterIdentify) = await ReadNextCaptureAsync(capturingHandler);
        Assert.Equal("push_to_talk_released", (string?)captureAfterIdentify["event"]);
        Assert.Equal("ada@example.com", (string?)captureAfterIdentify["distinct_id"]);
    }

    private static async Task<(Uri CaptureUri, JsonNode CaptureBody)> ReadNextCaptureAsync(CapturingHttpMessageHandler capturingHandler) =>
        await capturingHandler.ReceivedCaptures.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(HandlerWaitTimeout);

    /// <summary>Holds every request until <see cref="ReleaseGate"/> completes, then fails it as an unreachable host would.</summary>
    private sealed class GatedFailingHttpMessageHandler : HttpMessageHandler
    {
        public TaskCompletionSource ReleaseGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SemaphoreSlim RequestReceived { get; } = new(0);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestReceived.Release();
            await ReleaseGate.Task;
            throw new HttpRequestException("us.i.posthog.com is unreachable");
        }
    }

    /// <summary>Answers 200 to every capture and hands its URL and parsed JSON body to the test.</summary>
    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        public Channel<(Uri CaptureUri, JsonNode CaptureBody)> ReceivedCaptures { get; } = Channel.CreateUnbounded<(Uri, JsonNode)>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            JsonNode captureBody = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            ReceivedCaptures.Writer.TryWrite((request.RequestUri!, captureBody));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
