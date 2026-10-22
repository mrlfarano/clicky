namespace Clicky.Core;

/// <summary>Which horizontal side of the hotspot the text bubble occupies.</summary>
public enum OverlayBubbleHorizontalSide
{
    /// <summary>The Mac default: the bubble sits to the right of the cursor (OverlayWindow.swift:288).</summary>
    RightOfHotspot,

    /// <summary>Flipped: the bubble sits to the left of the hotspot because its right side has no room.</summary>
    LeftOfHotspot,
}

/// <summary>Which vertical side of the hotspot the text bubble occupies.</summary>
public enum OverlayBubbleVerticalSide
{
    /// <summary>The Mac default: the bubble sits below the cursor (OverlayWindow.swift:288).</summary>
    BelowHotspot,

    /// <summary>Flipped: the bubble sits above the hotspot because below it has no room.</summary>
    AboveHotspot,
}

/// <summary>A width and height in DIPs — WPF's device-independent units, before the monitor's DPI scale is applied.</summary>
public readonly record struct DipSize(double Width, double Height);

/// <summary>
/// A rectangle on the virtual screen in physical pixels, kept as exact doubles: scaling the DIP
/// constants by a monitor's DPI produces fractional pixels at some scales, and rounding belongs at
/// the Win32 boundary (SetWindowPos in the shell), not inside the placement math.
/// </summary>
public readonly record struct VirtualScreenRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

/// <summary>Everything the shell needs to position the overlay for one target, in physical pixels on the virtual screen.</summary>
/// <param name="HotspotPx">
/// Where the glyph's tip landed. Always exactly the requested target (decision PNT-04, docs/03 WS-01:
/// the tip is the hotspot — the Mac instead offsets the whole buddy 8 pt right / 12 pt below the
/// target, OverlayWindow.swift:465-468).
/// </param>
/// <param name="WindowRectPx">
/// The union of the glyph rectangle and the clamped bubble rectangle. The shell rounds these doubles
/// when calling SetWindowPos; because the tip is the window's top-left pixel (CursorOverlayWindow.cs),
/// the unrounded left/top equals the hotspot whenever the bubble did not flip or clamp past it.
/// </param>
/// <param name="BubbleRectPx">Where the text bubble sits after flipping and clamping.</param>
/// <param name="BubbleHorizontalSide">The horizontal side of the hotspot the bubble was placed on.</param>
/// <param name="BubbleVerticalSide">The vertical side of the hotspot the bubble was placed on.</param>
public sealed record OverlayPlacementResult(
    VirtualScreenPoint HotspotPx,
    VirtualScreenRect WindowRectPx,
    VirtualScreenRect BubbleRectPx,
    OverlayBubbleHorizontalSide BubbleHorizontalSide,
    OverlayBubbleVerticalSide BubbleVerticalSide);

/// <summary>
/// Pure overlay placement for pointing (task T-003, parity PNT-04, workstream WS-01). Given a target
/// in physical pixels and the target monitor's work area, it puts the glyph's tip exactly on the
/// target, moves the Mac's 8/12 pt buddy offset onto the bubble as the hotspot-to-bubble gap
/// (OverlayWindow.swift:465-468), flips the bubble to the side with room, and clamps every bubble
/// edge 20 DIP inside rcWork (OverlayWindow.swift:471-474). The Mac clamps its whole buddy inside the
/// screen; on Windows the hotspot must stay exactly on the target (PNT-04's 4-px tolerance), so only
/// the bubble obeys the inset.
/// </summary>
public static class OverlayPlacement
{
    /// <summary>
    /// OverlayWindow.swift:471-474 keeps the Mac buddy at least 20 pt inside the screen; Windows keeps
    /// every bubble edge at least this many DIP inside the monitor's work area instead.
    /// </summary>
    public const double WorkAreaInsetDip = 20;

    /// <summary>
    /// The horizontal gap between the hotspot and the bubble's near edge. Decision PNT-04: the Mac's
    /// 8-pt horizontal buddy offset (OverlayWindow.swift:465-466) moves from the glyph to the bubble.
    /// </summary>
    public const double BubbleHorizontalGapFromHotspotDip = 8;

    /// <summary>
    /// The vertical gap between the hotspot and the bubble's near edge. Decision PNT-04: the Mac's
    /// 12-pt vertical buddy offset (OverlayWindow.swift:467-468) moves from the glyph to the bubble.
    /// </summary>
    public const double BubbleVerticalGapFromHotspotDip = 12;

    /// <summary>
    /// The buddy glyph's side length in DIP, drawn with its tip at the frame's top-left corner — the
    /// shell's triangle (0,0),(18,7),(7,18) (CursorOverlayWindow.cs) keeps the hotspot offset zero at
    /// every DPI. The Mac's triangle frame is 16x16 pt (OverlayWindow.swift:307) but is centered on
    /// its offset target rather than tipped on it.
    /// </summary>
    public const double GlyphSideLengthDip = 18;

