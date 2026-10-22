using System.Text;
using System.Text.Json.Nodes;
using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// The Worker /chat client, ported from ClaudeAPI.swift (WS-16, audit G2/G3): the selected model, a TLS
/// warm-up HEAD at creation, URLSession's timeouts as deadlines on the injected TimeProvider, one retry on
/// connect failure in place of waitsForConnectivity, and the non-2xx error. Decides nothing about the
/// session: a failure is thrown to the runner, which records it for the coordinator's reducer.
/// </summary>
internal sealed class ClaudeClient
{
    /// <summary>ClaudeAPI.swift:26, 42: URLSession's request timeout is an idle timeout, re-armed whenever data arrives.</summary>
    public static readonly TimeSpan RequestIdleTimeout = TimeSpan.FromSeconds(120);

    /// <summary>ClaudeAPI.swift:27: the deadline for the whole request, stream included.</summary>
    public static readonly TimeSpan ResourceTimeout = TimeSpan.FromSeconds(300);

    /// <summary>ClaudeAPI.swift:92.</summary>
    private static readonly TimeSpan TlsWarmupTimeout = TimeSpan.FromSeconds(10);

    // ClaudeAPI.swift:10-11, 67-74: one warm-up per process however many clients are created; Interlocked
    // stands in for the NSLock-guarded flag.
    private static int hasStartedTlsWarmup;

    private readonly HttpClient workerHttpClient;
    private readonly Uri chatEndpoint;
    private readonly TimeProvider timeProvider;

    public ClaudeClient(HttpClient workerHttpClient, Uri chatEndpoint, string model, TimeProvider timeProvider)
    {
        this.workerHttpClient = workerHttpClient;
        this.chatEndpoint = chatEndpoint;
        this.timeProvider = timeProvider;
        Model = model;
        // ClaudeAPI.swift:33-36: pre-establish the TLS session so the first real request, which carries the
        // large image payload, skips the cold handshake.
        _ = WarmUpTlsConnectionIfNeededAsync();
    }

    /// <summary>
    /// The model the next request is built with (ClaudeAPI.swift:14): the effective user setting, set again
    /// when the setting changes. A request in flight keeps its model (CLA-08), because its body is built once
    /// and every attempt resends that same body.
    /// </summary>
    public string Model { get; set; }

    /// <summary>The streaming /chat body for this exchange, carrying the current <see cref="Model"/> from here on.</summary>
    public JsonObject BuildStreamingRequestBody(
        string systemPrompt,
        IReadOnlyList<LabeledScreenshot> labeledScreenshots,
        IReadOnlyList<ConversationExchange> conversationHistory,
        string userPrompt) =>
        ClaudeRequestBuilder.BuildStreamingMessagesBody(Model, systemPrompt, labeledScreenshots, conversationHistory, userPrompt);

    /// <summary>
    /// POSTs <paramref name="requestBody"/> to /chat and hands each SSE line to <paramref name="handleSseLine"/> as
    /// it arrives (audit G1), until the stream ends or the handler returns false. Throws HttpRequestException with
    /// ClaudeAPI.swift:166-178's message for a non-2xx status, and TimeoutException when a deadline passes.
    /// </summary>
    public async Task StreamResponseLinesAsync(JsonObject requestBody, Func<string, Task<bool>> handleSseLine, CancellationToken cancellationToken)
    {
        string requestJson = requestBody.ToJsonString();
        // The TimeProvider overloads (not CancelAfter on a bare source) are what let a fake clock fire the deadlines.
        using var idleDeadline = new CancellationTokenSource(RequestIdleTimeout, timeProvider);
        using var resourceDeadline = new CancellationTokenSource(ResourceTimeout, timeProvider);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idleDeadline.Token, resourceDeadline.Token);
        try
        {
            using HttpResponseMessage chatResponse = await SendWithOneConnectRetryAsync(requestJson, requestCancellation.Token);
            if (!chatResponse.IsSuccessStatusCode)
            {
                string errorBody = await chatResponse.Content.ReadAsStringAsync(requestCancellation.Token);
                throw new HttpRequestException($"API Error ({(int)chatResponse.StatusCode}): {errorBody}", inner: null, chatResponse.StatusCode);
            }

            await using Stream responseStream = await chatResponse.Content.ReadAsStreamAsync(requestCancellation.Token);
            using var responseReader = new StreamReader(responseStream, Encoding.UTF8);
            while (await responseReader.ReadLineAsync(requestCancellation.Token) is string sseLine)
            {
                idleDeadline.CancelAfter(RequestIdleTimeout);
                if (!await handleSseLine(sseLine))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only a deadline can cancel the linked source without the caller's token: a stalled Worker.
            throw new TimeoutException($"The /chat request timed out (idle {RequestIdleTimeout.TotalSeconds} s, total {ResourceTimeout.TotalSeconds} s).");
        }
    }

    private async Task<HttpResponseMessage> SendWithOneConnectRetryAsync(string requestJson, CancellationToken cancellationToken)
    {
        try
        {
            return await SendChatRequestAsync(requestJson, cancellationToken);
        }
        // URLSession's waitsForConnectivity (ClaudeAPI.swift:28) waits out a dropped connection; the port retries
        // the connect once instead (audit G2). Any other failure, and a second connect failure, propagate.
        catch (HttpRequestException connectException) when (connectException.HttpRequestError == HttpRequestError.ConnectionError)
        {
            ClickyLog.Write($"chat request: connect failed ({connectException.Message}); retrying once");
            return await SendChatRequestAsync(requestJson, cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> SendChatRequestAsync(string requestJson, CancellationToken cancellationToken)
    {
        // A request message can be sent only once, so each attempt gets its own, carrying the same body.
        using var chatRequest = new HttpRequestMessage(HttpMethod.Post, chatEndpoint)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        ClickyLog.Write($"chat request: POST {chatEndpoint} ({requestJson.Length} chars)");
        // ResponseHeadersRead returns as soon as headers arrive; otherwise HttpClient buffers the whole stream.
        return await workerHttpClient.SendAsync(chatRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task WarmUpTlsConnectionIfNeededAsync()
    {
        if (Interlocked.Exchange(ref hasStartedTlsWarmup, 1) == 1)
        {
            return;
        }
        // ClaudeAPI.swift:80-84: the TLS session ticket is host-scoped, so the root is warmed, not /chat.
        using var warmupRequest = new HttpRequestMessage(HttpMethod.Head, new Uri(chatEndpoint, "/"));
        using var warmupDeadline = new CancellationTokenSource(TlsWarmupTimeout, timeProvider);
        try
        {
            (await workerHttpClient.SendAsync(warmupRequest, warmupDeadline.Token)).Dispose();
        }
        catch (Exception)
        {
            // ClaudeAPI.swift:64-65, 93-95: purely an optimization, so the response and any failure are ignored.
            // In replay the manifest maps no "/" route, so the fail-closed handler throws here, unanswered.
        }
    }
}
