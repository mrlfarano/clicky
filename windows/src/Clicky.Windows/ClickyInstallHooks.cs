using Microsoft.Win32;
using Velopack;

namespace Clicky.Windows;

/// <summary>
/// Start-at-login for the per-user Velopack install (WS-20, audit row A3: the unpackaged
/// equivalent of SMAppService.mainApp.register() is an HKCU Run value; MSIX StartupTask is not
/// used because ADR 001 ships Velopack, not MSIX).
///
/// The Run value is written by Velopack's after-install hook rather than on first live launch:
/// Setup.exe runs current\Clicky.exe --veloapp-install before it returns, so the value exists as
/// soon as a silent install completes, even if the app has never been launched. Velopack runs
/// the before-uninstall hook (--veloapp-uninstall) before deleting the install, which is the
/// only point where the app can remove what it wrote; Velopack itself removes the Start-menu and
/// Desktop shortcuts it created (vpk pack's default --shortcuts Desktop,StartMenuRoot), but it
/// knows nothing about this value. No after-update hook is registered: the value points at
/// current\Clicky.exe, which Velopack keeps at the same path across updates, so an update never
/// needs to touch it (the install-hooks CI step re-reads it after an update to prove that).
/// </summary>
internal static class ClickyInstallHooks
{
    public const string RunRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "Clicky";

    public static void WriteStartAtLoginRunValue(SemanticVersion installedVersion)
    {
        using RegistryKey runKey = Registry.CurrentUser.CreateSubKey(RunRegistryKeyPath);
        // Quoted because the per-user profile path can contain spaces.
        runKey.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
    }

    public static void DeleteStartAtLoginRunValue(SemanticVersion uninstalledVersion)
    {
        using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunRegistryKeyPath, writable: true);
        runKey?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }
}
