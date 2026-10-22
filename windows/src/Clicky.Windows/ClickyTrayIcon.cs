using System.Drawing;
using System.Runtime.InteropServices;
using H.NotifyIcon;
using Microsoft.Win32;
using Windows.Win32.Foundation;
using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// The tray icon (audit B1, B2, B4): H.NotifyIcon.Wpf's Shell_NotifyIconW wrapper, with the light or dark .ico
/// that Core's <see cref="TrayIconThemeSelector"/> picks. It can also read where the icon and the taskbar are,
/// which the panel anchor needs. Win11 puts a new icon in the overflow (^) flyout until the user pins it.
/// </summary>
internal sealed class ClickyTrayIcon : IDisposable
{
    private const string PersonalizeRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly TaskbarIcon taskbarIcon;
    private readonly TrayIconThemeSelector trayIconThemeSelector = new(ReadSystemUsesLightThemeValue);

    public ClickyTrayIcon()
    {
        taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "Clicky",
            // The click toggles the panel at once instead of waiting out the double-click time.
            NoLeftClickDelay = true,
            Icon = LoadTrayIcon(trayIconThemeSelector.CurrentIconFileName),
        };
        taskbarIcon.TrayLeftMouseUp += (_, _) => Clicked?.Invoke();
        // A TaskbarIcon built in code (not XAML) only appears once forced. Efficiency mode stays off: it would
        // drop the whole process to EcoQoS, which push-to-talk and audio must not run under.
        taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
        ClickyLog.Write($"tray: icon added ({trayIconThemeSelector.CurrentIconFileName})");
    }

    /// <summary>A left click on the icon, raised on the dispatcher.</summary>
    public event Action? Clicked;

    /// <summary>Feeds one WM_SETTINGCHANGE to the theme selector and swaps the icon when the taskbar theme changed.</summary>
    public void HandleSettingChange(string? changedSettingName)
    {
        if (!trayIconThemeSelector.HandleSettingChange(changedSettingName))
        {
            return;
        }
        Icon? previousIcon = taskbarIcon.Icon;
        taskbarIcon.Icon = LoadTrayIcon(trayIconThemeSelector.CurrentIconFileName);
        previousIcon?.Dispose();
        ClickyLog.Write($"tray: theme changed, icon {trayIconThemeSelector.CurrentIconFileName}");
    }

    /// <summary>The icon's rect from Shell_NotifyIconGetRect, in physical pixels; null when the shell cannot say (for example while it restarts).</summary>
    public PhysicalPixelRect? ReadIconRectPx()
    {
        var notifyIconIdentifier = new NotifyIconIdentifier
        {
            StructureSizeBytes = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            IconGuid = taskbarIcon.TrayIcon.Id,
        };
        if (Shell_NotifyIconGetRect(ref notifyIconIdentifier, out RECT iconRect) != 0)
        {
            return null;
        }
        return new PhysicalPixelRect(iconRect.left, iconRect.top, iconRect.right - iconRect.left, iconRect.bottom - iconRect.top);
    }

    /// <summary>The primary taskbar's edge from ABM_GETTASKBARPOS; Bottom, the default, when the call fails.</summary>
    public static TaskbarEdge ReadTaskbarEdge()
    {
        var appBarData = new AppBarData { StructureSizeBytes = (uint)Marshal.SizeOf<AppBarData>() };
        return SHAppBarMessage(AppBarMessageGetTaskbarPosition, ref appBarData) == 0
            ? TaskbarEdge.Bottom
            : (TaskbarEdge)appBarData.Edge;
    }

    public void Dispose()
    {
        // NIM_DELETE happens here, so a quit leaves no ghost icon behind (PNL-05).
        taskbarIcon.Dispose();
        ClickyLog.Write("tray: icon removed");
    }

    // The icons are copied next to Clicky.exe (Assets\ in the csproj).
    private static Icon LoadTrayIcon(string iconFileName) => new(Path.Combine(AppContext.BaseDirectory, "Assets", iconFileName));

    private static int? ReadSystemUsesLightThemeValue()
    {
        using RegistryKey? personalizeKey = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryKeyPath);
        return personalizeKey?.GetValue("SystemUsesLightTheme") as int?;
    }

    // CsWin32 refuses these two shell32 structs on AnyCPU (their packing differs on x86), so they are declared by hand.
    // shortcut: natural (64-bit) layout only; Clicky ships win-x64 (ci.yml). Revisit if an x86 build is ever added.
    private const uint AppBarMessageGetTaskbarPosition = 0x00000005; // ABM_GETTASKBARPOS

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint StructureSizeBytes;
        public nint OwnerWindowHandle;
        public uint IconId;
        public Guid IconGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint StructureSizeBytes;
        public nint WindowHandle;
        public uint CallbackMessage;
        public uint Edge;
        public RECT Rect;
        public nint MessageParameter;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier notifyIconIdentifier, out RECT iconLocation);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern nuint SHAppBarMessage(uint message, ref AppBarData appBarData);
}
