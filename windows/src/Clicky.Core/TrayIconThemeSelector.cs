namespace Clicky.Core;

/// <summary>
/// Picks the tray icon for the taskbar's theme (audit B2). The Mac draws a template image that macOS tints itself
/// (MenuBarPanelManager.swift:68-69, 74-108). Windows does no tinting, so the shell ships a light-theme and a
/// dark-theme .ico. It swaps them when WM_SETTINGCHANGE("ImmersiveColorSet") arrives, after re-reading
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\SystemUsesLightTheme. That registry read is
/// passed in, so TrayThemeTests can fake it.
/// </summary>
public sealed class TrayIconThemeSelector(Func<int?> readSystemUsesLightThemeValue)
{
    /// <summary>The WM_SETTINGCHANGE lParam string Windows broadcasts when the light/dark theme changes.</summary>
    public const string ImmersiveColorSetSettingName = "ImmersiveColorSet";

    /// <summary>A dark glyph, for the light taskbar.</summary>
    public const string LightThemeIconFileName = "clicky-tray-light-theme.ico";

    /// <summary>A white glyph, for the dark taskbar.</summary>
    public const string DarkThemeIconFileName = "clicky-tray-dark-theme.ico";

    public string CurrentIconFileName { get; private set; } = SelectIconFileName(readSystemUsesLightThemeValue());

    /// <summary>
    /// Handles one WM_SETTINGCHANGE. Returns true when the icon has to be swapped to <see cref="CurrentIconFileName"/>.
    /// Other settings, and theme broadcasts that leave the taskbar theme as it was, return false.
    /// </summary>
    public bool HandleSettingChange(string? changedSettingName)
    {
        if (!string.Equals(changedSettingName, ImmersiveColorSetSettingName, StringComparison.Ordinal))
        {
            return false;
        }
        string selectedIconFileName = SelectIconFileName(readSystemUsesLightThemeValue());
        if (selectedIconFileName == CurrentIconFileName)
        {
            return false;
        }
        CurrentIconFileName = selectedIconFileName;
        return true;
    }

    // A missing value (no light taskbar before 1903) means the dark taskbar, which is also Windows' default.
    private static string SelectIconFileName(int? systemUsesLightThemeValue) =>
        systemUsesLightThemeValue == 1 ? LightThemeIconFileName : DarkThemeIconFileName;
}
