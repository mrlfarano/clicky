using System.ComponentModel;
using Clicky.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Clicky.Windows;

/// <param name="Bounds">rcMonitor in physical pixels (Per-Monitor V2), top-left origin.</param>
/// <param name="WorkArea">rcWork in physical pixels: rcMonitor minus the taskbar band — the rectangle OverlayPlacement flips and clamps the bubble inside (T-003/T-004).</param>
/// <param name="EffectiveDpi">Logged for mixed-DPI QA runs; the overlay's placement converts DIPs with <see cref="DpiScale"/>.</param>
internal sealed record DisplayMonitor(HMONITOR Handle, string DeviceName, PhysicalPixelRect Bounds, uint EffectiveDpi, bool IsCursorMonitor, PhysicalPixelRect WorkArea)
{
    /// <summary>The monitor's DPI as a scale factor (1.0 at 96 DPI, 1.5 at 150%); 1.0 when the DPI could not be read.</summary>
    public double DpiScale => EffectiveDpi == 0 ? 1.0 : EffectiveDpi / 96.0;
}

internal static class DisplayMonitorEnumerator
{
    /// <summary>
    /// Every monitor with the cursor's monitor first and the rest in enumeration order, matching the
    /// macOS ordering (CompanionScreenCaptureUtility.swift:60-68), so "screen 1" is always where the cursor is.
    /// </summary>
    public static unsafe IReadOnlyList<DisplayMonitor> EnumerateCursorMonitorFirst(System.Drawing.Point cursorPositionPx)
    {
        HMONITOR cursorMonitorHandle = PInvoke.MonitorFromPoint(cursorPositionPx, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        var monitorHandles = new List<HMONITOR>();
        MONITORENUMPROC collectMonitorHandle = (monitorHandle, _, _, _) =>
        {
            monitorHandles.Add(monitorHandle);
            return true;
        };
        if (!PInvoke.EnumDisplayMonitors(default, (RECT*)null, collectMonitorHandle, default))
        {
            throw new Win32Exception("EnumDisplayMonitors failed");
        }

        return monitorHandles
            .Select(monitorHandle => DescribeMonitorForHandle(monitorHandle, isCursorMonitor: monitorHandle == cursorMonitorHandle))
            // OrderBy is stable, so the non-cursor monitors keep their enumeration order.
            .OrderBy(monitor => monitor.IsCursorMonitor ? 0 : 1)
            .ToList();
    }

    public static string ReadDeviceName(HMONITOR monitorHandle) => ReadMonitorInfo(monitorHandle).szDevice.ToString();

    /// <summary>
    /// Full description of one monitor handle. Internal so the overlay can describe the monitor a device-name
    /// lookup fell back to (CursorOverlayWindow.ResolveTargetMonitor).
    /// </summary>
    internal static DisplayMonitor DescribeMonitorForHandle(HMONITOR monitorHandle, bool isCursorMonitor)
    {
        MONITORINFOEXW monitorInfo = ReadMonitorInfo(monitorHandle);
        RECT monitorRect = monitorInfo.monitorInfo.rcMonitor;
        RECT workAreaRect = monitorInfo.monitorInfo.rcWork;
        uint effectiveDpi = PInvoke.GetDpiForMonitor(monitorHandle, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded
            ? dpiX
            : 0;
        return new DisplayMonitor(
            monitorHandle,
            monitorInfo.szDevice.ToString(),
            new PhysicalPixelRect(monitorRect.left, monitorRect.top, monitorRect.right - monitorRect.left, monitorRect.bottom - monitorRect.top),
            effectiveDpi,
            isCursorMonitor,
            new PhysicalPixelRect(workAreaRect.left, workAreaRect.top, workAreaRect.right - workAreaRect.left, workAreaRect.bottom - workAreaRect.top));
    }

    private static unsafe MONITORINFOEXW ReadMonitorInfo(HMONITOR monitorHandle)
    {
        var monitorInfo = new MONITORINFOEXW();
        monitorInfo.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (!PInvoke.GetMonitorInfo(monitorHandle, (MONITORINFO*)&monitorInfo))
        {
            throw new Win32Exception("GetMonitorInfo failed");
        }
        return monitorInfo;
    }
}
