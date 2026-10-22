namespace Clicky.Core;

/// <summary>
/// The screen edge the taskbar is docked to. The values are SHAppBarMessage(ABM_GETTASKBARPOS)'s ABE_* uEdge
/// codes, so the shell casts uEdge straight to this enum.
/// </summary>
public enum TaskbarEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
}

/// <summary>
/// Where the companion panel opens for the tray icon (parity PNL-01, audit B4). The Mac centers the panel under
/// its status item, 4 pt below the menu bar (MenuBarPanelManager.swift:178-198). Windows applies the same rule
/// against whichever edge the taskbar is on: above a bottom taskbar, below a top one, beside a left or right
/// one. The gap is measured from the work area's edge (the taskbar's inner edge) and the panel is centered on
/// the icon along the taskbar. The panel is then clamped 4 DIP inside the work area, so an icon near a corner,
/// or inside the Win11 overflow flyout, never pushes it off screen. Pure, so PanelAnchorTests runs on Linux CI.
/// </summary>
public static class PanelAnchor
{
    /// <summary>MenuBarPanelManager.swift:183's gapBelowMenuBar: the gap between the panel and the taskbar, and the clamp inset on every other edge.</summary>
    public const double GapFromWorkAreaEdgeDip = 4;

    /// <param name="taskbarEdge">The edge from ABM_GETTASKBARPOS.</param>
    /// <param name="iconRectPx">The tray icon from Shell_NotifyIconGetRect, in physical pixels.</param>
    /// <param name="panelSizeDip">The panel's measured size in DIPs (WPF measures content in DIPs).</param>
    /// <param name="workAreaPx">rcWork of the icon's monitor, in physical pixels under Per-Monitor-V2 DPI.</param>
    /// <param name="dpiScale">That monitor's DPI scale (1.0 at 100%), which converts the DIP sizes to physical pixels.</param>
    public static VirtualScreenRect Compute(
        TaskbarEdge taskbarEdge,
        PhysicalPixelRect iconRectPx,
        DipSize panelSizeDip,
        PhysicalPixelRect workAreaPx,
        double dpiScale)
    {
        double gapPx = GapFromWorkAreaEdgeDip * dpiScale;
        double panelWidthPx = panelSizeDip.Width * dpiScale;
        double panelHeightPx = panelSizeDip.Height * dpiScale;
        double iconCenterXPx = iconRectPx.Left + iconRectPx.Width / 2.0;
        double iconCenterYPx = iconRectPx.Top + iconRectPx.Height / 2.0;
        double workAreaRightPx = workAreaPx.Left + workAreaPx.Width;
        double workAreaBottomPx = workAreaPx.Top + workAreaPx.Height;

        (double panelLeftPx, double panelTopPx) = taskbarEdge switch
        {
            TaskbarEdge.Bottom => (iconCenterXPx - panelWidthPx / 2, workAreaBottomPx - gapPx - panelHeightPx),
            TaskbarEdge.Top => (iconCenterXPx - panelWidthPx / 2, workAreaPx.Top + gapPx),
            TaskbarEdge.Left => (workAreaPx.Left + gapPx, iconCenterYPx - panelHeightPx / 2),
            TaskbarEdge.Right => (workAreaRightPx - gapPx - panelWidthPx, iconCenterYPx - panelHeightPx / 2),
            _ => throw new ArgumentOutOfRangeException(nameof(taskbarEdge), taskbarEdge, null),
        };

        // As in OverlayPlacement, the Max wins for a panel bigger than the work area: it stays flush with the left/top inset.
        panelLeftPx = Math.Max(workAreaPx.Left + gapPx, Math.Min(panelLeftPx, workAreaRightPx - gapPx - panelWidthPx));
        panelTopPx = Math.Max(workAreaPx.Top + gapPx, Math.Min(panelTopPx, workAreaBottomPx - gapPx - panelHeightPx));
        return new VirtualScreenRect(panelLeftPx, panelTopPx, panelWidthPx, panelHeightPx);
    }
}
