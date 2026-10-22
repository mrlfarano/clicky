using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// Read access to the companion's current voice state for code that must not hard-wire the session
/// reducer — the updater only applies a staged release while this reads <see cref="CompanionVoiceState.Idle"/>.
/// </summary>
internal interface ICompanionVoiceStateProvider
{
    CompanionVoiceState CurrentVoiceState { get; }

    event Action? VoiceStateChanged;
}

/// <summary>
/// The provider the shell feeds: whoever drives the interaction reports each voice state here, and
/// readers observe it through <see cref="ICompanionVoiceStateProvider"/>.
/// </summary>
internal sealed class CompanionVoiceStateReporter : ICompanionVoiceStateProvider
{
    // volatile: reported on the dispatcher, read by the updater from the thread pool.
    private volatile CompanionVoiceState currentVoiceState = CompanionVoiceState.Idle;

    public CompanionVoiceState CurrentVoiceState => currentVoiceState;

    public event Action? VoiceStateChanged;

    public void ReportVoiceState(CompanionVoiceState newVoiceState)
    {
        currentVoiceState = newVoiceState;
        VoiceStateChanged?.Invoke();
    }
}
