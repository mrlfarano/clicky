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

public sealed record CompanionSessionState(
    CompanionVoiceState VoiceState,
    CompanionDictationPhase DictationPhase,
    CompanionResponsePhase ResponsePhase,
    int LatestChatRequestId,
    string? InFlightTranscript,
    IReadOnlyList<ConversationExchange> ConversationHistory)
{
    public static CompanionSessionState Initial { get; } = new(
        CompanionVoiceState.Idle,
        CompanionDictationPhase.None,
        CompanionResponsePhase.None,
        LatestChatRequestId: 0,
        InFlightTranscript: null,
        ConversationHistory: []);
}

/// <summary>Something the shell observed: a shortcut transition, a dictation milestone, or a reply milestone.</summary>
public abstract record CompanionSessionEvent
{
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
}

/// <summary>A command for the shell to execute; the reducer itself does no I/O.</summary>
public abstract record CompanionSessionEffect
{
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
/// Seam 6: the push-to-talk → chat → TTS state machine of CompanionManager.swift:430-540 and 586-726 as a
/// pure event → (state, effects) function. Events that do not apply to the current state (a stale reply
/// after a cancel, a transcript nobody is finalizing) change nothing, standing in for Swift's
/// Task.isCancelled checks (CompanionManager.swift:598, 623, 721).
/// shortcut: no ResponseFailed/TtsFailed events yet, so a failed request or TTS leaves voiceState at
/// processing until the next press; add them with the CLA-06/TTS-03 error handling (T-027, T-029).
/// </summary>
public static class CompanionSession
{
    /// <summary>CompanionManager.swift:691-694.</summary>
    public const int MaximumConversationHistoryExchanges = 10;

    public static CompanionSessionTransition Reduce(CompanionSessionState state, CompanionSessionEvent sessionEvent) => sessionEvent switch
    {
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
        _ => Transition(state),
    };

    private static CompanionSessionTransition ShortcutPressed(CompanionSessionState state)
    {
        // CompanionManager.swift:476: a press while dictation is preparing, recording or finalizing is
        // ignored before push_to_talk_started fires (parity spec PTT-02b, AN-02).
        if (state.DictationPhase != CompanionDictationPhase.None)
        {
            return Transition(state);
        }

        var effects = new List<CompanionSessionEffect> { new CompanionSessionEffect.DismissPanel() };
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

        // CompanionManager.swift:633-636, 676-678: idle first so the triangle is visible for the flight.
        if (pointTagParseResult.Coordinate is not null)
        {
            voiceState = CompanionVoiceState.Idle;
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
            },
            effects);
    }

    private static CompanionSessionTransition Transition(CompanionSessionState state, IReadOnlyList<CompanionSessionEffect>? effects = null) =>
        new(state, effects ?? []);
}
