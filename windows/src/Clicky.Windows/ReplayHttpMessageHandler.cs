using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Clicky.Windows;

/// <summary>
/// Stands in for the network under --replay (ADR 001 tracer item 4): POST /chat answers 200 with a recorded
/// SSE body, so the app reads it through the same HttpClient → StreamReader path as a live response. CI needs
/// no keys and no network.
/// </summary>
internal sealed class ReplayHttpMessageHandler(string fixturePath, TimeSpan delayBeforeEachEvent) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath.EndsWith("/chat", StringComparison.Ordinal) != true)
        {
            // Anything else means the app was wired wrong; fail the way the Worker would.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }

        var replayResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StreamContent(new PacedSseEventStream(ReadSseEvents(fixturePath), delayBeforeEachEvent)),
        };
        replayResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return Task.FromResult(replayResponse);
    }

    /// <summary>Splits the fixture at blank lines, one chunk per SSE event, so --event-delay-ms paces events and not bytes.</summary>
    private static List<byte[]> ReadSseEvents(string fixturePath)
    {
        string fixtureText = File.ReadAllText(fixturePath).ReplaceLineEndings("\n");
        return fixtureText
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(sseEvent => Encoding.UTF8.GetBytes(sseEvent + "\n\n"))
            .ToList();
    }

    /// <summary>
    /// A read-only stream that hands out one SSE event per read burst, waiting before each one. A ReadAsync
    /// returns only what has "arrived", like a network stream, so the reader sees each event as it is delivered.
    /// </summary>
    private sealed class PacedSseEventStream(IReadOnlyList<byte[]> sseEvents, TimeSpan delayBeforeEachEvent) : Stream
    {
        private int nextEventIndex;
        private ReadOnlyMemory<byte> undeliveredBytesOfCurrentEvent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            while (undeliveredBytesOfCurrentEvent.IsEmpty)
            {
                if (nextEventIndex == sseEvents.Count)
                {
                    return 0;
                }
                if (delayBeforeEachEvent > TimeSpan.Zero)
                {
                    await Task.Delay(delayBeforeEachEvent, cancellationToken);
                }
                undeliveredBytesOfCurrentEvent = sseEvents[nextEventIndex++];
            }

            int deliveredByteCount = Math.Min(destination.Length, undeliveredBytesOfCurrentEvent.Length);
            undeliveredBytesOfCurrentEvent[..deliveredByteCount].CopyTo(destination);
            undeliveredBytesOfCurrentEvent = undeliveredBytesOfCurrentEvent[deliveredByteCount..];
            return deliveredByteCount;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
