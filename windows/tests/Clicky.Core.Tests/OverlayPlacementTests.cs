using Xunit;

namespace Clicky.Core.Tests;

/// <summary>
/// Pins the pure overlay placement added in task T-003 (parity PNT-04, workstream WS-01). Decision
/// PNT-04: the glyph's tip is the hotspot and sits exactly on the target; the Mac's 8/12 pt buddy
/// offset (OverlayWindow.swift:465-468) moves to the bubble as its gap from the hotspot; the bubble
/// flips to the side with room and clamps 20 DIP inside rcWork on every edge
/// (OverlayWindow.swift:471-474). Every expected rect below is hand-computed from those rules,
/// never produced by running OverlayPlacement.
/// </summary>
public sealed class OverlayPlacementTests
{
    // A 1920x1080 primary at 100% with a 48-px taskbar: rcWork = (0, 0, 1920, 1032), so the 20-DIP
    // inset lines sit at x 20/1900 and y 20/1012.
    private static readonly PhysicalPixelRect PrimaryWorkAreaPx = new(Left: 0, Top: 0, Width: 1920, Height: 1032);

    // A 160x48-DIP bubble; at 100% that is 160x48 px.
    private static readonly DipSize BubbleSizeDip = new(Width: 160, Height: 48);

    [Fact]
    public void BubbleFlipsAtEachEdge()
    {
        // Right edge: the default right-side bubble would span [1888, 2048], past the 1900 inset
        // line, so it flips left keeping the same 8 px gap: [1712, 1872]. The hotspot stays at 1880.
        OverlayPlacementResult rightEdgePlacement = PlaceOnPrimaryWorkArea(new VirtualScreenPoint(1880, 500));
        Assert.Equal(OverlayBubbleHorizontalSide.LeftOfHotspot, rightEdgePlacement.BubbleHorizontalSide);
        Assert.Equal(OverlayBubbleVerticalSide.BelowHotspot, rightEdgePlacement.BubbleVerticalSide);
        Assert.Equal(new VirtualScreenRect(Left: 1712, Top: 512, Width: 160, Height: 48), rightEdgePlacement.BubbleRectPx);
        AssertBubbleStaysTwentyDipInsideWorkArea(rightEdgePlacement, PrimaryWorkAreaPx, dpiScale: 1.0);

        // Bottom edge: the default below-hotspot bubble would span [1012, 1060], past the 1012 inset
        // line, so it flips above keeping the same 12 px gap: top 1000 - 12 - 48 = 940.
        OverlayPlacementResult bottomEdgePlacement = PlaceOnPrimaryWorkArea(new VirtualScreenPoint(400, 1000));
        Assert.Equal(OverlayBubbleHorizontalSide.RightOfHotspot, bottomEdgePlacement.BubbleHorizontalSide);
        Assert.Equal(OverlayBubbleVerticalSide.AboveHotspot, bottomEdgePlacement.BubbleVerticalSide);
        Assert.Equal(new VirtualScreenRect(Left: 408, Top: 940, Width: 160, Height: 48), bottomEdgePlacement.BubbleRectPx);
        AssertBubbleStaysTwentyDipInsideWorkArea(bottomEdgePlacement, PrimaryWorkAreaPx, dpiScale: 1.0);

        // Left edge: the bubble's default side (right of the hotspot) already has room and flipping
        // left would only move it further out, so it does not flip; the clamp lifts its left edge
        // from 10 + 8 = 18 to the 20 px inset line instead.
        OverlayPlacementResult leftEdgePlacement = PlaceOnPrimaryWorkArea(new VirtualScreenPoint(10, 500));
        Assert.Equal(OverlayBubbleHorizontalSide.RightOfHotspot, leftEdgePlacement.BubbleHorizontalSide);
        Assert.Equal(OverlayBubbleVerticalSide.BelowHotspot, leftEdgePlacement.BubbleVerticalSide);
        Assert.Equal(new VirtualScreenRect(Left: 20, Top: 512, Width: 160, Height: 48), leftEdgePlacement.BubbleRectPx);
        AssertBubbleStaysTwentyDipInsideWorkArea(leftEdgePlacement, PrimaryWorkAreaPx, dpiScale: 1.0);

        // Top edge: the same on the vertical axis — no flip, the clamp lifts the top edge from
        // 5 + 12 = 17 to the 20 px inset line.
        OverlayPlacementResult topEdgePlacement = PlaceOnPrimaryWorkArea(new VirtualScreenPoint(400, 5));
        Assert.Equal(OverlayBubbleHorizontalSide.RightOfHotspot, topEdgePlacement.BubbleHorizontalSide);
        Assert.Equal(OverlayBubbleVerticalSide.BelowHotspot, topEdgePlacement.BubbleVerticalSide);
        Assert.Equal(new VirtualScreenRect(Left: 408, Top: 20, Width: 160, Height: 48), topEdgePlacement.BubbleRectPx);
        AssertBubbleStaysTwentyDipInsideWorkArea(topEdgePlacement, PrimaryWorkAreaPx, dpiScale: 1.0);

        // Bottom-right corner: both axes flip at once — left 1880 - 8 - 160 = 1712, above
        // 1000 - 12 - 48 = 940.
        OverlayPlacementResult cornerPlacement = PlaceOnPrimaryWorkArea(new VirtualScreenPoint(1880, 1000));
        Assert.Equal(OverlayBubbleHorizontalSide.LeftOfHotspot, cornerPlacement.BubbleHorizontalSide);
        Assert.Equal(OverlayBubbleVerticalSide.AboveHotspot, cornerPlacement.BubbleVerticalSide);
        Assert.Equal(new VirtualScreenRect(Left: 1712, Top: 940, Width: 160, Height: 48), cornerPlacement.BubbleRectPx);
        AssertBubbleStaysTwentyDipInsideWorkArea(cornerPlacement, PrimaryWorkAreaPx, dpiScale: 1.0);
    }

