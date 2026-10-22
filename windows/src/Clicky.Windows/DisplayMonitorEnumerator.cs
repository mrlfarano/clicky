using System.ComponentModel;
using Clicky.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Clicky.Windows;

/// <param name="Bounds">rcMonitor in physical pixels (Per-Monitor V2), top-left origin.</param>
/// <param name="EffectiveDpi">Logged for mixed-DPI QA runs; the mapping itself only needs <paramref name="Bounds"/>.</param>
internal sealed record DisplayMonitor(HMONITOR Handle, string DeviceName, PhysicalPixelRect Bounds, uint EffectiveDpi, bool IsCursorMonitor);

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
            .Select(monitorHandle => DescribeMonitor(monitorHandle, isCursorMonitor: monitorHandle == cursorMonitorHandle))
            // OrderBy is stable, so the non-cursor monitors keep their enumeration order.
            .OrderBy(monitor => monitor.IsCursorMonitor ? 0 : 1)
            .ToList();
    }

    public static string ReadDeviceName(HMONITOR monitorHandle) => ReadMonitorInfo(monitorHandle).szDevice.ToString();

    private static DisplayMonitor DescribeMonitor(HMONITOR monitorHandle, bool isCursorMonitor)
    {
        MONITORINFOEXW monitorInfo = ReadMonitorInfo(monitorHandle);
        RECT monitorRect = monitorInfo.monitorInfo.rcMonitor;
        uint effectiveDpi = PInvoke.GetDpiForMonitor(monitorHandle, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded
            ? dpiX
            : 0;
        return new DisplayMonitor(
            monitorHandle,
            monitorInfo.szDevice.ToString(),
            new PhysicalPixelRect(monitorRect.left, monitorRect.top, monitorRect.right - monitorRect.left, monitorRect.bottom - monitorRect.top),
            effectiveDpi,
            isCursorMonitor);
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
