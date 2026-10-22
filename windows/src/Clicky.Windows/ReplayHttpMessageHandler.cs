using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Clicky.Windows;

/// <summary>
/// Stands in for the network under --replay and --replay-manifest (ADR 001 tracer item 4): the manifest
/// maps Worker routes to recorded responses, so the app reads them through the same HttpClient →
/// StreamReader path as a live response. CI needs no keys and no network. A route missing from the
/// manifest throws instead of answering, so replay can never fall back to the live Worker.
/// </summary>
internal sealed class ReplayHttpMessageHandler(
    ReplayManifest replayManifest,
    TimeSpan delayBeforeEachSseEvent,
    TimeProvider timeProvider) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string requestPath = request.RequestUri?.AbsolutePath ?? "/";
        ReplayManifestRoute? manifestRoute = replayManifest.FindRouteFor(requestPath);
        if (manifestRoute is null)
        {
            // Fail closed (WS-04): an unmapped route must fail the run, never answer it. This handler is the
            // app's only HTTP handler in replay, so throwing here guarantees no socket is ever opened; a live
            // attempt would instead surface as a DNS failure for the reserved replay.clicky.invalid host.
            throw new InvalidOperationException(
                $"replay manifest has no route for '{requestPath}'; replay fails closed and never opens a live connection.");
        }

        var replayResponse = new HttpResponseMessage((HttpStatusCode)manifestRoute.Status) { RequestMessage = request };
        if (manifestRoute.ServesPacedSseStream)
        {
            replayResponse.Content = new StreamContent(
                new PacedSseEventStream(ReadSseEvents(manifestRoute.BodyFilePath!), delayBeforeEachSseEvent, timeProvider));
        }
        else
        {
            // bodyFile bytes are served verbatim (an mp3, a JSON error body); a body-less route answers empty.
            byte[] responseBodyBytes = manifestRoute.BodyFilePath is not null
                ? File.ReadAllBytes(manifestRoute.BodyFilePath)
                : Encoding.UTF8.GetBytes(manifestRoute.Body ?? "");
            replayResponse.Content = new ByteArrayContent(responseBodyBytes);
        }

        bool manifestSetsContentType = manifestRoute.Headers.Keys.Any(headerName =>
            headerName.Equals("content-type", StringComparison.OrdinalIgnoreCase));
        if (manifestRoute.ServesPacedSseStream && !manifestSetsContentType)
        {
            // The Worker streams text/event-stream; keep the same content type when the manifest does not override it.
            replayResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        }
        foreach ((string headerName, string headerValue) in manifestRoute.Headers)
        {
            replayResponse.Content.Headers.TryAddWithoutValidation(headerName, headerValue);
        }
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
    /// The wait goes through the injected <paramref name="timeProvider"/> so tests fire it by advancing a fake
    /// clock instead of sleeping.
    /// </summary>
    private sealed class PacedSseEventStream(
        IReadOnlyList<byte[]> sseEvents,
        TimeSpan delayBeforeEachEvent,
        TimeProvider timeProvider) : Stream
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
                    // The TimeProvider overload (not the bare Task.Delay) is what lets a fake clock fire this.
                    await Task.Delay(delayBeforeEachEvent, timeProvider, cancellationToken);
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
