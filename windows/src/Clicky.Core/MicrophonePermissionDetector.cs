namespace Clicky.Core;

/// <summary>
/// Whether Windows currently lets this app capture the microphone (parity scenario PNL-04, audit
/// row F4). Binary on purpose: the Windows panel's permission section has exactly one microphone
/// row, and its copy and "Grant" button are the same for every not-granted cause, so there is no
/// third state to represent.
/// </summary>
public enum MicrophonePermissionState
{
    Granted,
    Denied,
}

/// <summary>
/// What one probe of the microphone permission observed. Windows offers two probe flavors
/// (audit row F4): <c>AppCapability.Create("microphone").CheckAccess()</c> where the API exists,
/// and otherwise the HRESULT of a trial shared-mode <c>IAudioClient::Initialize</c> on the default
/// capture endpoint. The shell owns the probe; this record is the OS-neutral contract so the
/// detector and its tests run anywhere.
/// </summary>
/// <param name="AppCapabilityReportsAllowed">
/// Null when AppCapability is unavailable on this build (pre-2004) or could not be reached;
/// otherwise whether it reported <c>Allowed</c>. Every other AppCapability status counts as not
/// granted: an unpackaged desktop app never gets a consent prompt, so "prompt required" can never
/// turn into a grant here.
/// </param>
/// <param name="InitializeHResult">
/// HRESULT of the trial shared-mode <c>IAudioClient::Initialize</c>; null when the AppCapability
/// flavor answered instead. Zero (S_OK) means capture would work; <c>E_ACCESSDENIED</c> means the
/// "Let desktop apps access your microphone" privacy switch blocked it.
/// </param>
public readonly record struct MicrophonePermissionProbeResult(
    bool? AppCapabilityReportsAllowed,
    int? InitializeHResult);

/// <summary>
/// Port of the 1.5 s permission polling in <c>CompanionManager.startPermissionPolling</c>
/// (CompanionManager.swift:414-420), reduced to the microphone on Windows (parity scenario PNL-04:
/// "polling detects a later grant within 1.5 s"). Pure orchestration over an injected probe and an
/// injected clock — no Windows types — so <c>MicPermissionTests</c> runs on the Linux CI job with a
/// fake clock and fake probe flavors. The shell plugs in the real probe
/// (<c>WindowsMicrophonePermissionProbe</c>) and <c>TimeProvider.System</c> at startup.
/// </summary>
public sealed class MicrophonePermissionDetector
{
    /// <summary>
    /// The poll interval. The Mac polls every 1.5 s (CompanionManager.swift:417) and PNL-04 accepts
    /// detection within 1.5 s, so the interval equals the budget exactly: a flip that happens right
    /// after one poll is caught by the next tick, at worst 1.5 s later.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>E_ACCESSDENIED (0x80070005): what the "Let desktop apps access your microphone" switch makes a trial IAudioClient::Initialize return (audit F4).</summary>
    public const int AccessDeniedHResult = unchecked((int)0x80070005);

    /// <summary>S_OK (0): a trial IAudioClient::Initialize succeeded, so capture would work.</summary>
    public const int SuccessHResult = 0;

    private readonly Func<MicrophonePermissionProbeResult> probeMicrophonePermission;
    private readonly TimeProvider timeProvider;

    public MicrophonePermissionDetector(
        Func<MicrophonePermissionProbeResult> probeMicrophonePermission,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(probeMicrophonePermission);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.probeMicrophonePermission = probeMicrophonePermission;
        this.timeProvider = timeProvider;
    }

    /// <summary>The most recently probed state; null until the first probe. Set synchronously by <see cref="StartPollingAsync"/> before its first wait.</summary>
    public MicrophonePermissionState? CurrentPermissionState { get; private set; }

    /// <summary>
    /// Raised for every change of the observed state (the denied/granted flips PNL-04 needs), with
    /// the previous state first. The initial baseline probe does not raise it — it only sets
    /// <see cref="CurrentPermissionState"/> — because it is not a flip.
    /// </summary>
    public event Action<MicrophonePermissionState, MicrophonePermissionState>? PermissionStateFlipped;

    /// <summary>
    /// Probes once immediately (so <see cref="CurrentPermissionState"/> is set as soon as polling
    /// starts, before any wait), then every <see cref="PollInterval"/> on the injected clock,
    /// reporting each flip. Runs until the cancellation token is cancelled, then propagates
    /// <see cref="OperationCanceledException"/> exactly like Task.Delay does.
    /// </summary>
    public async Task StartPollingAsync(CancellationToken cancellationToken)
    {
        ProbeAndReportStateFlip();
        while (true)
        {
            // Task.Delay's TimeProvider overload is what lets FakeTimeProvider fire the next poll
            // deterministically in tests (same pattern as the shell's SSE pacing timers).
            await Task.Delay(PollInterval, timeProvider, cancellationToken);
            ProbeAndReportStateFlip();
        }
    }

    /// <summary>
    /// Audit F4's decision rule. AppCapability's word wins wherever the API is available; on the
    /// fallback path S_OK from a trial capture initialize is the only evidence of "granted", while
    /// E_ACCESSDENIED — or any other failure HRESULT, for example a machine with no capture device
    /// at all — leaves the panel row in its safe not-granted state.
    /// </summary>
    public static MicrophonePermissionState ResolvePermissionState(MicrophonePermissionProbeResult probeResult)
    {
        if (probeResult.AppCapabilityReportsAllowed is bool appCapabilityReportsAllowed)
        {
            return appCapabilityReportsAllowed
                ? MicrophonePermissionState.Granted
                : MicrophonePermissionState.Denied;
        }

        return probeResult.InitializeHResult == SuccessHResult
            ? MicrophonePermissionState.Granted
            : MicrophonePermissionState.Denied;
    }

    private void ProbeAndReportStateFlip()
    {
        MicrophonePermissionState newPermissionState = ResolvePermissionState(probeMicrophonePermission());
        if (newPermissionState == CurrentPermissionState)
        {
            return; // unchanged: PNL-04's evidence cares about flips only
        }

        bool isBaselineProbe = CurrentPermissionState is null;
        MicrophonePermissionState previousPermissionState = CurrentPermissionState ?? newPermissionState;
        CurrentPermissionState = newPermissionState;
        if (!isBaselineProbe)
        {
            PermissionStateFlipped?.Invoke(previousPermissionState, newPermissionState);
        }
    }
}
