namespace Clicky.Core;

/// <summary>The three finalize timers of parity spec STT-05.</summary>
public enum TranscriptFinalizeDeadline
{
    /// <summary>The provider's 1.4 s wait for an explicit final turn after key-up (AssemblyAIStreamingTranscriptionProvider.swift:113, 349-365).</summary>
    ProviderGrace,

    /// <summary>The manager's 2.8 s fallback when a session exists (AssemblyAIStreamingTranscriptionProvider.swift:115, BuddyDictationManager.swift:491-511).</summary>
    SessionFallback,

    /// <summary>The manager's 2.4 s fallback when the key is released before the session opened (BuddyDictationManager.swift:219, 491-492).</summary>
    DefaultFallback,
}

/// <summary>
/// One push-to-talk dictation's finalization. LatestTranscriptText is the trimmed transcript composed after the
/// last Turn. IsFinished is Swift's hasDeliveredFinalTranscript and hasFinishedCurrentDictationSession in one:
/// whichever path finishes first wins and every later event is a no-op.
/// </summary>
public sealed record TranscriptFinalizeState(
    bool HasActiveTranscriptionSession,
    bool IsFinalizingTranscript,
    bool IsFinished,
    string LatestTranscriptText)
{
    public static TranscriptFinalizeState Initial { get; } = new(
        HasActiveTranscriptionSession: false,
        IsFinalizingTranscript: false,
        IsFinished: false,
        LatestTranscriptText: "");
}

public abstract record TranscriptFinalizeEvent
{
    /// <summary>
    /// An AssemblyAI Turn, with FullTranscriptText composed by AssemblyAiTurnTranscriptAssembler.
    /// EndsTurn is end_of_turn or turn_is_formatted (AssemblyAIStreamingTranscriptionProvider.swift:282, 304).
    /// </summary>
    public sealed record TurnReceived(string FullTranscriptText, bool EndsTurn) : TranscriptFinalizeEvent;

    /// <summary>The shortcut was released; HasActiveTranscriptionSession is false while the token fetch or websocket open is still running.</summary>
    public sealed record KeyReleased(bool HasActiveTranscriptionSession) : TranscriptFinalizeEvent;

    /// <summary>AssemblyAI's {"type":"Termination"} message (AssemblyAIStreamingTranscriptionProvider.swift:254-260).</summary>
    public sealed record TerminationReceived : TranscriptFinalizeEvent;

    /// <summary>A websocket failure or AssemblyAI {"type":"Error"} message (AssemblyAIStreamingTranscriptionProvider.swift:261-264, 392-408).</summary>
    public sealed record ErrorReceived(string ErrorMessage) : TranscriptFinalizeEvent;

    /// <summary>The shell's TimeProvider timer for ScheduleDeadline(Deadline) fired.</summary>
    public sealed record DeadlineElapsed(TranscriptFinalizeDeadline Deadline) : TranscriptFinalizeEvent;
}

/// <summary>A command for the shell to execute; the reducer itself does no I/O.</summary>
public abstract record TranscriptFinalizeEffect
{
    /// <summary>Send {"type":"ForceEndpoint"} so AssemblyAI closes the current turn (AssemblyAIStreamingTranscriptionProvider.swift:205).</summary>
    public sealed record SendForceEndpoint : TranscriptFinalizeEffect;

    /// <summary>Arm a one-shot TimeProvider timer that reports DeadlineElapsed(Deadline) after Delay. Timers are never cancelled: a late one is a no-op.</summary>
    public sealed record ScheduleDeadline(TranscriptFinalizeDeadline Deadline, TimeSpan Delay) : TranscriptFinalizeEffect;

    /// <summary>Send {"type":"Terminate"} and close the websocket (AssemblyAIStreamingTranscriptionProvider.swift:214-215, 373).</summary>
    public sealed record SendTerminate : TranscriptFinalizeEffect;

    /// <summary>Hand the trimmed, non-empty transcript to the session as TranscriptFinal (BuddyDictationManager.swift:605).</summary>
    public sealed record SubmitTranscript(string TranscriptText) : TranscriptFinalizeEffect;

    /// <summary>The final transcript was empty, so nothing is submitted (BuddyDictationManager.swift:603).</summary>
    public sealed record DiscardEmptyTranscript : TranscriptFinalizeEffect;

    /// <summary>An error with no transcript to fall back on: log ErrorMessage and end dictation without submitting (BuddyDictationManager.swift:571-578).</summary>
    public sealed record CancelDictation(string ErrorMessage) : TranscriptFinalizeEffect;
}

public sealed record TranscriptFinalizeTransition(TranscriptFinalizeState State, IReadOnlyList<TranscriptFinalizeEffect> Effects);

