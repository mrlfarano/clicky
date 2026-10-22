namespace Clicky.Core;

/// <summary>Mirrors Swift's CompanionVoiceState: what the cursor overlay shows (waveform, spinner, or nothing).</summary>
public enum CompanionVoiceState
{
    Idle,
    Listening,
    Processing,
    Responding,
}

/// <summary>
/// Where push-to-talk dictation is. Swift spreads this over BuddyDictationManager's isPreparingToRecord,
/// isRecordingFromKeyboardShortcut and isFinalizingTranscript (BuddyDictationManager.swift:225-241).
/// </summary>
public enum CompanionDictationPhase
{
    None,
    PreparingToRecord,
    Recording,
    FinalizingTranscript,
}

/// <summary>Where the reply to the last submitted transcript is (CompanionManager.swift:586-726).</summary>
public enum CompanionResponsePhase
{
    None,
    AwaitingChatResponse,
    AwaitingTtsStart,
    Speaking,
}

/// <summary>One stored exchange; AssistantResponse is the spoken text with the POINT tag removed.</summary>
public sealed record ConversationExchange(string UserTranscript, string AssistantResponse);

/// <summary>
/// IsClickyCursorEnabled false is transient cursor mode (parity spec ONB-03). IsPointingAtElement stays true
/// until the flight returns, which the transient hide waits for (CompanionManager.swift:743-748).
/// </summary>
public sealed record CompanionSessionState(
    CompanionVoiceState VoiceState,
    CompanionDictationPhase DictationPhase,
    CompanionResponsePhase ResponsePhase,
    int LatestChatRequestId,
    string? InFlightTranscript,
    IReadOnlyList<ConversationExchange> ConversationHistory,
    bool IsClickyCursorEnabled,
    bool IsOverlayVisible,
    bool IsPointingAtElement,
    int LatestTransientHideId,
    bool IsTransientHidePending)
{
    public static CompanionSessionState Initial { get; } = new(
        CompanionVoiceState.Idle,
        CompanionDictationPhase.None,
        CompanionResponsePhase.None,
        LatestChatRequestId: 0,
        InFlightTranscript: null,
        ConversationHistory: [],
        // CompanionManager.swift:122-124: the cursor is enabled when the setting is absent.
        IsClickyCursorEnabled: true,
        IsOverlayVisible: false,
        IsPointingAtElement: false,
        LatestTransientHideId: 0,
        IsTransientHidePending: false);
}

/// <summary>Something the shell observed: a shortcut transition, a dictation milestone, or a reply milestone.</summary>
public abstract record CompanionSessionEvent
{
    /// <summary>App start with the persisted setting and the setup state (CompanionManager.swift:175-195).</summary>
    public sealed record Started(bool IsClickyCursorEnabled, bool HasCompletedOnboarding, bool HasMicrophonePermission) : CompanionSessionEvent;

    public sealed record ShortcutPressed : CompanionSessionEvent;

    public sealed record ShortcutReleased : CompanionSessionEvent;

    /// <summary>The async start finished and audio is being captured (BuddyDictationManager.swift:463).</summary>
    public sealed record RecordingStarted : CompanionSessionEvent;

    /// <summary>Finalization ended, by the provider's final transcript or the fallback timer (BuddyDictationManager.swift:581-606).</summary>
    public sealed record TranscriptFinal(string TranscriptText) : CompanionSessionEvent;

    /// <summary>The full streamed reply to the chat request with this id, POINT tag included.</summary>
    public sealed record ResponseReady(int ChatRequestId, string FullResponseText) : CompanionSessionEvent;

    public sealed record TtsStarted : CompanionSessionEvent;

    public sealed record TtsEnded : CompanionSessionEvent;

    /// <summary>The buddy flew back to the cursor and the pointing target is cleared (CompanionManager.swift:743-744).</summary>
    public sealed record PointingFinished : CompanionSessionEvent;

    /// <summary>The shell's TimeProvider timer for ScheduleTransientHide with this id fired.</summary>
    public sealed record TransientHideElapsed(int TransientHideId) : CompanionSessionEvent;
}

/// <summary>A command for the shell to execute; the reducer itself does no I/O.</summary>
public abstract record CompanionSessionEffect
{
    public sealed record ShowOverlay : CompanionSessionEffect;

    /// <summary>Fade the overlay out and hide it (CompanionManager.swift:753, OverlayWindow.swift:819).</summary>
    public sealed record HideOverlay : CompanionSessionEffect;

    /// <summary>
    /// Arm a one-shot TimeProvider timer that reports TransientHideElapsed(TransientHideId) after Delay. A press
    /// makes the id stale instead of asking for the timer back, so a timer that already fired is harmless.
    /// </summary>
    public sealed record ScheduleTransientHide(int TransientHideId, TimeSpan Delay) : CompanionSessionEffect;

