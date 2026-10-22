using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Clicky.Windows;

/// <summary>
/// The blue cursor and its text bubble: one small topmost window that moves, never a full-screen window per
/// monitor (ADR 001 "UI framework", audit risk 1). It is click-through and never takes focus (audit E1).
/// </summary>
internal sealed class CursorOverlayWindow : Window
{
    private const int WindowMessageDpiChanged = 0x02E0; // WM_DPICHANGED

    // The triangle's tip is the window's top-left pixel, so the hotspot offset is zero at every DPI and the
    // hotspot is simply GetWindowRect's top-left corner.
    private static readonly System.Drawing.Size HotspotOffsetPx = new(0, 0);

    private readonly TextBlock bubbleTextBlock;
    private readonly Border bubbleBorder;
    private HWND windowHandle;
    private System.Drawing.Point? hotspotTargetPx;

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
        // A fixed size (in DIPs, so WPF scales it per monitor) keeps WPF from resizing, and so moving, the
        // window as text streams in. The transparent remainder is click-through anyway.
        Width = 420;
        Height = 240;

        var cursorTriangle = new Polygon
        {
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
        var overlayCanvas = new Canvas();
        Canvas.SetLeft(bubbleBorder, 22);
        Canvas.SetTop(bubbleBorder, 20);
        overlayCanvas.Children.Add(cursorTriangle);
        overlayCanvas.Children.Add(bubbleBorder);
        Content = overlayCanvas;

        SourceInitialized += (_, _) => ApplyOverlayWindowStyles();
    }

    /// <summary>True on 19041+ where WDA_EXCLUDEFROMCAPTURE keeps the overlay out of screenshots (audit D2).</summary>
    public bool IsExcludedFromCapture { get; private set; }

    /// <summary>Creates the HWND without showing it, so styles and capture exclusion are in place before the first capture.</summary>
    public void CreateHiddenWindowHandle() => new WindowInteropHelper(this).EnsureHandle();

    public void ShowWithHotspotAt(System.Drawing.Point hotspotPositionPx)
    {
        SetBubbleText("");
        // Moving before Show avoids a frame at the old position; moving again after covers WPF placing it on Show.
        MoveHotspotTo(hotspotPositionPx);
        Show();
        MoveHotspotTo(hotspotPositionPx);
    }

    public void SetBubbleText(string bubbleText)
    {
        bubbleTextBlock.Text = bubbleText;
        bubbleBorder.Visibility = bubbleText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Positions the window with SetWindowPos in physical pixels, so WPF's DIP conversion never touches the
    /// position (ADR 001 tracer item 6). Crossing onto a monitor with another DPI makes Windows send
    /// WM_DPICHANGED during the move, and WPF's handling of it can shift the window, hence the second attempt.
    /// </summary>
    public void MoveHotspotTo(System.Drawing.Point hotspotPositionPx)
    {
        hotspotTargetPx = hotspotPositionPx;
        ApplyHotspotTarget();
    }

    public System.Drawing.Point ReadHotspotPositionPx()
    {
        PInvoke.GetWindowRect(windowHandle, out RECT windowRect);
        return new System.Drawing.Point(windowRect.left + HotspotOffsetPx.Width, windowRect.top + HotspotOffsetPx.Height);
    }

    public string ReadCurrentMonitorDeviceName() =>
        DisplayMonitorEnumerator.ReadDeviceName(PInvoke.MonitorFromWindow(windowHandle, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST));

    private void ApplyHotspotTarget()
    {
        if (hotspotTargetPx is not System.Drawing.Point hotspotTarget)
        {
            return;
        }
        for (int moveAttempt = 0; moveAttempt < 2 && ReadHotspotPositionPx() != hotspotTarget; moveAttempt++)
        {
            PInvoke.SetWindowPos(
                windowHandle,
                default,
                hotspotTarget.X - HotspotOffsetPx.Width,
                hotspotTarget.Y - HotspotOffsetPx.Height,
                0,
                0,
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
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

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            IsExcludedFromCapture = PInvoke.SetWindowDisplayAffinity(windowHandle, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);
            if (!IsExcludedFromCapture)
            {
                ClickyLog.Write("overlay: WDA_EXCLUDEFROMCAPTURE failed; hiding the overlay during capture instead");
            }
        }

        HwndSource.FromHwnd(windowHandle).AddHook(OverlayWindowProcedure);
    }

    private IntPtr OverlayWindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WindowMessageDpiChanged)
        {
            // Left unhandled so WPF rescales the content for the new DPI; afterwards, put the hotspot back
            // where it belongs in case WPF moved the window to Windows' suggested rectangle.
            Dispatcher.InvokeAsync(ApplyHotspotTarget, DispatcherPriority.Render);
        }
        return IntPtr.Zero;
    }
}