/// <summary>
/// Parity spec STT-04, STT-05, STT-06 and STT-08 as a pure event → (state, effects) function: the provider's
/// final-transcript wait (AssemblyAIStreamingTranscriptionProvider.swift:198-419) and the manager's fallback and
/// error handling (BuddyDictationManager.swift:476-606) merged into one machine, one instance per dictation.
/// shortcut: the provider's latestTranscriptText and the manager's latestRecognizedText are one field here, so an
/// empty partial replacing a non-empty one leaves the fallback with "" where Swift keeps the old text; split them if
/// AssemblyAI is ever seen doing that.
/// </summary>
public static class TranscriptFinalize
{
    public static readonly TimeSpan ProviderGraceDelay = TimeSpan.FromMilliseconds(1400);

    public static readonly TimeSpan SessionFallbackDelay = TimeSpan.FromMilliseconds(2800);

    public static readonly TimeSpan DefaultFallbackDelay = TimeSpan.FromMilliseconds(2400);

    public static TranscriptFinalizeTransition Reduce(TranscriptFinalizeState state, TranscriptFinalizeEvent finalizeEvent)
    {
        if (state.IsFinished)
        {
            return Transition(state);
        }

        switch (finalizeEvent)
        {
            case TranscriptFinalizeEvent.TurnReceived turnReceived:
                state = state with { LatestTranscriptText = turnReceived.FullTranscriptText.Trim() };
                // AssemblyAIStreamingTranscriptionProvider.swift:302-308: after key-up the first end-of-turn delivers at once.
                return state.IsFinalizingTranscript && turnReceived.EndsTurn ? Finish(state) : Transition(state);

            case TranscriptFinalizeEvent.KeyReleased keyReleased when !state.IsFinalizingTranscript:
                state = state with { HasActiveTranscriptionSession = keyReleased.HasActiveTranscriptionSession, IsFinalizingTranscript = true };
                // BuddyDictationManager.swift:491-496: only a live session gets ForceEndpoint and the provider grace.
                if (!keyReleased.HasActiveTranscriptionSession)
                {
                    return Transition(state, [new TranscriptFinalizeEffect.ScheduleDeadline(TranscriptFinalizeDeadline.DefaultFallback, DefaultFallbackDelay)]);
                }
                return Transition(
                    state,
                    [
                        new TranscriptFinalizeEffect.ScheduleDeadline(TranscriptFinalizeDeadline.ProviderGrace, ProviderGraceDelay),
                        new TranscriptFinalizeEffect.SendForceEndpoint(),
                        new TranscriptFinalizeEffect.ScheduleDeadline(TranscriptFinalizeDeadline.SessionFallback, SessionFallbackDelay),
                    ]);

            case TranscriptFinalizeEvent.TerminationReceived when state.IsFinalizingTranscript:
            case TranscriptFinalizeEvent.DeadlineElapsed when state.IsFinalizingTranscript:
                return Finish(state);

            case TranscriptFinalizeEvent.ErrorReceived errorReceived:
                // AssemblyAIStreamingTranscriptionProvider.swift:397-402 and BuddyDictationManager.swift:567-570: an error while
                // finalizing submits the partial. Otherwise dictation is cancelled, even with a partial (:571-578).
                if (state.IsFinalizingTranscript && state.LatestTranscriptText.Length > 0)
                {
                    return Finish(state);
                }
                // Errors only come from an open session, so there is always one to terminate (BuddyDictationManager.swift:347).
                return Transition(
                    state with { IsFinished = true },
                    [new TranscriptFinalizeEffect.SendTerminate(), new TranscriptFinalizeEffect.CancelDictation(errorReceived.ErrorMessage)]);

            default:
                return Transition(state);
        }
    }

    /// <summary>AssemblyAIStreamingTranscriptionProvider.swift:367-374 and BuddyDictationManager.swift:581-606.</summary>
    private static TranscriptFinalizeTransition Finish(TranscriptFinalizeState state)
    {
        TranscriptFinalizeEffect outcomeEffect = state.LatestTranscriptText.Length > 0
            ? new TranscriptFinalizeEffect.SubmitTranscript(state.LatestTranscriptText)
            : new TranscriptFinalizeEffect.DiscardEmptyTranscript();
        // A release before the session opened has no websocket to terminate (BuddyDictationManager.swift:598, session nil).
        TranscriptFinalizeEffect[] terminateEffects = state.HasActiveTranscriptionSession ? [new TranscriptFinalizeEffect.SendTerminate()] : [];
        return Transition(state with { IsFinished = true }, [.. terminateEffects, outcomeEffect]);
    }

    private static TranscriptFinalizeTransition Transition(TranscriptFinalizeState state, IReadOnlyList<TranscriptFinalizeEffect>? effects = null) =>
        new(state, effects ?? []);
}