    public sealed record DismissPanel : CompanionSessionEffect;

    public sealed record CancelChatRequest(int ChatRequestId) : CompanionSessionEffect;

    public sealed record StopTtsPlayback : CompanionSessionEffect;

    public sealed record ClearPointingTarget : CompanionSessionEffect;

    public sealed record StartRecording : CompanionSessionEffect;

    /// <summary>Cancel a pending recording start and stop recording, which begins finalization if it had started.</summary>
    public sealed record StopRecording : CompanionSessionEffect;

    public sealed record StartChatRequest(
        int ChatRequestId,
        string Transcript,
        IReadOnlyList<ConversationExchange> ConversationHistory) : CompanionSessionEffect;

    public sealed record PointAtElement(PointTagParseResult PointTagParseResult) : CompanionSessionEffect;

    public sealed record SpeakResponse(string SpokenText) : CompanionSessionEffect;

    /// <summary>
    /// EventContent is the transcript for user_message_sent, the spoken text for ai_response_received and
    /// the label for element_pointed; the AnalyticsEvents catalogue turns it into PostHog properties.
    /// </summary>
    public sealed record TrackAnalyticsEvent(string EventName, string? EventContent = null) : CompanionSessionEffect;
}

public sealed record CompanionSessionTransition(CompanionSessionState State, IReadOnlyList<CompanionSessionEffect> Effects);

/// <summary>
/// Seam 6: the push-to-talk → chat → TTS state machine and transient cursor hide of
/// CompanionManager.swift:175-195, 430-540, 586-726 and 732-756 as a
/// pure event → (state, effects) function. Events that do not apply to the current state (a stale reply
/// after a cancel, a transcript nobody is finalizing) change nothing, standing in for Swift's
/// Task.isCancelled checks (CompanionManager.swift:598, 623, 721).
/// A failed TTS needs no event of its own: the shell speaks the fallback voice between TtsStarted and TtsEnded
/// (TTS-03, T-029).
/// shortcut: no ResponseFailed event yet, so a failed request leaves voiceState at processing until the next
/// press; add it with the CLA-06 error handling (T-027).
/// </summary>
public static class CompanionSession
{
    /// <summary>CompanionManager.swift:691-694.</summary>
    public const int MaximumConversationHistoryExchanges = 10;

    /// <summary>CompanionManager.swift:750-751: the pause after TTS and pointing end before a transient hide.</summary>
    public static readonly TimeSpan TransientHideDelay = TimeSpan.FromSeconds(1);

    public static CompanionSessionTransition Reduce(CompanionSessionState state, CompanionSessionEvent sessionEvent) =>
        ScheduleOrCancelTransientHide(ReduceEvent(state, sessionEvent));

    private static CompanionSessionTransition ReduceEvent(CompanionSessionState state, CompanionSessionEvent sessionEvent) => sessionEvent switch
    {
        CompanionSessionEvent.Started started => Started(state, started),
        CompanionSessionEvent.ShortcutPressed => ShortcutPressed(state),
        CompanionSessionEvent.ShortcutReleased => ShortcutReleased(state),
        CompanionSessionEvent.RecordingStarted when state.DictationPhase == CompanionDictationPhase.PreparingToRecord =>
            Transition(state with { DictationPhase = CompanionDictationPhase.Recording, VoiceState = CompanionVoiceState.Listening }),
        CompanionSessionEvent.TranscriptFinal transcriptFinal when state.DictationPhase == CompanionDictationPhase.FinalizingTranscript =>
            TranscriptFinal(state, transcriptFinal.TranscriptText),
        CompanionSessionEvent.ResponseReady responseReady
            when state.ResponsePhase == CompanionResponsePhase.AwaitingChatResponse
                && responseReady.ChatRequestId == state.LatestChatRequestId =>
            ResponseReady(state, responseReady.FullResponseText),
        CompanionSessionEvent.TtsStarted when state.ResponsePhase == CompanionResponsePhase.AwaitingTtsStart =>
            Transition(state with { ResponsePhase = CompanionResponsePhase.Speaking, VoiceState = CompanionVoiceState.Responding }),
        // Parity spec TTS-01: idle when playback ends. Swift goes idle as soon as play() returns
        // (CompanionManager.swift:704-722), so the responding state is never visible there.
        CompanionSessionEvent.TtsEnded when state.ResponsePhase == CompanionResponsePhase.Speaking =>
            Transition(state with { ResponsePhase = CompanionResponsePhase.None, VoiceState = CompanionVoiceState.Idle }),
        CompanionSessionEvent.PointingFinished => Transition(state with { IsPointingAtElement = false }),
        CompanionSessionEvent.TransientHideElapsed transientHideElapsed
            when state.IsTransientHidePending && transientHideElapsed.TransientHideId == state.LatestTransientHideId =>
            Transition(state with { IsOverlayVisible = false, IsTransientHidePending = false }, [new CompanionSessionEffect.HideOverlay()]),
        _ => Transition(state),
    };