    [Fact]
    public void HotspotEqualsTargetPx()
    {
        // PNT-04 decision: the tip is the hotspot. The Mac offsets the whole buddy 8 pt right and
        // 12 pt below the target before clamping (OverlayWindow.swift:465-474); Windows must apply
        // no offset to the glyph at all, so the hotspot equals the target in physical px for every
        // target — mid-screen, near each work-area edge, and even outside the work area entirely
        // (the mapper clamps to the monitor, so a target can land in the taskbar band), where only
        // the bubble is pulled back inside.
        VirtualScreenPoint[] targetPointsPx =
        [
            new(X: 960, Y: 500),    // mid work area
            new(X: 1880, Y: 500),   // against the right inset line
            new(X: 400, Y: 1000),   // against the bottom inset line
            new(X: 10, Y: 500),     // nearer than the inset to the left edge
            new(X: 400, Y: 5),      // nearer than the inset to the top edge
            new(X: 1919, Y: 1079),  // the monitor's bottom-right corner, outside rcWork
        ];
        foreach (VirtualScreenPoint targetPointPx in targetPointsPx)
        {
            OverlayPlacementResult placement = PlaceOnPrimaryWorkArea(targetPointPx);
            Assert.Equal(targetPointPx, placement.HotspotPx);
        }

        // The same exactness holds on the negative-origin secondary monitor at 150% DPI, whose
        // quarter-screen target is hand-computed in NegativeOriginMixedDpi below.
        OverlayPlacementResult mixedDpiPlacement = OverlayPlacement.Place(
            targetPointPx: new VirtualScreenPoint(-2160, 405),
            workAreaPx: new PhysicalPixelRect(Left: -2880, Top: 0, Width: 2880, Height: 1548),
            dpiScale: 1.5,
            bubbleSizeDip: new DipSize(Width: 120, Height: 40));
        Assert.Equal(new VirtualScreenPoint(-2160, 405), mixedDpiPlacement.HotspotPx);
    }

