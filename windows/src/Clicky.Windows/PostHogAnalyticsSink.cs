using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// The live analytics sink (T-044, WS-24). Fire-and-forget like PostHogSDK.shared.capture, which
/// ClickyAnalytics.swift:27-120 calls without awaiting or handling errors: TrackEvent only queues, a
/// background task POSTs one event at a time, and a failed POST is logged and dropped, never retried and never
/// surfaced to the caller (AN-02's funnels assume analytics cannot change what the app does).
/// Direct POST to PostHog's capture endpoint on the app's shared HttpClient rather than the PostHog .NET
/// package: capture is one JSON body per event, and the SDK would bring its own HttpClient, flush timers and
/// on-disk queue for that.
/// </summary>
internal sealed class PostHogAnalyticsSink : IAnalyticsSink
{
    // Bounded so an offline session cannot grow without limit; when full the oldest unsent event is dropped.
    private const int MaximumPendingCaptureCount = 1000;

    // The shared HttpClient has no timeout (ClaudeClient keeps its own deadlines), so a hung capture would
    // otherwise stall every capture queued behind it.
    private static readonly TimeSpan CaptureRequestTimeout = TimeSpan.FromSeconds(10);

    private readonly Channel<JsonObject> pendingCaptures = Channel.CreateBounded<JsonObject>(
        new BoundedChannelOptions(MaximumPendingCaptureCount) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly List<AnalyticsEventReport> recordedEvents = [];
    private readonly HttpClient httpClient;
    private readonly Uri captureEndpoint;
    private readonly string projectApiKey;
    private readonly Action<string> writeLogLine;

    /// <summary>The settings analyticsDistinctId until <see cref="Identify"/>, then the email (AN-01 identify row).</summary>
    private string currentDistinctId;

    public PostHogAnalyticsSink(HttpClient httpClient, Uri postHogHost, string projectApiKey, string analyticsDistinctId, Action<string> writeLogLine)
    {
        this.httpClient = httpClient;
        // /i/v0/e/ is the capture route posthog-ios posts to; appending keeps any path prefix in the host URL.
        captureEndpoint = new Uri(postHogHost.AbsoluteUri.TrimEnd('/') + "/i/v0/e/");
        this.projectApiKey = projectApiKey;
        currentDistinctId = analyticsDistinctId;
        this.writeLogLine = writeLogLine;
        _ = Task.Run(SendPendingCapturesAsync);
    }

    public IReadOnlyList<AnalyticsEventReport> RecordedEvents => recordedEvents;

    public void TrackEvent(AnalyticsEvent analyticsEvent)
    {
        recordedEvents.Add(AnalyticsEventReport.FromAnalyticsEvent(analyticsEvent));
        EnqueueCapture(analyticsEvent.EventName, (JsonObject)analyticsEvent.EventProperties.DeepClone());
    }

    /// <summary>
    /// CompanionManager.swift:161-163 calls PostHogSDK.identify(email, userProperties: ["email": email]), which
    /// posthog-ios sends as an $identify capture under the new id with the old one as $anon_distinct_id (so
    /// PostHog merges the anonymous history into the person) and the properties as $set.
    /// </summary>
    public void Identify(AnalyticsIdentify analyticsIdentify)
    {
        string anonymousDistinctId = currentDistinctId;
        currentDistinctId = analyticsIdentify.DistinctId;
        EnqueueCapture("$identify", new JsonObject
        {
            ["$anon_distinct_id"] = anonymousDistinctId,
            ["$set"] = analyticsIdentify.PersonProperties.DeepClone(),
        });
    }

    // The distinct id is stamped here, at track time, so an identify never relabels events queued before it.
    private void EnqueueCapture(string eventName, JsonObject eventProperties) =>
        pendingCaptures.Writer.TryWrite(new JsonObject
        {
            ["api_key"] = projectApiKey,
            ["event"] = eventName,
            ["distinct_id"] = currentDistinctId,
            ["properties"] = eventProperties,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        });

    private async Task SendPendingCapturesAsync()
    {
        await foreach (JsonObject pendingCapture in pendingCaptures.Reader.ReadAllAsync())
        {
            // The event name only: properties carry transcript and response text, which the log must never hold (WS-11).
            string eventName = (string)pendingCapture["event"]!;
            try
            {
                using var captureTimeout = new CancellationTokenSource(CaptureRequestTimeout);
                using HttpResponseMessage captureResponse = await httpClient.PostAsJsonAsync(captureEndpoint, pendingCapture, captureTimeout.Token);
                if (!captureResponse.IsSuccessStatusCode)
                {
                    writeLogLine($"analytics: {eventName} dropped (HTTP {(int)captureResponse.StatusCode})");
                }
            }
            catch (Exception captureException)
            {
                writeLogLine($"analytics: {eventName} dropped ({captureException.GetType().Name})");
            }
        }
    }
}