    /// <summary>
    /// Places the overlay for one pointing target. The hotspot lands exactly on
    /// <paramref name="targetPointPx"/>; the bubble starts at the Mac's side (right of and below the
    /// cursor, OverlayWindow.swift:288), flips horizontally when its far edge would cross the work
    /// area's right inset line and vertically when it would cross the bottom inset line, and is then
    /// clamped so all four edges stay at least <see cref="WorkAreaInsetDip"/> DIP inside
    /// <paramref name="workAreaPx"/> — which also covers targets nearer than the inset to the left or
    /// top edge, targets outside the work area entirely (the mapper can land one in the taskbar band),
    /// and bubbles wider than the room left on either side.
    /// </summary>
    /// <param name="targetPointPx">The point to point at, in physical pixels on the virtual screen (from ScreenshotToScreenMapper).</param>
    /// <param name="workAreaPx">The target monitor's work area (rcWork from GetMonitorInfo), in physical pixels under Per-Monitor-V2 DPI.</param>
    /// <param name="dpiScale">The target monitor's DPI scale (1.0 at 100%, 1.5 at 150%), which converts the DIP constants to physical pixels.</param>
    /// <param name="bubbleSizeDip">The text bubble's measured size in DIPs (WPF measures its content in DIPs).</param>
    public static OverlayPlacementResult Place(
        VirtualScreenPoint targetPointPx,
        PhysicalPixelRect workAreaPx,
        double dpiScale,
        DipSize bubbleSizeDip)
    {
        double workAreaInsetPx = WorkAreaInsetDip * dpiScale;
        double bubbleWidthPx = bubbleSizeDip.Width * dpiScale;
        double bubbleHeightPx = bubbleSizeDip.Height * dpiScale;
        double bubbleHorizontalGapPx = BubbleHorizontalGapFromHotspotDip * dpiScale;
        double bubbleVerticalGapPx = BubbleVerticalGapFromHotspotDip * dpiScale;
        double glyphSidePx = GlyphSideLengthDip * dpiScale;

        // The four lines every bubble edge must stay inside: the work area inset by 20 DIP on every side.
        double insetWorkAreaLeftPx = workAreaPx.Left + workAreaInsetPx;
        double insetWorkAreaTopPx = workAreaPx.Top + workAreaInsetPx;
        double insetWorkAreaRightPx = workAreaPx.Left + workAreaPx.Width - workAreaInsetPx;
        double insetWorkAreaBottomPx = workAreaPx.Top + workAreaPx.Height - workAreaInsetPx;

        // Horizontal: the bubble starts right of the hotspot (the Mac's side, OverlayWindow.swift:288)
        // and flips to the left only when its far edge would cross the right inset line, keeping the
        // same 8-DIP gap on either side of the hotspot.
        OverlayBubbleHorizontalSide bubbleHorizontalSide = OverlayBubbleHorizontalSide.RightOfHotspot;
        double bubbleLeftPx = targetPointPx.X + bubbleHorizontalGapPx;
        if (bubbleLeftPx + bubbleWidthPx > insetWorkAreaRightPx)
        {
            bubbleHorizontalSide = OverlayBubbleHorizontalSide.LeftOfHotspot;
            bubbleLeftPx = targetPointPx.X - bubbleHorizontalGapPx - bubbleWidthPx;
        }

        // Vertical: the same rule on the other axis — start below the hotspot, flip above when the
        // bubble would cross the bottom inset line.
        OverlayBubbleVerticalSide bubbleVerticalSide = OverlayBubbleVerticalSide.BelowHotspot;
        double bubbleTopPx = targetPointPx.Y + bubbleVerticalGapPx;
        if (bubbleTopPx + bubbleHeightPx > insetWorkAreaBottomPx)
        {
            bubbleVerticalSide = OverlayBubbleVerticalSide.AboveHotspot;
            bubbleTopPx = targetPointPx.Y - bubbleVerticalGapPx - bubbleHeightPx;
        }

        // The clamp, not the flip, holds the inset at the left and top edges (flipping left or up only
        // moves the bubble further out there) and whenever even the flipped side has no room. The Max
        // wins when the bubble is wider than the space between the inset lines, which leaves it
        // flush with the left/top inset line and overflowing the far one — the only defined outcome
        // for a bubble bigger than the work area minus both insets.
        bubbleLeftPx = Math.Max(insetWorkAreaLeftPx, Math.Min(bubbleLeftPx, insetWorkAreaRightPx - bubbleWidthPx));
        bubbleTopPx = Math.Max(insetWorkAreaTopPx, Math.Min(bubbleTopPx, insetWorkAreaBottomPx - bubbleHeightPx));

        VirtualScreenRect bubbleRectPx = new(bubbleLeftPx, bubbleTopPx, bubbleWidthPx, bubbleHeightPx);
        VirtualScreenRect glyphRectPx = new(targetPointPx.X, targetPointPx.Y, glyphSidePx, glyphSidePx);

        // The window is exactly the union of the glyph and the clamped bubble, so wherever the bubble
        // flipped to, one content-sized window holds both.
        double windowLeftPx = Math.Min(glyphRectPx.Left, bubbleRectPx.Left);
        double windowTopPx = Math.Min(glyphRectPx.Top, bubbleRectPx.Top);
        double windowRightPx = Math.Max(glyphRectPx.Right, bubbleRectPx.Right);
        double windowBottomPx = Math.Max(glyphRectPx.Bottom, bubbleRectPx.Bottom);
        VirtualScreenRect windowRectPx = new(windowLeftPx, windowTopPx, windowRightPx - windowLeftPx, windowBottomPx - windowTopPx);

        return new OverlayPlacementResult(
            HotspotPx: targetPointPx,
            WindowRectPx: windowRectPx,
            BubbleRectPx: bubbleRectPx,
            BubbleHorizontalSide: bubbleHorizontalSide,
            BubbleVerticalSide: bubbleVerticalSide);
    }
}
