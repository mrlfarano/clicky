using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// The blue cursor and its text bubble: one small, content-sized topmost window that MOVES to each
/// pointing target, never a full-screen window per monitor (ADR 001 "UI framework", audit risk 1).
/// Every position and size is applied with SetWindowPos in physical pixels, so WPF's DIP conversion
/// never touches the window's position (ADR 001 tracer item 6), and only when the placement actually
/// changed — stationary rendering frames issue zero SetWindowPos calls. The bubble's flip/clamp layout
/// comes from Core's OverlayPlacement (T-003), and the target monitor is matched by device name,
/// because monitor indices shift when the monitor set changes. Click-through, never takes focus (E1).
/// </summary>
internal sealed class CursorOverlayWindow : Window
{
    private const int WindowMessageDpiChanged = 0x02E0; // WM_DPICHANGED
    private const int WindowMessageDisplayChange = 0x007E; // WM_DISPLAYCHANGE

    private readonly Polygon cursorTrianglePolygon;
    private readonly TextBlock bubbleTextBlock;
    private readonly Border bubbleBorder;
    private readonly Canvas overlayCanvas;
    private HWND windowHandle;

    // The pointing anchor the current placement is computed around; OverlayPlacement puts the glyph's
    // tip (the hotspot) exactly on it, so the hotspot offset from the window's origin is placement-derived.
    private System.Drawing.Point? pointingTargetPx;
    private string? pointingTargetMonitorDeviceName;

    // The monitor list the device-name lookup resolves against; re-read on WM_DISPLAYCHANGE so a changed
    // resolution or monitor set gives placement fresh work areas and DPIs.
    private List<DisplayMonitor> cachedMonitors = [];

    // The placement OverlayPlacement.Place produced for the current target and bubble size, and the
    // physical-integer geometry actually handed to SetWindowPos the last time one was applied. The
    // render loop compares the two and only calls SetWindowPos when they differ.
    private OverlayPlacementResult? pendingPlacement;
    private double pendingPlacementDpiScale = 1.0;
    private AppliedWindowPlacement? lastAppliedWindowPlacement;

    public CursorOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        // No fixed Width/Height: the window is content-sized. Every placement sets the physical rect via
        // SetWindowPos and a matching DIP size, so the window is exactly the union of the glyph and the
        // clamped bubble (OverlayPlacementResult.WindowRectPx) rather than a fixed transparent sheet.

