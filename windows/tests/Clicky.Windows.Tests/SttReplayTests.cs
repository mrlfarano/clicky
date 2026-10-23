using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Clicky.Core;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-023 (WS-14 part 3): the AssemblyAI websocket shell. Parity spec STT-01, STT-02, STT-04, STT-05 and STT-08 replay
/// the recorded sessions in shared/fixtures/stt through the real Clicky.exe (--replay-manifest with
/// assemblyaiSocket.framesFile and a --script press and release), because the report is where the voice states,
/// the /chat request and the websocket frames meet. The shared-invoker case runs in-process against a loopback
/// websocket server, since only there can a test hand ClientWebSocket its invoker.
/// </summary>
[Collection(ClickyProcessReplayCollection.Name)]
public sealed class SttReplayTests
{
    private const string ReplayTemporaryToken = "replay-temporary-token";
    private const long ScriptedPressTMs = 100;
    private const long ScriptedReleaseTMs = 1500;

    [Fact]
    public async Task SessionBasicSendsUrlAndBinaryFrames()
    {
        ReplayRun replayRun = await RunSttReplayAsync("assemblyai-session-basic.jsonl", TranscribeTokenRoute());
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        // STT-01: the token is fetched, then the first out frame is the URL built from it and the 11 base keyterms.
        string expectedWebsocketUrl = AssemblyAiWebsocketUrlBuilder.BuildWebsocketUrl(
            ReplayTemporaryToken, TranscriptionKeyterms.BuildTranscriptionKeyterms([]));
        Assert.Equal(expectedWebsocketUrl, (string?)ReadFixtureFrames("assemblyai-session-basic.jsonl").First(frame => frame.Dir == "out").Frame);
        Assert.Equal(1, CountRequests(replayRun.Report, "POST", "/transcribe-token"));
        List<WebSocketFrame> outFrames = ReportFrames(replayRun.Report).Where(frame => frame.Dir == "out").ToList();
        Assert.Equal(expectedWebsocketUrl, (string?)outFrames[0].Frame);

        // Then binary PCM16 frames while listening, and the control messages after them, as the fixture records.
        List<WebSocketFrame> binaryFrames = outFrames.Where(frame => frame.Frame is JsonValue && ((string)frame.Frame!).StartsWith("<binary ")).ToList();
        Assert.True(binaryFrames.Count >= 5, $"expected ~13 binary frames for 1.4 s of audio, got {binaryFrames.Count}");
        Assert.All(binaryFrames, binaryFrame =>
        {
            int byteCount = int.Parse(((string)binaryFrame.Frame!).Split(' ')[1]);
            Assert.True(byteCount > 0 && byteCount % 2 == 0, $"a PCM16 frame of {byteCount} bytes");
        });
        Assert.Equal(CollapseBinaryRuns(ReadFixtureFrames("assemblyai-session-basic.jsonl").Where(frame => frame.Dir == "out")), CollapseBinaryRuns(outFrames));
        Assert.Equal(["url", "<binary>", """{"type":"ForceEndpoint"}""", """{"type":"Terminate"}""", "<close 1001>"], CollapseBinaryRuns(outFrames));
    }

