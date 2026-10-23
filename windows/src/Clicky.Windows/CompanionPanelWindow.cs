using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// The companion panel's window (audit B3): borderless, topmost, out of the taskbar and Alt+Tab. It activates
/// when shown, because the panel's email field needs keyboard focus and Windows has no window that is key
/// without being active. Losing that activation is the Windows form of click-outside dismissal (B5).
/// Its content is <see cref="CompanionPanelView"/>, bound to <see cref="PanelViewModel"/>.
/// </summary>
internal sealed class CompanionPanelWindow : Window
{
    /// <summary>The Mac panel's width (MenuBarPanelManager.swift:35); its height follows the content.</summary>
    private const double PanelWidthDip = 320;

    private const int WindowMessageSettingChange = 0x001A; // WM_SETTINGCHANGE

    private readonly Border panelContentBorder;
    private HWND windowHandle;

    public CompanionPanelWindow(PanelViewModel panelViewModel)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = PanelWidthDip;

        var panelView = new CompanionPanelView { DataContext = panelViewModel };
        panelView.CloseButton.Click += (_, _) => CloseButtonClicked?.Invoke();
        panelContentBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 12, 12),
            Child = panelView,
        };
        Content = panelContentBorder;

        SourceInitialized += (_, _) => ApplyPanelWindowStyles();
    }

    /// <summary>The × button (CompanionPanelView.swift:106-107).</summary>
    public event Action? CloseButtonClicked;

    /// <summary>Every WM_SETTINGCHANGE with its setting name (lParam), which the tray uses for theme changes.</summary>
    public event Action<string?>? SettingChanged;

    /// <summary>True on 19041+ where WDA_EXCLUDEFROMCAPTURE keeps the panel out of screenshots (audit D2).</summary>
    public bool IsExcludedFromCapture { get; private set; }

    /// <summary>Creates the HWND without showing it, so capture exclusion and the WM_SETTINGCHANGE hook exist from startup.</summary>
    public void CreateHiddenWindowHandle() => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>
    /// Shows and activates the panel where Core's <see cref="PanelAnchor"/> puts it for the tray icon (PNL-01):
    /// beside the taskbar edge, on the icon's monitor, at that monitor's DPI.
    /// </summary>
    public void ShowAnchoredToTrayIcon(ClickyTrayIcon trayIcon)
    {
        PInvoke.GetCursorPos(out System.Drawing.Point cursorPositionPx);
        // When the shell cannot report the icon, anchor to the cursor instead: after a click, it is on the icon.
        PhysicalPixelRect iconRectPx = trayIcon.ReadIconRectPx() ?? new PhysicalPixelRect(cursorPositionPx.X, cursorPositionPx.Y, 1, 1);
        var iconCenterPx = new System.Drawing.Point(iconRectPx.Left + iconRectPx.Width / 2, iconRectPx.Top + iconRectPx.Height / 2);
        DisplayMonitor iconMonitor = DisplayMonitorEnumerator.DescribeMonitorForHandle(
            PInvoke.MonitorFromPoint(iconCenterPx, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST),
            isCursorMonitor: false);

        panelContentBorder.Measure(new Size(PanelWidthDip, double.PositiveInfinity));
        VirtualScreenRect panelRectPx = PanelAnchor.Compute(
            ClickyTrayIcon.ReadTaskbarEdge(),
            iconRectPx,
            new DipSize(PanelWidthDip, panelContentBorder.DesiredSize.Height),
            iconMonitor.WorkArea,
            iconMonitor.DpiScale);

        Height = panelRectPx.Height / iconMonitor.DpiScale;
        ApplyPositionPx(panelRectPx);
        Show();
        // WPF can move the window itself on its first Show; the second call puts it back on the anchor.
        ApplyPositionPx(panelRectPx);
        Activate();
    }

    private void ApplyPositionPx(VirtualScreenRect panelRectPx) =>
        PInvoke.SetWindowPos(
            windowHandle,
            default,
            (int)Math.Round(panelRectPx.Left),
            (int)Math.Round(panelRectPx.Top),
            (int)Math.Round(panelRectPx.Width),
            (int)Math.Round(panelRectPx.Height),
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    private void ApplyPanelWindowStyles()
    {
        windowHandle = (HWND)new WindowInteropHelper(this).Handle;
        // Tool window keeps the panel out of Alt+Tab; ShowInTaskbar=false alone does not for an unowned window.
        int currentExtendedStyles = PInvoke.GetWindowLong(windowHandle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(windowHandle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, currentExtendedStyles | (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
        IsExcludedFromCapture = WindowCaptureExclusion.TryExcludeFromCapture(windowHandle, "panel");
        HwndSource.FromHwnd(windowHandle).AddHook(PanelWindowProcedure);
    }

    private IntPtr PanelWindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WindowMessageSettingChange)
        {
            SettingChanged?.Invoke(lParam == IntPtr.Zero ? null : Marshal.PtrToStringUni(lParam));
        }
        return IntPtr.Zero;
    }
}