    private static CompanionSessionTransition Started(CompanionSessionState state, CompanionSessionEvent.Started started)
    {
        state = state with { IsClickyCursorEnabled = started.IsClickyCursorEnabled };
        // CompanionManager.swift:190-194, with the microphone as the only Windows permission (audit D4). A
        // transient cursor stays hidden until a press shows it (:484-489).
        if (started.IsClickyCursorEnabled && started.HasCompletedOnboarding && started.HasMicrophonePermission)
        {
            return Transition(state with { IsOverlayVisible = true }, [new CompanionSessionEffect.ShowOverlay()]);
        }
        return Transition(state);
    }

    private static CompanionSessionTransition ShortcutPressed(CompanionSessionState state)
    {
        // CompanionManager.swift:476: a press while dictation is preparing, recording or finalizing is
        // ignored before push_to_talk_started fires (parity spec PTT-02b, AN-02).
        if (state.DictationPhase != CompanionDictationPhase.None)
        {
            return Transition(state);
        }

        var effects = new List<CompanionSessionEffect>();
        // CompanionManager.swift:484-489: transient mode brings the cursor back for this interaction.
        bool isOverlayVisible = state.IsOverlayVisible;
        if (!state.IsClickyCursorEnabled && !isOverlayVisible)
        {
            effects.Add(new CompanionSessionEffect.ShowOverlay());
            isOverlayVisible = true;
        }
        effects.Add(new CompanionSessionEffect.DismissPanel());
        // CompanionManager.swift:494-497: a new press abandons the previous reply. Cancelling before
        // ResponseReady is what keeps that reply out of the history and out of ai_response_received.
        if (state.ResponsePhase == CompanionResponsePhase.AwaitingChatResponse)
        {
            effects.Add(new CompanionSessionEffect.CancelChatRequest(state.LatestChatRequestId));
        }
        effects.Add(new CompanionSessionEffect.StopTtsPlayback());
        effects.Add(new CompanionSessionEffect.ClearPointingTarget());
        effects.Add(new CompanionSessionEffect.TrackAnalyticsEvent("push_to_talk_started"));
        effects.Add(new CompanionSessionEffect.StartRecording());

        // CompanionManager.swift:447-448: preparing to record shows the spinner.
        return Transition(
            state with
            {
                DictationPhase = CompanionDictationPhase.PreparingToRecord,
                ResponsePhase = CompanionResponsePhase.None,
                InFlightTranscript = null,
                VoiceState = CompanionVoiceState.Processing,
                IsOverlayVisible = isOverlayVisible,
                IsPointingAtElement = false,
            },
            effects);
    }

    private static CompanionSessionTransition ShortcutReleased(CompanionSessionState state)
    {
        // CompanionManager.swift:533 has no guard: every release is tracked, even one whose press was ignored.
        var releasedAnalyticsEvent = new CompanionSessionEffect.TrackAnalyticsEvent("push_to_talk_released");

        switch (state.DictationPhase)
        {
            case CompanionDictationPhase.PreparingToRecord:
                // A tap released before recording began: the pending start is cancelled and dictation resets
                // without submitting anything (BuddyDictationManager.swift:444-448, CompanionManager.swift:449-450).
                // The press that started this dictation already ended any reply, so idle is all that is left.
                return Transition(
                    state with { DictationPhase = CompanionDictationPhase.None, VoiceState = CompanionVoiceState.Idle },
                    [releasedAnalyticsEvent, new CompanionSessionEffect.StopRecording()]);
            case CompanionDictationPhase.Recording:
                // BuddyDictationManager.swift:487-489, CompanionManager.swift:443-444.
                return Transition(
                    state with { DictationPhase = CompanionDictationPhase.FinalizingTranscript, VoiceState = CompanionVoiceState.Processing },
                    [releasedAnalyticsEvent, new CompanionSessionEffect.StopRecording()]);
            default:
                // Already finalizing (BuddyDictationManager.swift:483) or nothing to stop.
                return Transition(state, [releasedAnalyticsEvent]);
        }
    }

