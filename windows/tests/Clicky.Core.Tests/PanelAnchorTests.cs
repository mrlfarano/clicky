using Xunit;

namespace Clicky.Core.Tests;

/// <summary>
/// Pins the panel anchor added in task T-031 (parity PNL-01, audit B4). The panel sits 4 DIP off the taskbar's
/// inner edge (the work-area edge), centered on the icon along the taskbar
/// (MenuBarPanelManager.swift:178-198), and is clamped 4 DIP inside rcWork. Every expected rect is
/// hand-computed from those rules, never produced by running PanelAnchor.
/// </summary>
public sealed class PanelAnchorTests
{
    // The Mac panel's 320 x 380 pt (MenuBarPanelManager.swift:35-36), measured as 320 x 380 DIP.
    private static readonly DipSize PanelSizeDip = new(Width: 320, Height: 380);

    [Fact]
    public void AnchorsPanelForEachTaskbarEdge()
    {
        // A 1920x1080 monitor at 100% with a 48 px bottom taskbar: rcWork (0, 0, 1920, 1032). The icon's
        // center x is 1708 + 12 = 1720, so left = 1720 - 160 = 1560; top = 1032 - 4 - 380 = 648.
        Assert.Equal(
            new VirtualScreenRect(Left: 1560, Top: 648, Width: 320, Height: 380),
            PanelAnchor.Compute(TaskbarEdge.Bottom, new PhysicalPixelRect(1708, 1044, 24, 24), PanelSizeDip, new PhysicalPixelRect(0, 0, 1920, 1032), dpiScale: 1.0));

        // Top taskbar, 48 px: rcWork (0, 48, 1920, 1032). Same center x 1720 -> left 1560; top = 48 + 4 = 52.
        Assert.Equal(
            new VirtualScreenRect(Left: 1560, Top: 52, Width: 320, Height: 380),
            PanelAnchor.Compute(TaskbarEdge.Top, new PhysicalPixelRect(1708, 12, 24, 24), PanelSizeDip, new PhysicalPixelRect(0, 48, 1920, 1032), dpiScale: 1.0));

        // Left taskbar, 62 px: rcWork (62, 0, 1858, 1080). left = 62 + 4 = 66; the icon's center y is
        // 588 + 12 = 600, so top = 600 - 190 = 410.
        Assert.Equal(
            new VirtualScreenRect(Left: 66, Top: 410, Width: 320, Height: 380),
            PanelAnchor.Compute(TaskbarEdge.Left, new PhysicalPixelRect(19, 588, 24, 24), PanelSizeDip, new PhysicalPixelRect(62, 0, 1858, 1080), dpiScale: 1.0));

        // Right taskbar, 62 px: rcWork (0, 0, 1858, 1080). left = 1858 - 4 - 320 = 1534; top 410 as above.
        Assert.Equal(
            new VirtualScreenRect(Left: 1534, Top: 410, Width: 320, Height: 380),
            PanelAnchor.Compute(TaskbarEdge.Right, new PhysicalPixelRect(1877, 588, 24, 24), PanelSizeDip, new PhysicalPixelRect(0, 0, 1858, 1080), dpiScale: 1.0));
    }

    [Fact]
    public void ClampsInsideWorkAreaNearACorner()
    {
        // Bottom taskbar, icon at the far right: center x 1890 + 12 = 1902 would put the panel at [1742, 2062],
        // past the 1916 inset line, so the clamp pulls left to 1916 - 320 = 1596.
        Assert.Equal(
            new VirtualScreenRect(Left: 1596, Top: 648, Width: 320, Height: 380),
            PanelAnchor.Compute(TaskbarEdge.Bottom, new PhysicalPixelRect(1890, 1044, 24, 24), PanelSizeDip, new PhysicalPixelRect(0, 0, 1920, 1032), dpiScale: 1.0));

        // Left taskbar, icon near the bottom: center y 1050 + 12 = 1062 would put the top at 872 and the
        // bottom at 1252, so the clamp lifts the top to 1080 - 4 - 380 = 696.
        Assert.Equal(
            new VirtualScreenRect(Left: 66, Top: 696, Width: 320, Height: 380),
            PanelAnchor.Compute(TaskbarEdge.Left, new PhysicalPixelRect(19, 1050, 24, 24), PanelSizeDip, new PhysicalPixelRect(62, 0, 1858, 1080), dpiScale: 1.0));
    }

    [Fact]
    public void ScalesPanelAndGapByMonitorDpi()
    {
        // 150%: the panel is 480 x 570 px and the gap 6 px. rcWork (0, 0, 2880, 1548) above a 72 px taskbar.
        // The icon's center x is 2560 + 18 = 2578, so left = 2578 - 240 = 2338; top = 1548 - 6 - 570 = 972.
        Assert.Equal(
            new VirtualScreenRect(Left: 2338, Top: 972, Width: 480, Height: 570),
            PanelAnchor.Compute(TaskbarEdge.Bottom, new PhysicalPixelRect(2560, 1566, 36, 36), PanelSizeDip, new PhysicalPixelRect(0, 0, 2880, 1548), dpiScale: 1.5));
    }
}
