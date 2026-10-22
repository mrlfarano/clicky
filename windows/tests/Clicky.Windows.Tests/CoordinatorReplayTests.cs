using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Clicky.Core;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-012 (WS-07 part 3): CompanionCoordinator is the shell's single orchestrator between input events, the
/// session reducer and the executors. Parity spec PTT-02 runs through the real Clicky.exe --script harness;
/// the dispatcher marshalling is checked in-process on a dedicated STA dispatcher thread.
/// </summary>
[Collection(ClickyProcessReplayCollection.Name)]
public sealed partial class CoordinatorReplayTests
{
    [Fact]
    public async Task RepressCancelsResponse()
    {
        // PTT-02a: the tracer prompt is submitted at tMs 0, and point-basic.sse paced at 200 ms per event is
        // still streaming when the scripted second press lands at 300 ms.
        ReplayRun replayRun = await RunPointBasicWithScriptAsync(
            """{ "events": [ { "tMs": 300, "type": "press" } ] }""", "--event-delay-ms", "200");
        JsonNode report = replayRun.Report;
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");
        Assert.Null(report["error"]);

        // The cancelled reply is never spoken, stored or pointed at (CompanionManager.swift:598, 623, 713-714).
        Assert.True(string.IsNullOrEmpty((string?)report["spokenText"]), $"spokenText was '{report["spokenText"]}'");
        Assert.Empty(report["ttsRequests"]!.AsArray());
        Assert.Null(report["point"]);
        Assert.Contains("interaction cancelled by a newer press", replayRun.ProcessOutput);

        // The submission, then the re-press; no ai_response_received or element_pointed for the cancelled reply.
        Assert.Equal(
            ["user_message_sent", "push_to_talk_started"],
            report["analyticsEvents"]!.AsArray().Select(analyticsEvent => (string?)analyticsEvent!["name"]));
    }

    [Fact]
    public void RunnerMakesNoStateTransitions()
    {
        string runnerSource = File.ReadAllText(Path.Combine(
            ClickyReplayProcess.RepositoryRootDirectory, "windows", "src", "Clicky.Windows", "TracerInteractionRunner.cs"));

        // Any voiceState assignment (a property, a local such as voiceStateAfterEvent, or a timeline entry)
        // would be the runner deciding state itself instead of leaving it to the reducer.
        Assert.DoesNotMatch(VoiceStateAssignmentPattern(), runnerSource);
        Assert.DoesNotContain("CompanionVoiceState", runnerSource);
        Assert.DoesNotContain("VoiceStateTimeline", runnerSource);
        Assert.DoesNotMatch(VoiceStateNameLiteralPattern(), runnerSource);
    }

