namespace Clicky.Core;

/// <summary>One monitor as the platform enumerates it: its size in physical pixels and whether the cursor is on it.</summary>
public readonly record struct CaptureMonitor(PixelSize MonitorSize, bool IsCursorMonitor);

/// <param name="MonitorIndex">Index into the enumerated monitors passed to <see cref="ScreenCapturePlanning.PlanCaptures"/>.</param>
/// <param name="Label">The full label sent after the image, dimension suffix included.</param>
public readonly record struct PlannedCapture(int MonitorIndex, PixelSize ScreenshotSize, string Label);

/// <summary>
/// The platform-neutral parts of CompanionScreenCaptureUtility.swift: which order the screens are sent in,
/// how big each screenshot is and how it is labelled for Claude. The labels tell Claude which coordinate
/// space to answer in.
/// </summary>
public static class ScreenCapturePlanning
{
    public const int ScreenshotLongEdgePixels = 1280;

    /// <summary>
    /// Port of CompanionScreenCaptureUtility.swift:60-111: the cursor's monitor first, the rest in enumeration
    /// order (Swift's sort leaves their order unspecified; Windows keeps EnumDisplayMonitors order), each with
    /// its screenshot size and label. Screen numbers in the labels follow this order, so "screen 1" is the cursor's.
    /// </summary>
    public static IReadOnlyList<PlannedCapture> PlanCaptures(IReadOnlyList<CaptureMonitor> enumeratedMonitors) =>
        enumeratedMonitors
            .Select((monitor, monitorIndex) => (Monitor: monitor, MonitorIndex: monitorIndex))
            // OrderBy is stable, so the non-cursor monitors keep their enumeration order.
            .OrderBy(indexedMonitor => indexedMonitor.Monitor.IsCursorMonitor ? 0 : 1)
            .Select((indexedMonitor, screenIndex) =>
            {
                PixelSize screenshotSize = ComputeScreenshotSize(indexedMonitor.Monitor.MonitorSize.Width, indexedMonitor.Monitor.MonitorSize.Height);
                return new PlannedCapture(
                    indexedMonitor.MonitorIndex,
                    screenshotSize,
                    BuildScreenLabel(screenIndex, enumeratedMonitors.Count, indexedMonitor.Monitor.IsCursorMonitor, screenshotSize));
            })
            .ToList();

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