    [Fact]
    public async Task FinalOnKeyupSubmitsTranscript()
    {
        ReplayRun replayRun = await RunSttReplayAsync("assemblyai-final-on-keyup.jsonl", TranscribeTokenRoute());
        JsonNode report = replayRun.Report;
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        // STT-04: the first end-of-turn after ForceEndpoint delivers the committed formatted turn plus this one.
        Assert.Equal("Hey Claude. where's the save button", (string?)report["requestBody"]!["messages"]![0]!["content"]!.AsArray()[^1]!["text"]);
        Assert.Equal(1, CountRequests(report, "POST", "/chat"));
        // Press, Begin + mic, release, then the reply, the stub TTS start and end (session-reducer full-exchange-with-point).
        Assert.Equal(
            ["processing", "listening", "processing", "idle", "responding", "idle"],
            report["voiceStateTimeline"]!.AsArray().Select(timelineEntry => (string?)timelineEntry!["state"]));

        // STT-02: partials stream internally but are never drawn; the bubble only ever shows Claude's reply.
        string[] transcriptTexts = ReadFixtureFrames("assemblyai-final-on-keyup.jsonl")
            .Where(frame => frame.Dir == "in" && (string?)frame.Frame!["type"] == "Turn")
            .Select(frame => (string)frame.Frame!["transcript"]!)
            .ToArray();
        JsonArray displayTimeline = report["displayTimeline"]!.AsArray();
        Assert.NotEmpty(displayTimeline);
        Assert.All(displayTimeline, displayEntry => Assert.DoesNotContain(
            transcriptTexts, transcriptText => ((string)displayEntry!["text"]!).Contains(transcriptText, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task StallDeliversAt1400ms()
    {
        ReplayRun replayRun = await RunSttReplayAsync("assemblyai-stall-after-keyup.jsonl", TranscribeTokenRoute());
        JsonNode report = replayRun.Report;
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");

        // STT-05a: no end-of-turn after key-up, so the 1.4 s grace delivers the last partial and terminates.
        Assert.Equal("can you point at the terminal", (string?)report["requestBody"]!["messages"]![0]!["content"]!.AsArray()[^1]!["text"]);
        List<WebSocketFrame> outFrames = ReportFrames(report).Where(frame => frame.Dir == "out").ToList();
        long forceEndpointTMs = outFrames.Single(frame => FrameText(frame) == """{"type":"ForceEndpoint"}""").TMs;
        long terminateTMs = outFrames.Single(frame => FrameText(frame) == """{"type":"Terminate"}""").TMs;
        long submitTMs = (long)report["captureTimeline"]!.AsArray().Single()!;
        string timingEvidence = $"frames: {string.Join(", ", ReportFrames(report).Select(frame => $"{frame.TMs} {frame.Dir} {FrameText(frame)}"))}; "
            + $"voiceStateTimeline: {report["voiceStateTimeline"]!.ToJsonString()}; submit at {submitTMs}. Output:\n{replayRun.ProcessOutput}";
        // On the session's clock: key-up's ForceEndpoint to the grace's Terminate.
        Assert.True(Math.Abs(terminateTMs - forceEndpointTMs - 1400) <= 100, $"grace took {terminateTMs - forceEndpointTMs} ms; {timingEvidence}");
        // On the run's clock: the scripted release to the transcript's submit.
        Assert.True(Math.Abs(submitTMs - ScriptedReleaseTMs - 1400) <= 100, $"release to submit took {submitTMs - ScriptedReleaseTMs} ms; {timingEvidence}");
    }

    [Fact]
    public async Task ErrorsMakeNoChatRequest()
    {
        // STT-08a: a non-2xx token response fails the start before any websocket opens.
        JsonNode tokenErrorRoute = JsonNode.Parse(File.ReadAllText(
            Path.Combine(ClickyReplayProcess.RepositoryRootDirectory, "shared", "fixtures", "parity", "transcribe-token-http-500.json")))!;
        ReplayRun tokenErrorRun = await RunSttReplayAsync("assemblyai-session-basic.jsonl", tokenErrorRoute);
        AssertNoChatAndEndsIdle(tokenErrorRun);
        Assert.Equal(1, CountRequests(tokenErrorRun.Report, "POST", "/transcribe-token"));
        Assert.Empty(tokenErrorRun.Report["webSocketFrames"]!.AsArray());
        Assert.Contains("Failed to fetch AssemblyAI token (HTTP 500)", tokenErrorRun.ProcessOutput);

        // STT-08b: an AssemblyAI Error while recording, with no final text, cancels and terminates the session.
        ReplayRun midstreamErrorRun = await RunSttReplayAsync("assemblyai-error-midstream.jsonl", TranscribeTokenRoute());
        AssertNoChatAndEndsIdle(midstreamErrorRun);
        List<string> outFramesAfterError = ReportFrames(midstreamErrorRun.Report)
            .SkipWhile(frame => !(frame.Dir == "in" && (string?)frame.Frame!["type"] == "Error"))
            .Where(frame => frame.Dir == "out")
            .Select(FrameText)
            .ToList();
        Assert.Equal(["""{"type":"Terminate"}""", "<close 1001>"], outFramesAfterError.Where(frameText => !frameText.StartsWith("<binary ")));
    }

    [Fact]
    public async Task SharedHandlerAcrossSessions()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;
        using var loopbackHandler = new LoopbackAssemblyAiHandler();
        // As in Program.cs: one invoker, created once, carries every session's upgrade request.
        using var sharedInvoker = new HttpMessageInvoker(loopbackHandler, disposeHandler: false);
        using var workerHttpClient = new HttpClient(loopbackHandler, disposeHandler: false);
        var transcriptionProvider = new AssemblyAiStreamingTranscriptionProvider(
            workerHttpClient, new Uri("http://worker.test/transcribe-token"), new SharedInvokerWebSocketConnector(sharedInvoker), TimeProvider.System);
        const int FramesPerSession = 200;

        for (int sessionIndex = 0; sessionIndex < 2; sessionIndex++)
        {
            AssemblyAiStreamingTranscriptionSession session = await transcriptionProvider
                .StartStreamingSessionAsync(TranscriptionKeyterms.BaseKeyterms, testCancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), testCancellationToken);
            // Many capture threads at once: ClientWebSocket faults on a second concurrent SendAsync, so every frame
            // arriving whole proves the send channel serialized them.
            Parallel.For(0, FramesPerSession, frameIndex => transcriptionProvider.ReceivePcm16Frame(BitConverter.GetBytes(frameIndex)));
            session.RequestFinalTranscript();
            TranscriptFinalizeEffect dictationOutcome = await session.DictationOutcome.WaitAsync(TimeSpan.FromSeconds(10), testCancellationToken);
            Assert.Equal(new TranscriptFinalizeEffect.SubmitTranscript("Hello world."), dictationOutcome);
        }

        ServerSessionRecord[] serverSessions = await Task.WhenAll(loopbackHandler.ServerSessions).WaitAsync(TimeSpan.FromSeconds(10), testCancellationToken);
        Assert.Equal(2, serverSessions.Length);
        Assert.Equal(2, loopbackHandler.TokenRequestCount);
        Assert.False(loopbackHandler.IsDisposed, "a session disposed the shared handler");
        foreach (ServerSessionRecord serverSession in serverSessions)
        {
            Assert.Equal(Enumerable.Range(0, FramesPerSession), serverSession.BinaryFrames.Select(frameBytes => BitConverter.ToInt32(frameBytes)).Order());
            Assert.All(serverSession.BinaryFrames, frameBytes => Assert.Equal(4, frameBytes.Length));
            // Audio was queued before key-up, so the control messages follow every frame, then the close is 1001.
            Assert.Equal(["""{"type":"ForceEndpoint"}""", """{"type":"Terminate"}"""], serverSession.TextMessagesAfterLastBinaryFrame);
            Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, serverSession.CloseStatus);
        }
    }

    private static void AssertNoChatAndEndsIdle(ReplayRun replayRun)
    {
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");
        Assert.Null(replayRun.Report["requestBody"]);
        Assert.Equal(0, CountRequests(replayRun.Report, "POST", "/chat"));
        Assert.Equal("idle", (string?)replayRun.Report["voiceStateTimeline"]!.AsArray()[^1]!["state"]);
    }

    private static JsonObject TranscribeTokenRoute() => new() { ["body"] = $$"""{"token":"{{ReplayTemporaryToken}}"}""" };

    private static async Task<ReplayRun> RunSttReplayAsync(string framesFileName, JsonNode transcribeTokenRoute)
    {
        string replayFilesDirectory = Path.Combine(Path.GetTempPath(), $"clicky-stt-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(replayFilesDirectory);
        try
        {
            string manifestPath = Path.Combine(replayFilesDirectory, "manifest.json");
            File.WriteAllText(manifestPath, new JsonObject
            {
                ["routes"] = new JsonObject
                {
                    ["/transcribe-token"] = transcribeTokenRoute.DeepClone(),
                    ["/chat"] = new JsonObject { ["bodyFile"] = Path.Combine(ClickyReplayProcess.RepositoryRootDirectory, "shared", "fixtures", "sse", "point-basic.sse") },
                },
                ["assemblyaiSocket"] = new JsonObject { ["framesFile"] = FixturePath(framesFileName) },
            }.ToJsonString());
            string scriptPath = Path.Combine(replayFilesDirectory, "script.json");
            File.WriteAllText(scriptPath, $$"""{ "events": [ { "tMs": {{ScriptedPressTMs}}, "type": "press" }, { "tMs": {{ScriptedReleaseTMs}}, "type": "release" } ] }""");
            return await ClickyReplayProcess.RunManifestAsync(manifestPath, "--script", scriptPath);
        }
        finally
        {
            Directory.Delete(replayFilesDirectory, recursive: true);
        }
    }

    private static string FixturePath(string framesFileName) =>
        Path.Combine(ClickyReplayProcess.RepositoryRootDirectory, "shared", "fixtures", "stt", framesFileName);

    private sealed record WebSocketFrame(long TMs, string Dir, JsonNode? Frame);

    private static List<WebSocketFrame> ReadFixtureFrames(string framesFileName) => File.ReadAllLines(FixturePath(framesFileName))
        .Where(frameLine => !string.IsNullOrWhiteSpace(frameLine))
        .Select(frameLine => JsonNode.Parse(frameLine)!)
        .Select(frameLine => new WebSocketFrame((long)frameLine["tMs"]!, (string)frameLine["dir"]!, frameLine["frame"]))
        .ToList();

    private static List<WebSocketFrame> ReportFrames(JsonNode report) => report["webSocketFrames"]!.AsArray()
        .Select(frame => new WebSocketFrame((long)frame!["tMs"]!, (string)frame["dir"]!, frame["frame"]))
        .ToList();

    /// <summary>A string frame (URL, binary, close) as its value, a JSON message as compact JSON.</summary>
    private static string FrameText(WebSocketFrame frame) =>
        frame.Frame is JsonValue frameValue ? (string)frameValue! : frame.Frame!.ToJsonString();

    /// <summary>The out-frame sequence with each run of binary frames as one "&lt;binary&gt;", since frame sizes follow the capture timing.</summary>
    private static List<string> CollapseBinaryRuns(IEnumerable<WebSocketFrame> outFrames)
    {
        List<string> collapsedFrames = [];
        foreach (WebSocketFrame outFrame in outFrames)
        {
            string frameText = FrameText(outFrame);
            frameText = frameText.StartsWith("wss://") ? "url" : frameText.StartsWith("<binary ") ? "<binary>" : frameText;
            if (collapsedFrames.Count == 0 || frameText != "<binary>" || collapsedFrames[^1] != "<binary>")
            {
                collapsedFrames.Add(frameText);
            }
        }
        return collapsedFrames;
    }

    private static int CountRequests(JsonNode report, string method, string path) =>
        report["httpRequests"]!.AsArray().Count(httpRequest => (string?)httpRequest!["method"] == method && (string?)httpRequest["path"] == path);

    private sealed record ServerSessionRecord(List<byte[]> BinaryFrames, List<string> TextMessagesAfterLastBinaryFrame, WebSocketCloseStatus? CloseStatus);

    /// <summary>
    /// The Worker's /transcribe-token and AssemblyAI in one handler: a token POST answers a token, and an upgrade
    /// request answers 101 over a loopback TCP pair whose far end is a server websocket that sends Begin, answers
    /// ForceEndpoint with a final Turn, and records everything it receives until the client closes.
    /// </summary>
    private sealed class LoopbackAssemblyAiHandler : HttpMessageHandler
    {
        private const string WebSocketAcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private readonly ConcurrentQueue<Task<ServerSessionRecord>> serverSessions = new();
        private int tokenRequestCount;

        public IReadOnlyCollection<Task<ServerSessionRecord>> ServerSessions => serverSessions;
        public int TokenRequestCount => tokenRequestCount;
        public bool IsDisposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                int tokenNumber = Interlocked.Increment(ref tokenRequestCount);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"token":"loopback-token-{{tokenNumber}}"}""") };
            }
            Assert.Contains($"token=loopback-token-{tokenRequestCount}", request.RequestUri!.Query);

            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Task clientConnect = clientSocket.ConnectAsync(listener.LocalEndPoint!, cancellationToken).AsTask();
            Socket serverSocket = await listener.AcceptAsync(cancellationToken);
            await clientConnect;
            serverSessions.Enqueue(RunServerSessionAsync(WebSocket.CreateFromStream(
                new NetworkStream(serverSocket, ownsSocket: true), new WebSocketCreationOptions { IsServer = true })));

            string webSocketKey = request.Headers.GetValues("Sec-WebSocket-Key").Single();
            var upgradeResponse = new HttpResponseMessage(HttpStatusCode.SwitchingProtocols)
            {
                RequestMessage = request,
                Content = new UpgradedConnectionContent(new NetworkStream(clientSocket, ownsSocket: true)),
            };
            upgradeResponse.Headers.Connection.Add("Upgrade");
            upgradeResponse.Headers.Upgrade.Add(new ProductHeaderValue("websocket"));
            upgradeResponse.Headers.Add("Sec-WebSocket-Accept",
                Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(webSocketKey + WebSocketAcceptGuid))));
            return upgradeResponse;
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        private static async Task<ServerSessionRecord> RunServerSessionAsync(WebSocket serverWebSocket)
        {
            using (serverWebSocket)
            {
                List<byte[]> binaryFrames = [];
                List<string> textMessagesAfterLastBinaryFrame = [];
                await SendTextAsync(serverWebSocket, """{"type":"Begin","id":"loopback","expires_at":1760000480}""");
                var messageBuffer = new MemoryStream();
                var receiveBuffer = new byte[8192];
                while (true)
                {
                    WebSocketReceiveResult receiveResult = await serverWebSocket.ReceiveAsync(receiveBuffer, CancellationToken.None);
                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        return new ServerSessionRecord(binaryFrames, textMessagesAfterLastBinaryFrame, receiveResult.CloseStatus);
                    }
                    messageBuffer.Write(receiveBuffer, 0, receiveResult.Count);
                    if (!receiveResult.EndOfMessage)
                    {
                        continue;
                    }
                    byte[] messageBytes = messageBuffer.ToArray();
                    messageBuffer.SetLength(0);
                    if (receiveResult.MessageType == WebSocketMessageType.Binary)
                    {
                        binaryFrames.Add(messageBytes);
                        textMessagesAfterLastBinaryFrame.Clear();
                        continue;
                    }
                    string messageText = Encoding.UTF8.GetString(messageBytes);
                    textMessagesAfterLastBinaryFrame.Add(messageText);
                    if (messageText == """{"type":"ForceEndpoint"}""")
                    {
                        await SendTextAsync(serverWebSocket,
                            """{"turn_order":0,"turn_is_formatted":true,"end_of_turn":true,"transcript":"Hello world.","words":[],"type":"Turn"}""");
                    }
                }
            }
        }

        private static Task SendTextAsync(WebSocket webSocket, string messageText) =>
            webSocket.SendAsync(Encoding.UTF8.GetBytes(messageText), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    /// <summary>A 101 response's body is the raw duplex connection, as SocketsHttpHandler hands ClientWebSocket.</summary>
    private sealed class UpgradedConnectionContent(Stream connectionStream) : HttpContent
    {
        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => connectionStream;

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(connectionStream);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
