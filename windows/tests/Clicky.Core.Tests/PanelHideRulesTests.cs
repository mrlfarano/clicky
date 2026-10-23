using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Clicky.Core.Tests;

/// <summary>
/// Pins the panel's hide rules and launch auto-open (task T-031, parity PNL-02 and PNL-07). The open panel
/// hides on deactivation, a push-to-talk press or close, and logs the reason, except that deactivation while
/// the mic is denied leaves it open (MenuBarPanelManager.swift:226-230). When setup is incomplete the panel
/// opens 0.3 s after launch (MenuBarPanelManager.swift:112-117). Every timer runs on FakeTimeProvider.
/// </summary>
public sealed class PanelHideRulesTests
{
    private readonly FakeTimeProvider fakeTimeProvider = new();
    private readonly List<string> loggedLines = [];
    private MicrophonePermissionState microphonePermissionState = MicrophonePermissionState.Granted;
    private int panelWindowShowCount;
    private int panelWindowHideCount;
    private DateTimeOffset? panelWindowShownAt;

    [Theory]
    [InlineData(PanelHideReason.Deactivated, "panel hidden reason=deactivated")]
    [InlineData(PanelHideReason.PushToTalk, "panel hidden reason=ptt")]
    [InlineData(PanelHideReason.Close, "panel hidden reason=close")]
    [InlineData(PanelHideReason.Onboarding, "panel hidden reason=onboarding")]
    public void OpenPanelHidesWithReasonLogLine(PanelHideReason hideReason, string expectedLogLine)
    {
        CompanionPanelVisibility panelVisibility = CreatePanelVisibility();
        panelVisibility.ShowPanel();

        panelVisibility.HidePanel(hideReason);

        Assert.False(panelVisibility.IsPanelOpen);
        Assert.Equal(1, panelWindowHideCount);
        Assert.Equal([expectedLogLine], loggedLines);
    }

    [Fact]
    public void DeactivatedWhileMicrophoneDeniedKeepsPanelOpen()
    {
        microphonePermissionState = MicrophonePermissionState.Denied;
        CompanionPanelVisibility panelVisibility = CreatePanelVisibility();
        panelVisibility.ShowPanel();

        panelVisibility.HidePanel(PanelHideReason.Deactivated);
        Assert.True(panelVisibility.IsPanelOpen);
        Assert.Equal(0, panelWindowHideCount);
        Assert.Empty(loggedLines);

        // The exception covers deactivation only: push-to-talk still hides the panel while the mic is denied.
        panelVisibility.HidePanel(PanelHideReason.PushToTalk);
        Assert.False(panelVisibility.IsPanelOpen);
        Assert.Equal(["panel hidden reason=ptt"], loggedLines);
    }

    [Fact]
    public void HidingAClosedPanelDoesNothing()
    {
        CompanionPanelVisibility panelVisibility = CreatePanelVisibility();

        panelVisibility.HidePanel(PanelHideReason.Close);

        Assert.Equal(0, panelWindowHideCount);
        Assert.Empty(loggedLines);
    }

    [Fact]
    public void TrayClickTogglesButIgnoresTheClickThatDeactivatedThePanel()
    {
        CompanionPanelVisibility panelVisibility = CreatePanelVisibility();

        panelVisibility.HandleTrayIconClicked();
        Assert.True(panelVisibility.IsPanelOpen);

        // Pressing the icon deactivates the panel first; the click arriving 100 ms later must not reopen it.
        panelVisibility.HidePanel(PanelHideReason.Deactivated);
        fakeTimeProvider.Advance(TimeSpan.FromMilliseconds(100));
        panelVisibility.HandleTrayIconClicked();
        Assert.False(panelVisibility.IsPanelOpen);

        // Once the 500 ms window has passed, a click opens it again, and the next click closes it.
        fakeTimeProvider.Advance(TimeSpan.FromMilliseconds(400));
        panelVisibility.HandleTrayIconClicked();
        Assert.True(panelVisibility.IsPanelOpen);
        panelVisibility.HandleTrayIconClicked();
        Assert.False(panelVisibility.IsPanelOpen);
        Assert.Equal(["panel hidden reason=deactivated", "panel hidden reason=close"], loggedLines);
    }

    [Fact]
    public async Task AutoOpensAfter300ms()
    {
        microphonePermissionState = MicrophonePermissionState.Denied;
        CompanionPanelVisibility panelVisibility = CreatePanelVisibility();
        DateTimeOffset launchTime = fakeTimeProvider.GetUtcNow();

        Task autoOpenTask = panelVisibility.ShowPanelOnLaunchIfSetupIncompleteAsync(hasCompletedOnboarding: true, CancellationToken.None);
        fakeTimeProvider.Advance(TimeSpan.FromMilliseconds(299));
        Assert.False(panelVisibility.IsPanelOpen);

        fakeTimeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await autoOpenTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(panelVisibility.IsPanelOpen);
        Assert.Equal(launchTime + TimeSpan.FromMilliseconds(300), panelWindowShownAt);
    }

    [Fact]
    public async Task AutoOpensWhenNotOnboardedEvenWithMicrophoneGranted()
    {
        CompanionPanelVisibility panelVisibility = CreatePanelVisibility();

        Task autoOpenTask = panelVisibility.ShowPanelOnLaunchIfSetupIncompleteAsync(hasCompletedOnboarding: false, CancellationToken.None);
        fakeTimeProvider.Advance(CompanionPanelVisibility.AutoOpenDelay);
        await autoOpenTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(panelVisibility.IsPanelOpen);
    }

    [Fact]
    public void SetupCompleteNeverAutoOpens()
    {
        CompanionPanelVisibility panelVisibility = CreatePanelVisibility();

        Task autoOpenTask = panelVisibility.ShowPanelOnLaunchIfSetupIncompleteAsync(hasCompletedOnboarding: true, CancellationToken.None);

        Assert.True(autoOpenTask.IsCompletedSuccessfully);
        fakeTimeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, panelWindowShowCount);
    }

    private CompanionPanelVisibility CreatePanelVisibility() => new(
        showPanelWindow: () =>
        {
            panelWindowShowCount++;
            panelWindowShownAt = fakeTimeProvider.GetUtcNow();
        },
        hidePanelWindow: () => panelWindowHideCount++,
        readMicrophonePermissionState: () => microphonePermissionState,
        writeLogLine: loggedLines.Add,
        timeProvider: fakeTimeProvider);
}
