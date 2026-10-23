using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// The AssemblyAI streaming provider (WS-14, T-023), ported from AssemblyAIStreamingTranscriptionProvider.swift:19-85:
/// a fresh temporary token from the Worker per session, then a websocket through the one shared connector. It is
/// also the microphone pipeline's frame sink, forwarding PCM16 frames to the session that opened last.
/// </summary>
internal sealed class AssemblyAiStreamingTranscriptionProvider(
    HttpClient workerHttpClient,
    Uri transcribeTokenEndpoint,
    IWebSocketConnector sharedWebSocketConnector,
    TimeProvider timeProvider) : IPcmFrameSink
{
    private AssemblyAiStreamingTranscriptionSession? latestSession;

    /// <summary>Fetches a token, connects and returns once AssemblyAI sent Begin (STT-01); throws when any step fails.</summary>
    public async Task<AssemblyAiStreamingTranscriptionSession> StartStreamingSessionAsync(IReadOnlyList<string> keyterms, CancellationToken cancellationToken)
    {
        string temporaryToken = await FetchTemporaryTokenAsync(cancellationToken);
        var websocketUri = new Uri(AssemblyAiWebsocketUrlBuilder.BuildWebsocketUrl(temporaryToken, keyterms));
        IWebSocketTransport transport = await sharedWebSocketConnector.ConnectAsync(websocketUri, cancellationToken);
        var session = new AssemblyAiStreamingTranscriptionSession(transport, timeProvider);
        try
        {
            await session.WaitUntilReadyAsync(cancellationToken);
        }
        catch
        {
            session.Cancel();
            throw;
        }
        Volatile.Write(ref latestSession, session);
        return session;
    }

    public void ReceivePcm16Frame(ReadOnlyMemory<byte> pcm16LittleEndianFrameBytes) =>
        Volatile.Read(ref latestSession)?.AppendPcm16Frame(pcm16LittleEndianFrameBytes);

    private async Task<string> FetchTemporaryTokenAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage tokenResponse = await workerHttpClient.PostAsync(transcribeTokenEndpoint, content: null, cancellationToken);
        string tokenResponseBody = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
        return AssemblyAiTemporaryTokenResponseParser.ParseTemporaryToken((int)tokenResponse.StatusCode, tokenResponseBody);
    }
}

/// <summary>
/// One streaming session, AssemblyAIStreamingTranscriptionSession in AssemblyAIStreamingTranscriptionProvider.swift:87-478.
/// Swift's two serial GCD queues are two single-reader channels (audit G8): the state queue runs turn assembly
/// and Core's <see cref="TranscriptFinalize"/> machine one work item at a time, and the send queue keeps
/// ClientWebSocket to one SendAsync at a time, in the order audio and control messages were queued.
/// </summary>
internal sealed class AssemblyAiStreamingTranscriptionSession
{
    private static readonly byte[] ForceEndpointMessageBytes = """{"type":"ForceEndpoint"}"""u8.ToArray();
    private static readonly byte[] TerminateMessageBytes = """{"type":"Terminate"}"""u8.ToArray();

