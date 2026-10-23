namespace Clicky.Core;

/// <summary>Why the open panel is being hidden; each value is one reason token of PNL-02's evidence line.</summary>
public enum PanelHideReason
{
    /// <summary>The panel lost activation: the Windows form of the Mac's click-outside dismissal (audit B5).</summary>
    Deactivated,

    /// <summary>The reducer's DismissPanel on a push-to-talk press (CompanionManager.swift:491-492).</summary>
    PushToTalk,

    /// <summary>The panel's × button (CompanionPanelView.swift:106-107), or a tray click while the panel is open.</summary>
    Close,

    /// <summary>Start or Watch Onboarding Again (CompanionManager.swift:203, 224).</summary>
    Onboarding,
}

/// <summary>
/// Whether the companion panel is open, and the rules for opening and hiding it (parity PNL-01, PNL-02 and
/// PNL-07). The shell passes in the actual window show and hide calls. The rules and their timing run on the
/// injected clock, so PanelHideRulesTests runs on Linux CI. Every call is expected on the panel's dispatcher.
/// </summary>
public sealed class CompanionPanelVisibility(
    Action showPanelWindow,
    Action hidePanelWindow,
    Func<MicrophonePermissionState> readMicrophonePermissionState,
    Action<string> writeLogLine,
    TimeProvider timeProvider)
{
    /// <summary>MenuBarPanelManager.swift:112-117: the launch open waits 0.3 s so the tray icon is there to anchor to.</summary>
    public static readonly TimeSpan AutoOpenDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Pressing the tray icon deactivates the open panel before the click itself arrives (on mouse-up). Without
    /// this window, the "close" click would reopen the panel it had just hidden.
    /// </summary>
    public static readonly TimeSpan TrayClickAfterDeactivationIgnoreWindow = TimeSpan.FromMilliseconds(500);

    private DateTimeOffset? lastDeactivationHideTime;

    public bool IsPanelOpen { get; private set; }

    public void ShowPanel()
    {
        if (IsPanelOpen)
        {
            return;
        }
        IsPanelOpen = true;
        showPanelWindow();
    }

    /// <summary>
    /// PNL-02: hides the open panel and logs <c>panel hidden reason=deactivated|ptt|close|onboarding</c>. The one exception
    /// is deactivation while the mic is denied. The Mac keeps the panel open while permissions are missing
    /// and the app is inactive (MenuBarPanelManager.swift:226-230). On Windows the mic is the only permission,
    /// and the panel must stay so the user can follow its settings link.
    /// </summary>
    public void HidePanel(PanelHideReason hideReason)
    {
        if (!IsPanelOpen)
        {
            return;
        }
        if (hideReason == PanelHideReason.Deactivated && readMicrophonePermissionState() == MicrophonePermissionState.Denied)
        {
            return;
        }
        IsPanelOpen = false;
        if (hideReason == PanelHideReason.Deactivated)
        {
            lastDeactivationHideTime = timeProvider.GetUtcNow();
        }
        hidePanelWindow();
        writeLogLine($"panel hidden reason={ToLogReasonToken(hideReason)}");
    }

    /// <summary>PNL-01: a tray click toggles the panel (MenuBarPanelManager.swift:119-125).</summary>
    public void HandleTrayIconClicked()
    {
        if (IsPanelOpen)
        {
            HidePanel(PanelHideReason.Close);
            return;
        }
        if (lastDeactivationHideTime is DateTimeOffset deactivationHideTime
            && timeProvider.GetUtcNow() - deactivationHideTime < TrayClickAfterDeactivationIgnoreWindow)
        {
            return;
        }
        ShowPanel();
    }

    /// <summary>
    /// PNL-07: opens the panel 0.3 s after launch when the user still has to finish setup: either they have
    /// not onboarded or the mic is not granted (leanring_buddyApp.swift:47-51).
    /// </summary>
    public async Task ShowPanelOnLaunchIfSetupIncompleteAsync(bool hasCompletedOnboarding, CancellationToken cancellationToken)
    {
        if (hasCompletedOnboarding && readMicrophonePermissionState() == MicrophonePermissionState.Granted)
        {
            return;
        }
        await Task.Delay(AutoOpenDelay, timeProvider, cancellationToken);
        ShowPanel();
    }

    private static string ToLogReasonToken(PanelHideReason hideReason) => hideReason switch
    {
        PanelHideReason.Deactivated => "deactivated",
        PanelHideReason.PushToTalk => "ptt",
        PanelHideReason.Close => "close",
        PanelHideReason.Onboarding => "onboarding",
        _ => throw new ArgumentOutOfRangeException(nameof(hideReason), hideReason, null),
    };
}