        cursorTrianglePolygon = new Polygon
        {
            // The tip at (0,0) is the hotspot; placement moves the whole polygon so the tip lands on the target.
            Points = new PointCollection { new Point(0, 0), new Point(18, 7), new Point(7, 18) },
            Fill = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
        };
        bubbleTextBlock = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
        };
        bubbleBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            Child = bubbleTextBlock,
            Visibility = Visibility.Collapsed,
        };
        overlayCanvas = new Canvas();
        overlayCanvas.Children.Add(cursorTrianglePolygon);
        overlayCanvas.Children.Add(bubbleBorder);
        Content = overlayCanvas;

        SourceInitialized += (_, _) => ApplyOverlayWindowStyles();
    }

    /// <summary>True on 19041+ where WDA_EXCLUDEFROMCAPTURE keeps the overlay out of screenshots (audit D2).</summary>
    public bool IsExcludedFromCapture { get; private set; }

    /// <summary>
    /// How many SetWindowPos calls this window issued for its own placement (WPF's internal calls
    /// excluded). Every call follows a real placement change or a mismatch WPF introduced; stationary
    /// rendering frames add none. Reported as setWindowPosCalls (T-004).
    /// </summary>
    public int SetWindowPosCallCount { get; private set; }

    /// <summary>How many times the monitor list has been (re-)read; WM_DISPLAYCHANGE must increase it (T-004).</summary>
    public int MonitorListReadCount { get; private set; }

    /// <summary>Creates the HWND without showing it, so styles and capture exclusion are in place before the first capture.</summary>
    public void CreateHiddenWindowHandle() => new WindowInteropHelper(this).EnsureHandle();

    public void ShowWithHotspotAt(System.Drawing.Point hotspotPositionPx)
    {
        SetBubbleText("");
        // Resolve the anchor's monitor up front so the very first placement clamps the bubble against that
        // monitor's work area, then move before Show to avoid a frame at the old position; enforcing after
        // Show covers WPF placing the window itself on its first Show.
        string anchorMonitorDeviceName = DisplayMonitorEnumerator.ReadDeviceName(
            PInvoke.MonitorFromPoint(hotspotPositionPx, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST));
        PointAt(hotspotPositionPx, anchorMonitorDeviceName);
        Show();
        EnforcePlacementMatchesPending();
    }

    /// <summary>
    /// Moves the overlay so the glyph's tip lands exactly on <paramref name="targetPx"/>. The monitor is
    /// matched by <paramref name="targetMonitorDeviceName"/> (indices shift when monitors change); its work
    /// area and DPI feed Core's OverlayPlacement, which keeps the hotspot on the target and flips/clamps
    /// the bubble 20 DIP inside the work area (T-003, parity PNT-04/PNT-05).
    /// </summary>
    public void PointAt(System.Drawing.Point targetPx, string targetMonitorDeviceName)
    {
        pointingTargetPx = targetPx;
        pointingTargetMonitorDeviceName = targetMonitorDeviceName;
        RecomputePendingPlacement();
        // WPF moves the window itself on the first Show and around DPI changes; enforce the placement after
        // the eager apply so the read-back position is the requested one.
        EnforcePlacementMatchesPending();
    }

    public void SetBubbleText(string bubbleText)
    {
        bubbleTextBlock.Text = bubbleText;
        bubbleBorder.Visibility = bubbleText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        // The bubble's measured size is part of the placement, so re-place around the current target for
        // the new size: the window grows with the streaming text instead of clipping it.
        RecomputePendingPlacement();
    }

    /// <summary>
    /// The hotspot (the glyph's tip) in physical screen pixels: the window rect plus the placement-derived
    /// offset from the window's origin, which is zero only while the bubble sits right of and below the tip.
    /// </summary>
    public System.Drawing.Point ReadHotspotPositionPx()
    {
        PInvoke.GetWindowRect(windowHandle, out RECT windowRect);
        AppliedWindowPlacement appliedPlacement = lastAppliedWindowPlacement ?? default;
        return new System.Drawing.Point(
            windowRect.left + appliedPlacement.HotspotOffsetFromWindowLeftPx,
            windowRect.top + appliedPlacement.HotspotOffsetFromWindowTopPx);
    }

    public string ReadCurrentMonitorDeviceName() =>
        DisplayMonitorEnumerator.ReadDeviceName(PInvoke.MonitorFromWindow(windowHandle, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST));

    /// <summary>
    /// Test seam (T-004): runs exactly one iteration of the CompositionTarget.Rendering-driven placement
    /// loop, so a stationary window's zero-SetWindowPos behaviour is observable without a live render loop.
    /// </summary>
    internal void SimulateCompositionTargetRenderingTickForTest() => ApplyPendingPlacementIfChanged();

    /// <summary>
    /// Recomputes the placement for the current target and bubble size. <paramref name="dpiScaleOverride"/>
    /// carries the DPI a WM_DPICHANGED just announced; normally the scale comes from the target monitor.
    /// </summary>
    private void RecomputePendingPlacement(double? dpiScaleOverride = null)
    {
        if (pointingTargetPx is not System.Drawing.Point targetPx)
        {
            return;
        }
        DisplayMonitor targetMonitor = ResolveTargetMonitor(targetPx, pointingTargetMonitorDeviceName);
        double placementDpiScale = dpiScaleOverride ?? targetMonitor.DpiScale;
        pendingPlacementDpiScale = placementDpiScale;
        pendingPlacement = OverlayPlacement.Place(
            new VirtualScreenPoint(targetPx.X, targetPx.Y),
            targetMonitor.WorkArea,
            placementDpiScale,
            MeasureBubbleSizeDip());
        // Eager apply so the move is in effect before the caller reads the position back; the render loop
        // calls the same change-gated applier, which makes this a no-op when nothing changed.
        ApplyPendingPlacementIfChanged();
    }

    /// <summary>
    /// Per-monitor device-name matching: monitor indices shift when the monitor set changes, device names
    /// do not. Falls back to the monitor nearest the target when the name is unknown (including the
    /// missing-screen fallback's cursor monitor).
    /// </summary>
    private DisplayMonitor ResolveTargetMonitor(System.Drawing.Point targetPx, string? targetMonitorDeviceName)
    {
        DisplayMonitor? matchedMonitor = cachedMonitors.FirstOrDefault(monitor => monitor.DeviceName == targetMonitorDeviceName);
        if (matchedMonitor is null)
        {
            // Not cached (first placement) or the monitor set changed: re-read, then try the name once more.
            RefreshCachedMonitorList(targetPx);
            matchedMonitor = cachedMonitors.FirstOrDefault(monitor => monitor.DeviceName == targetMonitorDeviceName);
        }
        return matchedMonitor
            ?? DisplayMonitorEnumerator.DescribeMonitorForHandle(
                PInvoke.MonitorFromPoint(targetPx, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST),
                isCursorMonitor: false);
    }

    private void RefreshCachedMonitorList(System.Drawing.Point referencePointPx)
    {
        cachedMonitors = [.. DisplayMonitorEnumerator.EnumerateMonitors(referencePointPx)];
        MonitorListReadCount++;
    }

    private DipSize MeasureBubbleSizeDip()
    {
        if (bubbleTextBlock.Text.Length == 0)
        {
            return new DipSize(0, 0);
        }
        // Measure honours the TextBlock's MaxWidth even under infinite available size, so the bubble wraps
        // at 360 DIP; a directly-measured collapsed element still reports its desired size.
        bubbleBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new DipSize(bubbleBorder.DesiredSize.Width, bubbleBorder.DesiredSize.Height);
    }

    /// <summary>
    /// The placement loop's core: call SetWindowPos only when the pending placement differs from what was
    /// last applied. CompositionTarget.Rendering drives this every rendered frame, so a stationary window
    /// (the common case once the cursor has landed) issues zero calls.
    /// </summary>
    private void ApplyPendingPlacementIfChanged()
    {
        if (pendingPlacement is not OverlayPlacementResult placement)
        {
            return;
        }
        AppliedWindowPlacement desiredPlacement = RoundPlacementToPhysicalPixels(placement);
        if (desiredPlacement == lastAppliedWindowPlacement)
        {
            return;
        }
        ApplyPlacement(desiredPlacement, placement);
    }

    private static AppliedWindowPlacement RoundPlacementToPhysicalPixels(OverlayPlacementResult placement)
    {
        int windowLeftPx = RoundToPhysicalPixel(placement.WindowRectPx.Left);
        int windowTopPx = RoundToPhysicalPixel(placement.WindowRectPx.Top);
        return new AppliedWindowPlacement(
            windowLeftPx,
            windowTopPx,
            RoundToPhysicalPixel(placement.WindowRectPx.Width),
            RoundToPhysicalPixel(placement.WindowRectPx.Height),
            // OverlayPlacement keeps the hotspot exactly on the (integral) target, so this offset is exact
            // integer arithmetic and the read-back hotspot equals the target with no tolerance.
            RoundToPhysicalPixel(placement.HotspotPx.X) - windowLeftPx,
            RoundToPhysicalPixel(placement.HotspotPx.Y) - windowTopPx);
    }

    private static int RoundToPhysicalPixel(double pixelValue) => (int)Math.Round(pixelValue, MidpointRounding.AwayFromZero);

    private void ApplyPlacement(AppliedWindowPlacement desiredPlacement, OverlayPlacementResult placement)
    {
        // Content offsets in DIPs: the placement is physical, and WPF lays content out in DIPs scaled by the
        // window's DPI, so dividing by the placement's scale converts physical offsets into content
        // coordinates wherever the bubble flipped to.
        double placementDpiScale = pendingPlacementDpiScale;
        Canvas.SetLeft(cursorTrianglePolygon, (placement.HotspotPx.X - placement.WindowRectPx.Left) / placementDpiScale);
        Canvas.SetTop(cursorTrianglePolygon, (placement.HotspotPx.Y - placement.WindowRectPx.Top) / placementDpiScale);
        Canvas.SetLeft(bubbleBorder, (placement.BubbleRectPx.Left - placement.WindowRectPx.Left) / placementDpiScale);
        Canvas.SetTop(bubbleBorder, (placement.BubbleRectPx.Top - placement.WindowRectPx.Top) / placementDpiScale);

        // Keep WPF's own DIP bookkeeping aligned with the physical size about to be enforced, so WPF's
        // layout pass does not fight the SetWindowPos below over the window's size.
        Width = placement.WindowRectPx.Width / placementDpiScale;
        Height = placement.WindowRectPx.Height / placementDpiScale;

        // Physical pixels straight to Win32 (no SWP_NOSIZE: the window is content-sized), which is what
        // keeps WPF's DIP conversion away from the position on mixed-DPI rigs.
        PInvoke.SetWindowPos(
            windowHandle,
            default,
            desiredPlacement.LeftPx,
            desiredPlacement.TopPx,
            desiredPlacement.WidthPx,
            desiredPlacement.HeightPx,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        SetWindowPosCallCount++;
        lastAppliedWindowPlacement = desiredPlacement;
    }

    /// <summary>
    /// WPF moves the window itself on the first Show and around WM_DPICHANGED (it takes Windows' suggested
    /// rectangle); when the actual window rect no longer matches what was applied, re-apply the placement.
    /// </summary>
    private void EnforcePlacementMatchesPending()
    {
        if (pendingPlacement is not OverlayPlacementResult placement || lastAppliedWindowPlacement is null)
        {
            return;
        }
        for (int enforceAttempt = 0; enforceAttempt < 2; enforceAttempt++)
        {
            AppliedWindowPlacement appliedPlacement = lastAppliedWindowPlacement ?? default;
            PInvoke.GetWindowRect(windowHandle, out RECT actualWindowRect);
            bool actualMatchesApplied =
                actualWindowRect.left == appliedPlacement.LeftPx
                && actualWindowRect.top == appliedPlacement.TopPx
                && actualWindowRect.right - actualWindowRect.left == appliedPlacement.WidthPx
                && actualWindowRect.bottom - actualWindowRect.top == appliedPlacement.HeightPx;
            if (actualMatchesApplied)
            {
                return;
            }
            ApplyPlacement(appliedPlacement, placement);
        }
    }

    private void ApplyOverlayWindowStyles()
    {
        windowHandle = (HWND)new WindowInteropHelper(this).Handle;

        // Layered + transparent makes every pixel click-through; no-activate and tool-window keep it out of
        // focus, Alt+Tab and the taskbar.
        const WINDOW_EX_STYLE OverlayExtendedStyles =
            WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        int currentExtendedStyles = PInvoke.GetWindowLong(windowHandle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(windowHandle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, currentExtendedStyles | (int)OverlayExtendedStyles);

        IsExcludedFromCapture = WindowCaptureExclusion.TryExcludeFromCapture(windowHandle, "overlay");

        // The placement loop (T-004): every rendered frame re-checks the pending placement and calls
        // SetWindowPos only when it actually changed. WPF raises Rendering only when something renders, so
        // a stationary, text-idle overlay performs no work at all — the small-moving-window design's CPU
        // answer to audit risk 1.
        CompositionTarget.Rendering += OnCompositionTargetRendering;

        HwndSource.FromHwnd(windowHandle).AddHook(OverlayWindowProcedure);
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs eventArgs) => ApplyPendingPlacementIfChanged();

    protected override void OnClosed(EventArgs eventArguments)
    {
        // CompositionTarget.Rendering is a static per-thread event; leaving it subscribed would keep the
        // window alive for the life of the thread.
        CompositionTarget.Rendering -= OnCompositionTargetRendering;
        base.OnClosed(eventArguments);
    }

    private IntPtr OverlayWindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WindowMessageDpiChanged && pointingTargetPx is System.Drawing.Point targetPxForDpiChange)
        {
            // Left unhandled so WPF rescales the content for the new DPI; afterwards, re-place with the new
            // scale (queued at Render priority so it runs after WPF's own handling) so the hotspot lands
            // back exactly on the target in case WPF took Windows' suggested rectangle.
            int newDpi = (int)(((nint)wParam >> 16) & 0xFFFF); // HIWORD carries the new Y DPI
            Dispatcher.InvokeAsync(() => RecomputePendingPlacement(newDpi / 96.0), System.Windows.Threading.DispatcherPriority.Render);
        }
        else if (message == WindowMessageDisplayChange && pointingTargetPx is System.Drawing.Point targetPxForDisplayChange)
        {
            // A resolution change or monitor added/removed shifts work areas and DPIs: re-read the monitor
            // list (the device-name lookup depends on it) and re-place against the fresh geometry.
            RefreshCachedMonitorList(targetPxForDisplayChange);
            RecomputePendingPlacement();
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// The physical-integer window geometry actually handed to SetWindowPos, plus the hotspot's offset from
    /// the window's top-left corner it implies: the glyph tip is at (Left + offset), so the read-back
    /// hotspot is GetWindowRect plus the offset, exact in integer pixels.
    /// </summary>
    private readonly record struct AppliedWindowPlacement(
        int LeftPx,
        int TopPx,
        int WidthPx,
        int HeightPx,
        int HotspotOffsetFromWindowLeftPx,
        int HotspotOffsetFromWindowTopPx);
}
