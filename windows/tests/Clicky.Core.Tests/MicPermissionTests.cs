using System.Diagnostics;
using Clicky.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Clicky.Core.Tests;

/// <summary>
/// Pins the microphone permission detector (task T-020, parity scenario PNL-04, audit row F4):
/// denied/granted flips reported within 1.5 s on an injected clock, and E_ACCESSDENIED from a
/// trial IAudioClient::Initialize mapped to denied. The real Windows probe (AppCapability with the
/// build-number caveat, WASAPI trial-initialize fallback) cannot run on CI — there is no Settings
/// privacy switch and no device on the runner — so both probe flavors enter through the injected
/// probe delegate and every timer through FakeTimeProvider, the same pattern as
/// ReplayManifestTests.TimersUseInjectedClock on the shell side.
/// </summary>
public sealed class MicPermissionTests
{
    [Fact]
    public async Task DetectsFlipWithin1500ms()
    {
        var fakeTimeProvider = new FakeTimeProvider();
        bool appCapabilityReportsAllowed = false;
        MicrophonePermissionProbeResult FakeAppCapabilityProbe() => new(
            AppCapabilityReportsAllowed: appCapabilityReportsAllowed,
            InitializeHResult: null);

        var microphonePermissionDetector = new MicrophonePermissionDetector(FakeAppCapabilityProbe, fakeTimeProvider);
        var reportedFlips = new List<(MicrophonePermissionState NewPermissionState, DateTimeOffset VirtualClockTimeOfReport)>();
        microphonePermissionDetector.PermissionStateFlipped += (_, newPermissionState) =>
            reportedFlips.Add((newPermissionState, fakeTimeProvider.GetUtcNow()));

        using var pollingCancellation = new CancellationTokenSource();
        Task pollingTask = microphonePermissionDetector.StartPollingAsync(pollingCancellation.Token);

        // The baseline probe ran synchronously before the first Delay: denied, and no flip reported
        // (the first probe establishes the baseline, it is not a flip).
        Assert.Equal(MicrophonePermissionState.Denied, microphonePermissionDetector.CurrentPermissionState);
        Assert.Empty(reportedFlips);
        DateTimeOffset baselineProbeVirtualTime = fakeTimeProvider.GetUtcNow();

        // Flip denied -> granted right after the baseline poll: the next 1.5 s tick must catch it
        // (PNL-04: the privacy toggle is detected within 1.5 s).
        appCapabilityReportsAllowed = true;
        fakeTimeProvider.Advance(MicrophonePermissionDetector.PollInterval);
        await WaitUntilAsync(() => reportedFlips.Count == 1, "the denied -> granted flip");
        Assert.Equal(MicrophonePermissionState.Granted, reportedFlips[0].NewPermissionState);
        Assert.InRange(
            reportedFlips[0].VirtualClockTimeOfReport - baselineProbeVirtualTime,
            TimeSpan.Zero,
            MicrophonePermissionDetector.PollInterval);

        // And back granted -> denied: both directions of the privacy switch stay within 1.5 s
        // (the PNL-04 VM evidence toggles the switch both ways).
        appCapabilityReportsAllowed = false;
        fakeTimeProvider.Advance(MicrophonePermissionDetector.PollInterval);
        await WaitUntilAsync(() => reportedFlips.Count == 2, "the granted -> denied flip");
        Assert.Equal(MicrophonePermissionState.Denied, reportedFlips[1].NewPermissionState);
        Assert.InRange(
            reportedFlips[1].VirtualClockTimeOfReport - reportedFlips[0].VirtualClockTimeOfReport,
            TimeSpan.Zero,
            MicrophonePermissionDetector.PollInterval);

        // Exactly the two flips: a steady state between toggles must not re-report.
        Assert.Equal(2, reportedFlips.Count);

        pollingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pollingTask);
    }

    [Fact]
    public async Task AccessDeniedMapsToDenied()
    {
        MicrophonePermissionProbeResult TrialInitializeProbe() => new(
            AppCapabilityReportsAllowed: null, // AppCapability unavailable: the audit F4 fallback path
            InitializeHResult: MicrophonePermissionDetector.AccessDeniedHResult);

        // The pure decision rule first: E_ACCESSDENIED from a fake IAudioClient.Initialize is denied.
        Assert.Equal(
            MicrophonePermissionState.Denied,
            MicrophonePermissionDetector.ResolvePermissionState(TrialInitializeProbe()));

        // And the same result when that probe feeds the detector: the baseline probe lands in
        // CurrentPermissionState without reporting a flip.
        var microphonePermissionDetector = new MicrophonePermissionDetector(
            TrialInitializeProbe, new FakeTimeProvider());
        using var pollingCancellation = new CancellationTokenSource();
        Task pollingTask = microphonePermissionDetector.StartPollingAsync(pollingCancellation.Token);
        Assert.Equal(MicrophonePermissionState.Denied, microphonePermissionDetector.CurrentPermissionState);

        pollingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pollingTask);
    }

    /// <summary>
    /// FakeTimeProvider fires the poll timer during Advance, but the awaited polling-loop
    /// continuation may need a moment of real time to run; 5 s of wall clock is generous headroom
    /// and the timestamps under assertion always come from the virtual clock, so the wait adds no
    /// timing slack to the assertions.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string conditionDescription)
    {
        var wallClockTimeout = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(
                wallClockTimeout.Elapsed < TimeSpan.FromSeconds(5),
                $"Timed out waiting for {conditionDescription}.");
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }
}