    [Fact]
    public void NegativeOriginMixedDpi()
    {
        // The ADR acceptance-8 rig's secondary monitor: a 1920x1080 panel at 150% scaling, placed
        // left of the primary, so its physical monitor rect is 2880x1620 at (-2880, 0) and rcWork is
        // (-2880, 0, 2880, 1548) after a 48-DIP (72 px) taskbar. The target is PNT-04's
        // quarter-screen point (-2880 + 0.25 * 2880, 0 + 0.25 * 1620) = (-2160, 405) — the same
        // physical point the secondary-150pct-left-negative mapping case lands on.
        PhysicalPixelRect secondaryWorkAreaPx = new(Left: -2880, Top: 0, Width: 2880, Height: 1548);
        VirtualScreenPoint targetPointPx = new(-2160, 405);

        // At 150%: inset 30 px, gaps 12/18 px, glyph 27 px, and the 120x40-DIP bubble becomes
        // 180x60 px; the inset lines sit at x -2850/-30 and y 30/1518. Hand-computed, per edge:
        //   hotspot:  (-2160, 405) exactly — no 8/12 pt offset on the glyph.
        //   bubble:   default right/below — left -2160 + 12 = -2148 (far edge -1968, inside the -30
        //             inset line), top 405 + 18 = 423 (far edge 483, inside the 1518 line). No flip,
        //             no clamp.
        //   window:   union of the glyph [-2160, 405, 27, 27] and the bubble [-2148, 423, 180, 60]:
        //             left -2160, top 405, right -1968, bottom 483 -> (-2160, 405, 192, 78).
        OverlayPlacementResult placement = OverlayPlacement.Place(
            targetPointPx: targetPointPx,
            workAreaPx: secondaryWorkAreaPx,
            dpiScale: 1.5,
            bubbleSizeDip: new DipSize(Width: 120, Height: 40));

        Assert.Equal(targetPointPx, placement.HotspotPx);
        Assert.Equal(new VirtualScreenRect(Left: -2160, Top: 405, Width: 192, Height: 78), placement.WindowRectPx);
        Assert.Equal(new VirtualScreenRect(Left: -2148, Top: 423, Width: 180, Height: 60), placement.BubbleRectPx);
        Assert.Equal(OverlayBubbleHorizontalSide.RightOfHotspot, placement.BubbleHorizontalSide);
        Assert.Equal(OverlayBubbleVerticalSide.BelowHotspot, placement.BubbleVerticalSide);
        AssertBubbleStaysTwentyDipInsideWorkArea(placement, secondaryWorkAreaPx, dpiScale: 1.5);
    }

    private static OverlayPlacementResult PlaceOnPrimaryWorkArea(VirtualScreenPoint targetPointPx) =>
        OverlayPlacement.Place(
            targetPointPx: targetPointPx,
            workAreaPx: PrimaryWorkAreaPx,
            dpiScale: 1.0,
            bubbleSizeDip: BubbleSizeDip);

    /// <summary>The invariant behind every flip and clamp: all four bubble edges stay at least 20 DIP inside rcWork.</summary>
    private static void AssertBubbleStaysTwentyDipInsideWorkArea(OverlayPlacementResult placement, PhysicalPixelRect workAreaPx, double dpiScale)
    {
        double insetPx = OverlayPlacement.WorkAreaInsetDip * dpiScale;
        double insetLeftEdgePx = workAreaPx.Left + insetPx;
        double insetTopEdgePx = workAreaPx.Top + insetPx;
        double insetRightEdgePx = workAreaPx.Left + workAreaPx.Width - insetPx;
        double insetBottomEdgePx = workAreaPx.Top + workAreaPx.Height - insetPx;

        Assert.True(placement.BubbleRectPx.Left >= insetLeftEdgePx, $"bubble left edge {placement.BubbleRectPx.Left} is not at least 20 DIP inside rcWork's left edge {insetLeftEdgePx}");
        Assert.True(placement.BubbleRectPx.Top >= insetTopEdgePx, $"bubble top edge {placement.BubbleRectPx.Top} is not at least 20 DIP inside rcWork's top edge {insetTopEdgePx}");
        Assert.True(placement.BubbleRectPx.Right <= insetRightEdgePx, $"bubble right edge {placement.BubbleRectPx.Right} is not at least 20 DIP inside rcWork's right edge {insetRightEdgePx}");
        Assert.True(placement.BubbleRectPx.Bottom <= insetBottomEdgePx, $"bubble bottom edge {placement.BubbleRectPx.Bottom} is not at least 20 DIP inside rcWork's bottom edge {insetBottomEdgePx}");
    }
}
