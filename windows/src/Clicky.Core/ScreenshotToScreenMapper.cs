namespace Clicky.Core;

/// <summary>Width and height of an image in pixels.</summary>
public readonly record struct PixelSize(int Width, int Height);

/// <summary>A monitor rectangle in virtual-screen physical pixels (top-left origin; may be negative).</summary>
public readonly record struct PhysicalPixelRect(int Left, int Top, int Width, int Height);

/// <summary>A position on the virtual screen in physical pixels (top-left origin).</summary>
public readonly record struct VirtualScreenPoint(double X, double Y);

/// <summary>
/// Seam 3: maps a POINT coordinate from screenshot space to the virtual screen. Merges the two Swift
/// copies (CompanionManager.swift:638-677 and :999-1013) for Windows' top-left-origin, physical-pixel
/// coordinates, so the AppKit y-flip at :667-674 has no counterpart here.
/// </summary>
public static class ScreenshotToScreenMapper
{
    /// <summary>
    /// Picks which capture the tag refers to (CompanionManager.swift:640-646): a 1-based screen number
    /// in range wins, anything else (missing, 0, too large) falls back to the cursor's screen.
    /// </summary>
    public static int SelectTargetScreenIndex(long? screenNumber, int captureCount, int cursorScreenIndex)
    {
        if (screenNumber is long requestedScreenNumber && requestedScreenNumber >= 1 && requestedScreenNumber <= captureCount)
        {
            return (int)(requestedScreenNumber - 1);
        }
        return cursorScreenIndex;
    }

    /// <summary>Clamps the point to the screenshot (inclusive at the far edge, as Swift does), then scales it onto the monitor.</summary>
    public static VirtualScreenPoint MapToVirtualScreen(
        ScreenshotPoint screenshotPoint,
        PixelSize screenshotSize,
        PhysicalPixelRect monitorBounds)
    {
        double clampedX = Math.Max(0, Math.Min(screenshotPoint.X, screenshotSize.Width));
        double clampedY = Math.Max(0, Math.Min(screenshotPoint.Y, screenshotSize.Height));

        double monitorLocalX = clampedX * ((double)monitorBounds.Width / screenshotSize.Width);
        double monitorLocalY = clampedY * ((double)monitorBounds.Height / screenshotSize.Height);

        return new VirtualScreenPoint(monitorBounds.Left + monitorLocalX, monitorBounds.Top + monitorLocalY);
    }
}
