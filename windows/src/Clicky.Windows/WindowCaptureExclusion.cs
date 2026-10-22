using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// Keeps one of Clicky's own windows out of screenshots (CLA-01, audit D2). Every own window goes through this one
/// path, so the overlay and the panel always get the same exclude-or-hide treatment. The cursor overlay calls it
/// today; the companion panel (T-031+) does not exist yet and must call it from its SourceInitialized with its
/// HWND, and the capture step must also hide the panel whenever this returned false.
/// </summary>
internal static class WindowCaptureExclusion
{
    /// <summary>The capture options for the running OS, decided once for every own window and the capturer.</summary>
    public static CaptureOptions CurrentCaptureOptions { get; } = CaptureOptions.Decide(Environment.OSVersion.Version.Build);

    /// <summary>True when the window is now excluded; false means the caller must hide it for the capture frame.</summary>
    public static bool TryExcludeFromCapture(HWND windowHandle, string windowName) =>
        TryExcludeFromCapture(
            windowHandle,
            windowName,
            CurrentCaptureOptions,
            handleToExclude => PInvoke.SetWindowDisplayAffinity(handleToExclude, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE));

    /// <summary>The testable core: <paramref name="setExcludeFromCaptureAffinity"/> stands in for SetWindowDisplayAffinity.</summary>
    public static bool TryExcludeFromCapture(
        HWND windowHandle,
        string windowName,
        CaptureOptions captureOptions,
        Func<HWND, bool> setExcludeFromCaptureAffinity)
    {
        if (captureOptions.OwnWindowExclusion != OwnWindowCaptureExclusion.ExcludeFromCapture)
        {
            return false;
        }
        bool isExcludedFromCapture = setExcludeFromCaptureAffinity(windowHandle);
        if (!isExcludedFromCapture)
        {
            ClickyLog.Write($"{windowName}: WDA_EXCLUDEFROMCAPTURE failed; hiding the {windowName} during capture instead");
        }
        return isExcludedFromCapture;
    }
}