    private static CompanionSessionTransition TranscriptFinal(CompanionSessionState state, string transcriptText)
    {
        string trimmedTranscriptText = transcriptText.Trim();
        // BuddyDictationManager.swift:603: nothing was said, so nothing is submitted.
        if (trimmedTranscriptText.Length == 0)
        {
            return Transition(state with { DictationPhase = CompanionDictationPhase.None, VoiceState = CompanionVoiceState.Idle });
        }

        // CompanionManager.swift:520-524, 590-592, 609-611: the request carries the history as it is now.
        int chatRequestId = state.LatestChatRequestId + 1;
        return Transition(
            state with
            {
                DictationPhase = CompanionDictationPhase.None,
                ResponsePhase = CompanionResponsePhase.AwaitingChatResponse,
                LatestChatRequestId = chatRequestId,
                InFlightTranscript = trimmedTranscriptText,
                VoiceState = CompanionVoiceState.Processing,
            },
            [
                new CompanionSessionEffect.TrackAnalyticsEvent("user_message_sent", trimmedTranscriptText),
                new CompanionSessionEffect.StartChatRequest(chatRequestId, trimmedTranscriptText, state.ConversationHistory),
            ]);
    }

    private static CompanionSessionTransition ResponseReady(CompanionSessionState state, string fullResponseText)
    {
        PointTagParseResult pointTagParseResult = PointTag.Parse(fullResponseText);
        string spokenText = pointTagParseResult.SpokenText;
        var effects = new List<CompanionSessionEffect>();
        CompanionVoiceState voiceState = state.VoiceState;
        bool isPointingAtElement = false;

        // CompanionManager.swift:633-636, 676-678: idle first so the triangle is visible for the flight.
        if (pointTagParseResult.Coordinate is not null)
        {
            voiceState = CompanionVoiceState.Idle;
            isPointingAtElement = true;
            effects.Add(new CompanionSessionEffect.PointAtElement(pointTagParseResult));
            effects.Add(new CompanionSessionEffect.TrackAnalyticsEvent("element_pointed", pointTagParseResult.ElementLabel ?? "unknown"));
        }

        // CompanionManager.swift:684-694: store the spoken text (tag stripped), keep the last 10.
        List<ConversationExchange> conversationHistory =
        [
            .. state.ConversationHistory,
            new ConversationExchange(state.InFlightTranscript!, spokenText),
        ];
        if (conversationHistory.Count > MaximumConversationHistoryExchanges)
        {
            conversationHistory.RemoveRange(0, conversationHistory.Count - MaximumConversationHistoryExchanges);
        }

        effects.Add(new CompanionSessionEffect.TrackAnalyticsEvent("ai_response_received", spokenText));

        CompanionResponsePhase responsePhase = CompanionResponsePhase.None;
        // CompanionManager.swift:702-704: blank text is not spoken, and the reply ends here (:721-722).
        if (spokenText.Trim().Length > 0)
        {
            responsePhase = CompanionResponsePhase.AwaitingTtsStart;
            effects.Add(new CompanionSessionEffect.SpeakResponse(spokenText));
        }
        else
        {
            voiceState = CompanionVoiceState.Idle;
        }

        return Transition(
            state with
            {
                ResponsePhase = responsePhase,
                InFlightTranscript = null,
                ConversationHistory = conversationHistory,
                VoiceState = voiceState,
                IsPointingAtElement = isPointingAtElement,
            },
            effects);
    }

    /// <summary>
    /// CompanionManager.swift:732-756: in transient mode, once dictation is over, TTS has stopped (:737-741) and
    /// the pointing flight has returned (:743-748), the overlay hides after a 1 s pause (:750-754). Swift polls
    /// every 200 ms; here the shell arms a TimeProvider timer and feeds TransientHideElapsed back.
    /// Swift schedules after a press with no speech only until the first reply (currentResponseTask is never
    /// reset, :457); this follows parity spec TTS-02 and schedules it every time.
    /// </summary>
    private static CompanionSessionTransition ScheduleOrCancelTransientHide(CompanionSessionTransition transition)
    {
        CompanionSessionState state = transition.State;
        bool isInteractionOver = state.DictationPhase == CompanionDictationPhase.None
            && state.ResponsePhase == CompanionResponsePhase.None
            && !state.IsPointingAtElement;
        bool shouldHideTransientOverlay = !state.IsClickyCursorEnabled && state.IsOverlayVisible && isInteractionOver;

        if (shouldHideTransientOverlay && !state.IsTransientHidePending)
        {
            int transientHideId = state.LatestTransientHideId + 1;
            return Transition(
                state with { LatestTransientHideId = transientHideId, IsTransientHidePending = true },
                [.. transition.Effects, new CompanionSessionEffect.ScheduleTransientHide(transientHideId, TransientHideDelay)]);
        }
        // CompanionManager.swift:480-482: a press cancels the pending hide.
        if (!shouldHideTransientOverlay && state.IsTransientHidePending)
        {
            return Transition(state with { IsTransientHidePending = false }, transition.Effects);
        }
        return transition;
    }

    private static CompanionSessionTransition Transition(CompanionSessionState state, IReadOnlyList<CompanionSessionEffect>? effects = null) =>
        new(state, effects ?? []);
}
