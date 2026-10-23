using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Clicky.Windows;

/// <summary>
/// One open websocket (audit row G4): the AssemblyAI session's only view of the network, so replay swaps in
/// <see cref="ReplayWebSocketConnector"/>'s recorded frames for the live ClientWebSocket. At most one send and
/// one receive may be outstanding at a time, as with ClientWebSocket; the session serializes its sends.
/// </summary>
internal interface IWebSocketTransport : IAsyncDisposable
{
    /// <summary>Sends one whole message, binary (PCM16 audio) or text (a JSON control message).</summary>
    Task SendAsync(ReadOnlyMemory<byte> messageBytes, WebSocketMessageType messageType, CancellationToken cancellationToken);

    /// <summary>The next message as UTF-8 text, or null once the server closed the socket.</summary>
    Task<string?> ReceiveTextMessageAsync(CancellationToken cancellationToken);

    /// <summary>Closes with 1001 going away, Swift's cancel(with: .goingAway) (AssemblyAIStreamingTranscriptionProvider.swift:215).</summary>
    Task CloseGoingAwayAsync(CancellationToken cancellationToken);
}

internal interface IWebSocketConnector
{
    Task<IWebSocketTransport> ConnectAsync(Uri websocketUri, CancellationToken cancellationToken);
}

/// <summary>
/// The live connector: every session's ClientWebSocket connects through ONE long-lived invoker. Swift shares one
/// URLSession across sessions because a per-session one corrupts the connection pool after a few rapid
/// reconnects (AssemblyAIStreamingTranscriptionProvider.swift:30-34, the AGENTS.md lesson); ConnectAsync(uri,
/// invoker, ct) is the .NET equivalent.
/// </summary>
internal sealed class SharedInvokerWebSocketConnector(HttpMessageInvoker sharedInvoker) : IWebSocketConnector
{
    public async Task<IWebSocketTransport> ConnectAsync(Uri websocketUri, CancellationToken cancellationToken)
    {
        var clientWebSocket = new ClientWebSocket();
        try
        {
            await clientWebSocket.ConnectAsync(websocketUri, sharedInvoker, cancellationToken);
        }
        catch
        {
            clientWebSocket.Dispose();
            throw;
        }
        return new ClientWebSocketTransport(clientWebSocket);
    }

    private sealed class ClientWebSocketTransport(ClientWebSocket clientWebSocket) : IWebSocketTransport
    {
        public Task SendAsync(ReadOnlyMemory<byte> messageBytes, WebSocketMessageType messageType, CancellationToken cancellationToken) =>
            clientWebSocket.SendAsync(messageBytes, messageType, endOfMessage: true, cancellationToken).AsTask();

        public async Task<string?> ReceiveTextMessageAsync(CancellationToken cancellationToken)
        {
            var messageBuffer = new ArrayBufferWriter<byte>();
            while (true)
            {
                ValueWebSocketReceiveResult receiveResult = await clientWebSocket.ReceiveAsync(messageBuffer.GetMemory(4096), cancellationToken);
                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                messageBuffer.Advance(receiveResult.Count);
                if (receiveResult.EndOfMessage)
                {
                    // Swift decodes a binary message as UTF-8 text too (AssemblyAIStreamingTranscriptionProvider.swift:227-229).
                    return Encoding.UTF8.GetString(messageBuffer.WrittenSpan);
                }
            }
        }

        public async Task CloseGoingAwayAsync(CancellationToken cancellationToken)
        {
            if (clientWebSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // Output only, like Swift's cancel: the server's close reply is not awaited.
                await clientWebSocket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, null, cancellationToken);
            }
        }

        public ValueTask DisposeAsync()
        {
            clientWebSocket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
