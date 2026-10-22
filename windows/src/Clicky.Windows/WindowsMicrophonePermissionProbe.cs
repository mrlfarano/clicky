using System.Runtime.InteropServices;
using Clicky.Core;
using NAudio.CoreAudioApi;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace Clicky.Windows;

/// <summary>
/// The live probe behind Core's <see cref="MicrophonePermissionDetector"/> (task T-020 / WS-13 F4
/// spike, parity scenario PNL-04). An unpackaged desktop app never gets a microphone consent
/// dialog on Windows: access is governed by Settings › Privacy › "Let desktop apps access your
/// microphone", so there is nothing to REQUEST, only to DETECT. Two flavors, per audit row F4:
/// <see cref="Windows.Security.Authorization.AppCapabilityAccess.AppCapability"/> where the build
/// has it, and otherwise the HRESULT of a trial shared-mode IAudioClient::Initialize on the default
/// capture endpoint, where E_ACCESSDENIED means the privacy switch is off.
/// </summary>
internal static class WindowsMicrophonePermissionProbe
{
    // 100 ms in REFERENCE_TIME units (100 ns each): a comfortably small trial buffer. The size does
    // not affect the answer — Initialize fails or succeeds before any audio would flow — and the
    // client is never Started, so this probe never records anything.
    private const long TrialInitializeBufferDurationInHundredsOfNanoseconds = 100 * 10000;

    public static MicrophonePermissionProbeResult ProbeMicrophonePermission()
    {
        MicrophonePermissionProbeResult? appCapabilityProbeResult = TryProbeWithAppCapability();
        if (appCapabilityProbeResult is not null)
        {
            return appCapabilityProbeResult.Value;
        }
        return ProbeWithTrialWasapiInitialize();
    }

    private static MicrophonePermissionProbeResult? TryProbeWithAppCapability()
    {
        // Build-number caveat (audit F4): AppCapabilityAccess ships with Windows 10 2004 (build
        // 19041). Clicky supports 18362+, so on older builds this guard is false and the WASAPI
        // trial-initialize fallback answers instead.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return null;
        }

        try
        {
            AppCapability microphoneCapability = AppCapability.Create("microphone");
            AppCapabilityAccessStatus microphoneAccessStatus = microphoneCapability.CheckAccess();
            return new MicrophonePermissionProbeResult(
                AppCapabilityReportsAllowed: microphoneAccessStatus == AppCapabilityAccessStatus.Allowed,
                InitializeHResult: null);
        }
        catch (Exception appCapabilityFailure)
        {
            // "Where available" is not satisfied when the API cannot be reached at all (an older
            // build with a different contract surface, or a restricted environment): fall back.
            ClickyLog.Write(
                $"mic permission: AppCapability probe failed ({appCapabilityFailure.GetType().Name}: {appCapabilityFailure.Message}); falling back to the WASAPI trial-initialize probe");
            return null;
        }
    }

    private static MicrophonePermissionProbeResult ProbeWithTrialWasapiInitialize()
    {
        try
        {
            using var deviceEnumerator = new MMDeviceEnumerator();
            using MMDevice defaultCaptureDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            AudioClient trialAudioClient = defaultCaptureDevice.AudioClient;
            trialAudioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.None,
                TrialInitializeBufferDurationInHundredsOfNanoseconds,
                0,
                trialAudioClient.MixFormat,
                Guid.Empty);
            return new MicrophonePermissionProbeResult(
                AppCapabilityReportsAllowed: null,
                InitializeHResult: MicrophonePermissionDetector.SuccessHResult);
        }
        catch (COMException trialInitializeFailure)
        {
            // E_ACCESSDENIED is the privacy switch; anything else (for example "no capture device")
            // still means capture cannot start, which the Core mapping resolves to Denied.
            return new MicrophonePermissionProbeResult(
                AppCapabilityReportsAllowed: null,
                InitializeHResult: trialInitializeFailure.HResult);
        }
    }
}