    [Fact]
    public async Task MarshalsOntoDispatcher()
    {
        var coordinatorReady = new TaskCompletionSource<(CompanionCoordinator Coordinator, Dispatcher UiDispatcher)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerThreadIds = new ConcurrentQueue<int>();
        var analyticsSink = new ThreadRecordingAnalyticsSink(handlerThreadIds);
        var uiThread = new Thread(() =>
        {
            try
            {
                coordinatorReady.SetResult((CreateInProcessCoordinator(analyticsSink), Dispatcher.CurrentDispatcher));
            }
            catch (Exception setupException)
            {
                coordinatorReady.SetException(setupException);
                return;
            }
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "clicky-coordinator-ui-tests" };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();

        (CompanionCoordinator coordinator, Dispatcher uiDispatcher) = await coordinatorReady.Task;
        PropertyChangedEventHandler recordPropertyChangedThread = (_, _) => handlerThreadIds.Enqueue(Environment.CurrentManagedThreadId);
        coordinator.PropertyChanged += recordPropertyChangedThread;
        int postingThreadId = -1;
        try
        {
            // The keyboard hook raises its transitions on a worker thread, exactly like this thread-pool post.
            await Task.Run(() =>
            {
                postingThreadId = Environment.CurrentManagedThreadId;
                return coordinator.Post(new CompanionSessionEvent.ShortcutPressed());
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            uiDispatcher.InvokeShutdown();
        }

        Assert.NotEqual(uiThread.ManagedThreadId, postingThreadId);
        // The press reaches the reducer (processing, then listening once the stub recording starts) and
        // its analytics effect ran: every PropertyChanged and sink call happened on the UI thread.
        Assert.Equal(CompanionVoiceState.Listening, coordinator.VoiceState);
        Assert.Equal(["push_to_talk_started"], analyticsSink.RecordedEvents.Select(analyticsEvent => analyticsEvent.Name));
        Assert.NotEmpty(handlerThreadIds);
        Assert.All(handlerThreadIds, handlerThreadId => Assert.Equal(uiThread.ManagedThreadId, handlerThreadId));
    }

    [Fact]
    public async Task VoiceStateTimelineRecorded()
    {
        // Press at 300 cancels the first exchange; release at 400 submits the prompt again, and that second
        // exchange runs to completion: every state the reducer passes through lands in the report.
        ReplayRun replayRun = await RunPointBasicWithScriptAsync(
            """{ "events": [ { "tMs": 300, "type": "press" }, { "tMs": 400, "type": "release" } ] }""", "--event-delay-ms", "200");
        JsonNode report = replayRun.Report;
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");
        string tracerPromptText = (string)JsonNode.Parse(File.ReadAllText(
            Path.Combine(Path.GetDirectoryName(ClickyReplayProcess.ClickyExecutablePath)!, "appsettings.json")))!["TracerPromptText"]!;
        string secondExchangeResponseText = (string)report["responseText"]!;

        // The same events the coordinator reduces: the run starts as a just-finalized dictation of the tracer
        // prompt, the stub recorder starts at once and submits the tracer prompt on release, and the stub TTS
        // sink starts and ends as soon as it is asked to speak.
        CompanionSessionState sessionState = CompanionSessionState.Initial with
        {
            DictationPhase = CompanionDictationPhase.FinalizingTranscript,
            VoiceState = CompanionVoiceState.Processing,
        };
        CompanionSessionEvent[] coordinatorEvents =
        [
            new CompanionSessionEvent.TranscriptFinal(tracerPromptText),
            new CompanionSessionEvent.ShortcutPressed(),
            new CompanionSessionEvent.RecordingStarted(),
            new CompanionSessionEvent.ShortcutReleased(),
            new CompanionSessionEvent.TranscriptFinal(tracerPromptText),
            new CompanionSessionEvent.ResponseReady(2, secondExchangeResponseText),
            new CompanionSessionEvent.TtsStarted(),
            new CompanionSessionEvent.TtsEnded(),
        ];
        List<string> expectedVoiceStates = [ReportName(sessionState.VoiceState)];
        foreach (CompanionSessionEvent coordinatorEvent in coordinatorEvents)
        {
            sessionState = CompanionSession.Reduce(sessionState, coordinatorEvent).State;
            if (ReportName(sessionState.VoiceState) != expectedVoiceStates[^1])
            {
                expectedVoiceStates.Add(ReportName(sessionState.VoiceState));
            }
        }

        JsonArray voiceStateTimeline = report["voiceStateTimeline"]!.AsArray();
        Assert.Equal(expectedVoiceStates, voiceStateTimeline.Select(timelineEntry => (string?)timelineEntry!["state"]));
        // Changes caused by scripted events carry the events' scheduled instants on the virtual clock.
        Assert.Equal(300, (long)voiceStateTimeline[1]!["tMs"]!);
        Assert.Equal(400, (long)voiceStateTimeline[2]!["tMs"]!);
    }

    private static string ReportName(CompanionVoiceState voiceState) => voiceState.ToString().ToLowerInvariant();

    private static async Task<ReplayRun> RunPointBasicWithScriptAsync(string scriptJson, params string[] extraArguments)
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), $"clicky-replay-script-{Guid.NewGuid():N}.json");
        File.WriteAllText(scriptPath, scriptJson);
        try
        {
            return await ClickyReplayProcess.RunAsync("point-basic.sse", ["--script", scriptPath, .. extraArguments]);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    /// <summary>A coordinator wired like Program.cs, minus the network: a press never reaches the runner's HTTP client.</summary>
    private static CompanionCoordinator CreateInProcessCoordinator(IAnalyticsSink analyticsSink)
    {
        var overlayWindow = new CursorOverlayWindow();
        var textToSpeechSink = new InMemoryTextToSpeechSink();
        var fallbackSpeechSink = new InMemoryFallbackSpeechSink();
        // An empty manifest fails every request closed, so even the client's warm-up HEAD opens no socket.
        var failClosedHttpClient = new HttpClient(new ReplayHttpMessageHandler(new ReplayManifest([], null), TimeSpan.Zero, TimeProvider.System));
        var interactionRunner = new TracerInteractionRunner(
            ClickyCommandLineOptions.Parse([]),
            systemPrompt: "",
            CompanionSettings.Defaults,
            new ClaudeClient(failClosedHttpClient, new Uri("http://replay.clicky.invalid/chat"), CompanionSettings.DefaultClaudeModel, TimeProvider.System),
            [],
            [],
            overlayWindow,
            new MonitorScreenCapturer(),
            analyticsSink,
            textToSpeechSink,
            fallbackSpeechSink);
        return new CompanionCoordinator(
            interactionRunner, overlayWindow, "tracer prompt", TimeProvider.System, analyticsSink, textToSpeechSink, fallbackSpeechSink,
            new CompanionVoiceStateReporter());
    }

    [GeneratedRegex(@"voiceState\w*\s*=(?!=)", RegexOptions.IgnoreCase)]
    private static partial Regex VoiceStateAssignmentPattern();

    [GeneratedRegex("\"(idle|listening|processing|responding)\"")]
    private static partial Regex VoiceStateNameLiteralPattern();

    /// <summary>Records which thread each analytics effect ran on, alongside the event itself.</summary>
    private sealed class ThreadRecordingAnalyticsSink(ConcurrentQueue<int> handlerThreadIds) : IAnalyticsSink
    {
        public List<AnalyticsEventReport> RecordedEvents { get; } = [];

        IReadOnlyList<AnalyticsEventReport> IAnalyticsSink.RecordedEvents => RecordedEvents;

        public void TrackEvent(string eventName, IReadOnlyDictionary<string, string?>? eventProperties = null)
        {
            handlerThreadIds.Enqueue(Environment.CurrentManagedThreadId);
            RecordedEvents.Add(new AnalyticsEventReport(eventName, null));
        }
    }
}