    private readonly IWebSocketTransport transport;
    private readonly TimeProvider timeProvider;
    private readonly Channel<Action> stateQueue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });

    // WebSocketMessageType.Close is the queue's "close 1001 after everything queued before it" marker.
    private readonly Channel<(ReadOnlyMemory<byte> MessageBytes, WebSocketMessageType MessageType)> sendQueue =
        Channel.CreateUnbounded<(ReadOnlyMemory<byte>, WebSocketMessageType)>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource receiveCancellation = new();
    private readonly TaskCompletionSource readyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<TranscriptFinalizeEffect> dictationOutcomeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AssemblyAiTurnTranscriptAssembler turnTranscriptAssembler = new();
    private TranscriptFinalizeState finalizeState = TranscriptFinalizeState.Initial with { HasActiveTranscriptionSession = true };

    public AssemblyAiStreamingTranscriptionSession(IWebSocketTransport transport, TimeProvider timeProvider)
    {
        this.transport = transport;
        this.timeProvider = timeProvider;
        // A start that is abandoned never awaits readiness, so its failure must not surface as unobserved.
        _ = readyCompletion.Task.ContinueWith(readyTask => readyTask.Exception, TaskContinuationOptions.OnlyOnFaulted);
        // On the thread pool, like Swift's own queues: a session opened from the dispatcher must not queue its
        // sends behind the dispatcher's submit-time screen capture.
        _ = Task.Run(RunStateQueueAsync);
        _ = Task.Run(RunSendQueueAsync);
        _ = Task.Run(RunReceiveLoopAsync);
    }

    /// <summary>
    /// How the dictation ended: SubmitTranscript, DiscardEmptyTranscript or CancelDictation, the outcome effects of
    /// <see cref="TranscriptFinalize"/>. Completes once; the websocket is already terminating by then.
    /// </summary>
    public Task<TranscriptFinalizeEffect> DictationOutcome => dictationOutcomeCompletion.Task;

    public void AppendPcm16Frame(ReadOnlyMemory<byte> pcm16LittleEndianFrameBytes) =>
        sendQueue.Writer.TryWrite((pcm16LittleEndianFrameBytes, WebSocketMessageType.Binary));

    /// <summary>Key-up, once audio has stopped: ForceEndpoint and the finalize deadlines (STT-04, STT-05).</summary>
    public void RequestFinalTranscript() =>
        EnqueueFinalizeEvent(new TranscriptFinalizeEvent.KeyReleased(HasActiveTranscriptionSession: true));

    /// <summary>Swift's cancel(): Terminate, then close 1001 (AssemblyAIStreamingTranscriptionProvider.swift:208-216).</summary>
    public void Cancel()
    {
        sendQueue.Writer.TryWrite((TerminateMessageBytes, WebSocketMessageType.Text));
        sendQueue.Writer.TryWrite((ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Close));
    }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => readyCompletion.Task.WaitAsync(cancellationToken);

    private async Task RunStateQueueAsync()
    {
        await foreach (Action stateWorkItem in stateQueue.Reader.ReadAllAsync())
        {
            stateWorkItem();
        }
    }

    private void EnqueueFinalizeEvent(TranscriptFinalizeEvent finalizeEvent) =>
        stateQueue.Writer.TryWrite(() => ReduceFinalizeEvent(finalizeEvent));

    private void ReduceFinalizeEvent(TranscriptFinalizeEvent finalizeEvent)
    {
        TranscriptFinalizeTransition transition = TranscriptFinalize.Reduce(finalizeState, finalizeEvent);
        finalizeState = transition.State;
        foreach (TranscriptFinalizeEffect effect in transition.Effects)
        {
            switch (effect)
            {
                case TranscriptFinalizeEffect.SendForceEndpoint:
                    sendQueue.Writer.TryWrite((ForceEndpointMessageBytes, WebSocketMessageType.Text));
                    break;
                case TranscriptFinalizeEffect.ScheduleDeadline scheduleDeadline:
                    _ = ReportDeadlineElapsedAsync(scheduleDeadline);
                    break;
                case TranscriptFinalizeEffect.SendTerminate:
                    Cancel();
                    break;
                default:
                    dictationOutcomeCompletion.TrySetResult(effect);
                    break;
            }
        }
    }

    // Never cancelled: once the machine has finished, a late deadline is a no-op.
    private async Task ReportDeadlineElapsedAsync(TranscriptFinalizeEffect.ScheduleDeadline scheduleDeadline)
    {
        await Task.Delay(scheduleDeadline.Delay, timeProvider);
        EnqueueFinalizeEvent(new TranscriptFinalizeEvent.DeadlineElapsed(scheduleDeadline.Deadline));
    }

    private async Task RunSendQueueAsync()
    {
        try
        {
            await foreach ((ReadOnlyMemory<byte> messageBytes, WebSocketMessageType messageType) in sendQueue.Reader.ReadAllAsync())
            {
                if (messageType == WebSocketMessageType.Close)
                {
                    await transport.CloseGoingAwayAsync(CancellationToken.None);
                    break;
                }
                await transport.SendAsync(messageBytes, messageType, CancellationToken.None);
            }
        }
        catch (Exception sendException)
        {
            // AssemblyAIStreamingTranscriptionProvider.swift:190-194: a failed send fails the session.
            FailSession(sendException.Message);
        }
        finally
        {
            sendQueue.Writer.TryComplete();
            receiveCancellation.Cancel();
            await transport.DisposeAsync();
        }
    }

    private async Task RunReceiveLoopAsync()
    {
        try
        {
            while (await transport.ReceiveTextMessageAsync(receiveCancellation.Token) is string messageText)
            {
                HandleIncomingTextMessage(messageText);
            }
            throw new WebSocketException("AssemblyAI closed the websocket.");
        }
        catch (Exception) when (receiveCancellation.IsCancellationRequested)
        {
            // This session closed the socket itself; whatever the aborted receive threw is expected.
        }
        catch (Exception receiveException)
        {
            // AssemblyAIStreamingTranscriptionProvider.swift:236-237: any receive failure fails the session.
            FailSession(receiveException.Message);
        }
    }

    /// <summary>AssemblyAIStreamingTranscriptionProvider.swift:242-271; a message that does not decode fails the session.</summary>
    private void HandleIncomingTextMessage(string messageText)
    {
        try
        {
            using JsonDocument messageDocument = JsonDocument.Parse(messageText);
            JsonElement message = messageDocument.RootElement;
            string messageType = message.GetProperty("type").GetString() ?? throw new JsonException("AssemblyAI message type is null.");
            switch (messageType.ToLowerInvariant())
            {
                case "begin":
                    readyCompletion.TrySetResult();
                    break;
                case "turn":
                    var turnMessage = new AssemblyAiTurnMessage(
                        OptionalProperty(message, "transcript")?.GetString(),
                        OptionalProperty(message, "turn_order")?.GetInt32(),
                        OptionalProperty(message, "end_of_turn")?.GetBoolean(),
                        OptionalProperty(message, "turn_is_formatted")?.GetBoolean());
                    stateQueue.Writer.TryWrite(() => ReduceFinalizeEvent(new TranscriptFinalizeEvent.TurnReceived(
                        turnTranscriptAssembler.ApplyTurnMessage(turnMessage),
                        EndsTurn: turnMessage.EndOfTurn == true || turnMessage.TurnIsFormatted == true)));
                    break;
                case "termination":
                    readyCompletion.TrySetResult();
                    EnqueueFinalizeEvent(new TranscriptFinalizeEvent.TerminationReceived());
                    break;
                case "error":
                    FailSession(OptionalProperty(message, "error")?.GetString()
                        ?? OptionalProperty(message, "message")?.GetString()
                        ?? "AssemblyAI returned an error.");
                    break;
            }
        }
        catch (Exception decodeException) when (decodeException is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            FailSession(decodeException.Message);
        }
    }

    /// <summary>A Swift Optional field: absent or null reads as null, any other value must have the right type.</summary>
    private static JsonElement? OptionalProperty(JsonElement message, string propertyName) =>
        message.TryGetProperty(propertyName, out JsonElement propertyValue) && propertyValue.ValueKind != JsonValueKind.Null
            ? propertyValue
            : null;

    /// <summary>AssemblyAIStreamingTranscriptionProvider.swift:392-408: an open fails, and the finalize machine decides the rest.</summary>
    private void FailSession(string errorMessage)
    {
        readyCompletion.TrySetException(new AssemblyAiTranscriptionException(errorMessage));
        EnqueueFinalizeEvent(new TranscriptFinalizeEvent.ErrorReceived(errorMessage));
    }
}
