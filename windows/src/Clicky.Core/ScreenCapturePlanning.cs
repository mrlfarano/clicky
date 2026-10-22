namespace Clicky.Core;

/// <summary>
/// The platform-neutral parts of CompanionScreenCaptureUtility.swift: how big each screenshot is
/// and how it is labelled for Claude. The labels tell Claude which coordinate space to answer in.
/// </summary>
public static class ScreenCapturePlanning
{
    public const int ScreenshotLongEdgePixels = 1280;

    /// <summary>
    /// Port of CompanionScreenCaptureUtility.swift:83-92. The long edge is always 1280 (smaller monitors
    /// are scaled up, as on macOS) and the short edge is truncated, not rounded: 1512x982 gives 1280x831.
    /// </summary>
    public static PixelSize ComputeScreenshotSize(int monitorWidthPixels, int monitorHeightPixels)
    {
        double aspectRatio = (double)monitorWidthPixels / monitorHeightPixels;
        if (monitorWidthPixels >= monitorHeightPixels)
        {
            return new PixelSize(ScreenshotLongEdgePixels, (int)(ScreenshotLongEdgePixels / aspectRatio));
        }
        return new PixelSize((int)(ScreenshotLongEdgePixels * aspectRatio), ScreenshotLongEdgePixels);
    }

    /// <summary>
    /// Port of CompanionScreenCaptureUtility.swift:104-111 plus the dimension suffix added at
    /// CompanionManager.swift:603-606. <paramref name="screenIndex"/> is 0-based; the label is 1-based.
    /// </summary>
    public static string BuildScreenLabel(int screenIndex, int screenCount, bool isCursorScreen, PixelSize screenshotSize)
    {
        string screenLabel;
        if (screenCount == 1)
        {
            screenLabel = "user's screen (cursor is here)";
        }
        else if (isCursorScreen)
        {
            screenLabel = $"screen {screenIndex + 1} of {screenCount} — cursor is on this screen (primary focus)";
        }
        else
        {
            screenLabel = $"screen {screenIndex + 1} of {screenCount} — secondary screen";
        }
        return $"{screenLabel} (image dimensions: {screenshotSize.Width}x{screenshotSize.Height} pixels)";
    }
}
