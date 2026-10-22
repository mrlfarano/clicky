using System.Windows.Threading;
using Clicky.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Clicky.Windows;

/// <summary>
/// The shell's single orchestrator (WS-07, T-012): input events go into Core's pure
/// <see cref="CompanionSession"/> reducer, and the effects it emits are executed here against the overlay,
/// the capture → /chat → SSE plumbing of <see cref="TracerInteractionRunner"/>, and the sinks. Every
/// reduction and effect runs on the overlay's dispatcher (audit G7); <see cref="Post"/> is the way in from
/// other threads. <see cref="VoiceState"/> is observable state driven only by the reducer (audit G9), and
/// every change of it lands in <see cref="VoiceStateTimeline"/> for the report.
/// </summary>
internal sealed partial class CompanionCoordinator(
    TracerInteractionRunner interactionRunner,
    CursorOverlayWindow overlayWindow,
    string tracerTranscriptText,
    TimeProvider timeProvider,
    IAnalyticsSink analyticsSink,
    ITextToSpeechSink textToSpeechSink,
    CompanionVoiceStateReporter voiceStateReporter) : ObservableObject
{
    private readonly Queue<(CompanionSessionEvent SessionEvent, long EventTMs)> pendingSessionEvents = new();
    private readonly List<VoiceStateTimelineEntry> voiceStateTimeline = [];
    // shortcut: no Started event yet (persistent/transient start, ONB-03): the runner shows the overlay at the
    // cursor when an exchange starts; feed Started from settings and setup state with onboarding (WS-18).
    private CompanionSessionState sessionState = CompanionSessionState.Initial;
    private bool isReducing;
    private DateTimeOffset virtualClockOrigin = timeProvider.GetUtcNow();

    /// <summary>The virtual-clock time of the event being reduced, stamped on the voice-state change it causes.</summary>
    private long currentEventTMs;

    /// <summary>Set for a replay run, which records its whole run into one report; live writes one per exchange.</summary>
    private TracerRunReport? replayRunReport;

    private TracerRunReport? latestExchangeReport;

    // One cancellation covers the exchange's request, pointing and speech: any of the reducer's
    // CancelChatRequest, StopTtsPlayback or ClearPointingTarget ends whichever of them is still running.
    private CancellationTokenSource? latestExchangeCancellation;
    private Task latestExchangeTask = Task.CompletedTask;
    private Task latestPointingTask = Task.CompletedTask;

    // Generates VoiceState with change notification. Only ApplySessionState sets it, from the reducer's state.
    [ObservableProperty]
    private CompanionVoiceState voiceState;

    public IReadOnlyList<VoiceStateTimelineEntry> VoiceStateTimeline => voiceStateTimeline;

    private long ElapsedTMs => (long)(timeProvider.GetUtcNow() - virtualClockOrigin).TotalMilliseconds;

    /// <summary>
    /// Marshals an event from any thread (the keyboard hook raises on a worker thread) onto the dispatcher,
    /// where the reducer and every effect run. The task completes once the event and its effects are handled.
    /// </summary>
    public Task Post(CompanionSessionEvent sessionEvent) =>
        overlayWindow.Dispatcher.InvokeAsync(() => Dispatch(sessionEvent, ElapsedTMs)).Task;

    /// <summary>
    /// PNL-05's stop path after the hook is gone, in CompanionManager.swift:290-296's order: dictation
    /// cancelled, overlay hidden, in-flight work cancelled. Each step logs one line; ShutdownTests asserts the order.
    /// </summary>
    public void StopForShutdown()
    {
        overlayWindow.Dispatcher.VerifyAccess();
        // shortcut: recording is a stub with no capture to stop, so cancelling dictation only drops the phase;
        // stop the mic pipeline here once WS-13 wires it into the coordinator.
        sessionState = sessionState with { DictationPhase = CompanionDictationPhase.None };
        ClickyLog.Write("shutdown: dictation cancelled");
        overlayWindow.Hide();
        ClickyLog.Write("shutdown: overlay hidden");
        latestExchangeCancellation?.Cancel();
        ClickyLog.Write("shutdown: in-flight work cancelled");
    }

    /// <summary>
    /// The replay run: the tracer prompt is submitted at tMs 0, the --script events are delivered at their
    /// times on the injected clock, and the report is written once nothing is left in flight.
    /// </summary>
    public async Task<TracerRunReport> RunReplayAsync(ReplayScript? replayScript)
    {
        virtualClockOrigin = timeProvider.GetUtcNow();
        TracerRunReport report = interactionRunner.CreateReport();
        replayRunReport = report;

        // The tracer's fixed prompt stands in for a dictation that has just finalized, so the run starts where
        // an exchange starts after a release, and the script's events are the only shortcut transitions.
        ApplySessionState(
            CompanionSessionState.Initial with { DictationPhase = CompanionDictationPhase.FinalizingTranscript, VoiceState = CompanionVoiceState.Processing },
            eventTMs: 0);
        Dispatch(new CompanionSessionEvent.TranscriptFinal(tracerTranscriptText), eventTMs: 0);

        // The report waits for the scripted horizon even when the exchange failed, so no scripted event is
        // lost to the write racing the script.
        await DeliverScriptedInputEventsAsync(replayScript);
        // A scripted release starts another exchange, so wait until the latest one has finished.
        while (!latestExchangeTask.IsCompleted)
        {
            await latestExchangeTask;
        }
        WriteReport(report);
        return report;
    }

    /// <summary>
    /// Delivers each scripted input event at its tMs on the injected clock. Times are read from the
    /// TimeProvider, not Stopwatch, so a fake clock yields exact tMs values.
    /// </summary>
    private async Task DeliverScriptedInputEventsAsync(ReplayScript? replayScript)
    {
        if (replayScript is null)
        {
            return;
        }
        foreach (ReplayScriptEvent scriptedEvent in replayScript.Events)
        {
            TimeSpan waitUntilEvent = TimeSpan.FromMilliseconds(scriptedEvent.TMs - ElapsedTMs);
            if (waitUntilEvent > TimeSpan.Zero)
            {
                await Task.Delay(waitUntilEvent, timeProvider);
            }
            // The event is stamped with its SCHEDULED virtual-clock instant, not the moment the continuation
            // happened to run: this loop resumes on the WPF dispatcher, and startup rendering (overlay window
            // creation, DPI setup) can delay the continuation by hundreds of milliseconds on a slow CI runner.
            // Parity scenarios assert virtual-clock semantics ("300 ms +/- one tick on the virtual clock",
            // PTT-01); the observed slip stays in the log.
            long observedSlipTMs = ElapsedTMs - scriptedEvent.TMs;
            if (observedSlipTMs > 100)
            {
                ClickyLog.Write($"script: dispatcher slip {observedSlipTMs} ms on event scheduled at {scriptedEvent.TMs} ms (timeline keeps the virtual instant)");
            }
            ClickyLog.Write($"script: {scriptedEvent.Transition.ToString().ToLowerInvariant()} delivered at {scriptedEvent.TMs} ms");
            Dispatch(
                scriptedEvent.Transition == PushToTalkTransition.Pressed
                    ? new CompanionSessionEvent.ShortcutPressed()
                    : new CompanionSessionEvent.ShortcutReleased(),
                scriptedEvent.TMs);
        }
    }

    /// <summary>
    /// Reduces the event and executes its effects. An event an effect raises (a recording starting, a
    /// transcript finalizing) is queued and reduced after the current effects, so effects run in reducer order.
    /// </summary>
    private void Dispatch(CompanionSessionEvent sessionEvent, long eventTMs)
    {
        overlayWindow.Dispatcher.VerifyAccess();
        pendingSessionEvents.Enqueue((sessionEvent, eventTMs));
        if (isReducing)
        {
            return;
        }
        isReducing = true;
        try
        {
            while (pendingSessionEvents.TryDequeue(out (CompanionSessionEvent SessionEvent, long EventTMs) pendingSessionEvent))
            {
                CompanionSessionTransition transition = CompanionSession.Reduce(sessionState, pendingSessionEvent.SessionEvent);
                ApplySessionState(transition.State, pendingSessionEvent.EventTMs);
                foreach (CompanionSessionEffect effect in transition.Effects)
                {
                    ExecuteEffect(effect);
                }
            }
        }
        finally
        {
            isReducing = false;
        }
    }

    private void ApplySessionState(CompanionSessionState newSessionState, long eventTMs)
    {
        sessionState = newSessionState;
        currentEventTMs = eventTMs;
        VoiceState = newSessionState.VoiceState;
    }

    partial void OnVoiceStateChanged(CompanionVoiceState value)
    {
        voiceStateTimeline.Add(new VoiceStateTimelineEntry(currentEventTMs, value.ToString().ToLowerInvariant()));
        voiceStateReporter.ReportVoiceState(value);
    }

    private void ExecuteEffect(CompanionSessionEffect effect)
    {
        switch (effect)
        {
            case CompanionSessionEffect.ShowOverlay:
                overlayWindow.Show();
                break;
            case CompanionSessionEffect.HideOverlay:
                overlayWindow.Hide();
                break;
            case CompanionSessionEffect.ScheduleTransientHide scheduleTransientHide:
                _ = ReportTransientHideElapsedAsync(scheduleTransientHide);
                break;
            case CompanionSessionEffect.DismissPanel:
                // No panel until WS-18; nothing to dismiss.
                break;
            case CompanionSessionEffect.CancelChatRequest or CompanionSessionEffect.StopTtsPlayback or CompanionSessionEffect.ClearPointingTarget:
                latestExchangeCancellation?.Cancel();
                break;
            // shortcut: recording is a stub — it starts at once and "transcribes" the fixed tracer prompt, so
            // a press/release submits that prompt; replace with mic capture + transcription (WS-13/WS-14).
            case CompanionSessionEffect.StartRecording:
                Dispatch(new CompanionSessionEvent.RecordingStarted(), currentEventTMs);
                break;
            case CompanionSessionEffect.StopRecording:
                Dispatch(new CompanionSessionEvent.TranscriptFinal(tracerTranscriptText), currentEventTMs);
                break;
            // CompanionManager.swift:520-524, 596: the screens are captured when the transcript is submitted, not at the press.
            case CompanionSessionEffect.StartChatRequest startChatRequest:
                latestExchangeCancellation = new CancellationTokenSource();
                latestExchangeTask = RunChatExchangeAsync(startChatRequest, currentEventTMs, latestExchangeCancellation.Token);
                break;
            case CompanionSessionEffect.PointAtElement pointAtElement:
                latestPointingTask = interactionRunner.PointAtAsync(
                    pointAtElement.PointTagParseResult, latestExchangeReport!, latestExchangeCancellation!.Token);
                break;
            case CompanionSessionEffect.SpeakResponse speakResponse:
                _ = SpeakResponseAsync(speakResponse.SpokenText, latestExchangeCancellation!.Token);
                break;
            case CompanionSessionEffect.TrackAnalyticsEvent trackAnalyticsEvent:
                // shortcut: no event properties until the AN-01 catalogue maps EventContent to them (T-044).
                analyticsSink.TrackEvent(trackAnalyticsEvent.EventName);
                break;
            default:
                throw new InvalidOperationException($"No executor for session effect {effect}.");
        }
    }

    private async Task RunChatExchangeAsync(CompanionSessionEffect.StartChatRequest startChatRequest, long submitTMs, CancellationToken cancellationToken)
    {
        TracerRunReport report = replayRunReport ?? interactionRunner.CreateReport();
        latestExchangeReport = report;
        try
        {
            string? responseText = await interactionRunner.RunChatExchangeAsync(report, startChatRequest.Transcript, submitTMs, cancellationToken);
            if (responseText is not null)
            {
                Dispatch(new CompanionSessionEvent.ResponseReady(startChatRequest.ChatRequestId, responseText), ElapsedTMs);
                // The report carries the point, so the exchange ends when the pointing the response asked for has.
                await latestPointingTask;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClickyLog.Write("interaction cancelled by a newer press");
            return;
        }
        if (replayRunReport is null)
        {
            WriteReport(report);
        }
    }

    // shortcut: the stub sink "plays" instantly, so TtsStarted/TtsEnded bracket the request; the real sink
    // reports playback start and end itself (T-029).
    private async Task SpeakResponseAsync(string spokenText, CancellationToken cancellationToken)
    {
        Dispatch(new CompanionSessionEvent.TtsStarted(), currentEventTMs);
        try
        {
            await textToSpeechSink.SpeakAsync(spokenText, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        Dispatch(new CompanionSessionEvent.TtsEnded(), ElapsedTMs);
    }

    private async Task ReportTransientHideElapsedAsync(CompanionSessionEffect.ScheduleTransientHide scheduleTransientHide)
    {
        // Resumes on the dispatcher; a press makes the id stale instead of cancelling this timer.
        await Task.Delay(scheduleTransientHide.Delay, timeProvider);
        Dispatch(new CompanionSessionEvent.TransientHideElapsed(scheduleTransientHide.TransientHideId), ElapsedTMs);
    }

    private void WriteReport(TracerRunReport report)
    {
        report.VoiceStateTimeline.AddRange(voiceStateTimeline);
        interactionRunner.WriteReport(report);
    }
}
