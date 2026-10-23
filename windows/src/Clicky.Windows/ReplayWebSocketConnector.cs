using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clicky.Windows;

/// <summary>
/// Stands in for the AssemblyAI websocket under a --replay-manifest with assemblyaiSocket.framesFile (T-023).
/// The frames file is a recorded session (format: docs/02-parity-spec.md, section 2): every session replays its
/// "in" frames at their tMs after the connect, and nothing after the client's close. A server that has sent its
/// last frame stalls until the client closes. Every frame either way lands in <see cref="RecordedFrames"/>, in
/// the frames-file line format plus the session number, so a report's webSocketFrames read like a recording.
/// </summary>
internal sealed class ReplayWebSocketConnector : IWebSocketConnector
{
    private readonly IReadOnlyList<(long TMs, JsonNode Frame)> recordedInFrames;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentQueue<WebSocketFrameReport> recordedFrames = new();
    private int connectedSessionCount;

    public ReplayWebSocketConnector(string framesFilePath, TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        recordedInFrames = ReadInFrames(framesFilePath);
    }

    public IReadOnlyCollection<WebSocketFrameReport> RecordedFrames => recordedFrames;

    public Task<IWebSocketTransport> ConnectAsync(Uri websocketUri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var replayTransport = new ReplayWebSocketTransport(this, Interlocked.Increment(ref connectedSessionCount), timeProvider.GetTimestamp());
        // The upgrade request is the session's first out frame.
        replayTransport.RecordFrame("out", JsonValue.Create(websocketUri.OriginalString));
        return Task.FromResult<IWebSocketTransport>(replayTransport);
    }

    private static List<(long TMs, JsonNode Frame)> ReadInFrames(string framesFilePath)
    {
        List<(long TMs, JsonNode Frame)> inFrames = [];
        string[] frameLines = File.ReadAllLines(framesFilePath);
        for (int lineIndex = 0; lineIndex < frameLines.Length; lineIndex++)
        {
            if (string.IsNullOrWhiteSpace(frameLines[lineIndex]))
            {
                continue;
            }
            JsonNode? frameLine;
            try
            {
                frameLine = JsonNode.Parse(frameLines[lineIndex]);
            }
            catch (JsonException lineJsonException)
            {
                throw new InvalidDataException($"{framesFilePath} line {lineIndex + 1} is not JSON: {lineJsonException.Message}");
            }
            if (frameLine is not JsonObject
                || frameLine["tMs"] is not JsonValue frameTMsValue
                || !frameTMsValue.TryGetValue(out long frameTMs)
                || frameLine["dir"] is not JsonValue frameDirectionValue
                || !frameDirectionValue.TryGetValue(out string? frameDirection)
                || frameDirection is not ("in" or "out")
                || frameLine["frame"] is not JsonNode frame)
            {
                throw new InvalidDataException($"{framesFilePath} line {lineIndex + 1} is not a {{\"tMs\": <ms>, \"dir\": \"in\"|\"out\", \"frame\": …}} line.");
            }
            if (frameDirection == "in")
            {
                inFrames.Add((frameTMs, frame.DeepClone()));
            }
        }
        return inFrames;
    }

    private sealed class ReplayWebSocketTransport(ReplayWebSocketConnector connector, int sessionNumber, long connectTimestamp) : IWebSocketTransport
    {
        private readonly TaskCompletionSource clientClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int nextInFrameIndex;

        public Task SendAsync(ReadOnlyMemory<byte> messageBytes, WebSocketMessageType messageType, CancellationToken cancellationToken)
        {
            RecordFrame("out", messageType == WebSocketMessageType.Binary
                ? JsonValue.Create($"<binary {messageBytes.Length} bytes>")
                : JsonNode.Parse(Encoding.UTF8.GetString(messageBytes.Span)));
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveTextMessageAsync(CancellationToken cancellationToken)
        {
            if (nextInFrameIndex == connector.recordedInFrames.Count)
            {
                await clientClosed.Task.WaitAsync(cancellationToken);
                return null;
            }
            (long frameTMs, JsonNode frame) = connector.recordedInFrames[nextInFrameIndex++];
            TimeSpan waitUntilFrame = TimeSpan.FromMilliseconds(frameTMs) - connector.timeProvider.GetElapsedTime(connectTimestamp);
            if (waitUntilFrame > TimeSpan.Zero)
            {
                await Task.Delay(waitUntilFrame, connector.timeProvider, cancellationToken);
            }
            RecordFrame("in", frame.DeepClone());
            return frame.ToJsonString();
        }

        public Task CloseGoingAwayAsync(CancellationToken cancellationToken)
        {
            RecordFrame("out", JsonValue.Create($"<close {(int)WebSocketCloseStatus.EndpointUnavailable}>"));
            clientClosed.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            clientClosed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public void RecordFrame(string direction, JsonNode? frame) => connector.recordedFrames.Enqueue(new WebSocketFrameReport(
            sessionNumber, (long)connector.timeProvider.GetElapsedTime(connectTimestamp).TotalMilliseconds, direction, frame));
    }
}
