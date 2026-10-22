using Xunit;

namespace Clicky.Core.Tests;

/// <summary>
/// Pins the tray icon's theme selection (task T-031, audit B2). On WM_SETTINGCHANGE("ImmersiveColorSet") the
/// selector re-reads SystemUsesLightTheme and picks the light or dark .ico. The registry read is a fake here.
/// </summary>
public sealed class TrayThemeTests
{
    [Fact]
    public void ImmersiveColorSetChangeSelectsLightOrDarkIcon()
    {
        int? systemUsesLightThemeValue = 0;
        var trayIconThemeSelector = new TrayIconThemeSelector(() => systemUsesLightThemeValue);
        Assert.Equal(TrayIconThemeSelector.DarkThemeIconFileName, trayIconThemeSelector.CurrentIconFileName);

        systemUsesLightThemeValue = 1;
        Assert.True(trayIconThemeSelector.HandleSettingChange("ImmersiveColorSet"));
        Assert.Equal(TrayIconThemeSelector.LightThemeIconFileName, trayIconThemeSelector.CurrentIconFileName);

        systemUsesLightThemeValue = 0;
        Assert.True(trayIconThemeSelector.HandleSettingChange("ImmersiveColorSet"));
        Assert.Equal(TrayIconThemeSelector.DarkThemeIconFileName, trayIconThemeSelector.CurrentIconFileName);
    }

    [Fact]
    public void OtherSettingChangesAndUnchangedThemeKeepTheIcon()
    {
        int? systemUsesLightThemeValue = 1;
        var trayIconThemeSelector = new TrayIconThemeSelector(() => systemUsesLightThemeValue);

        // Only ImmersiveColorSet re-reads the theme: another setting's broadcast leaves the icon as it was.
        systemUsesLightThemeValue = 0;
        Assert.False(trayIconThemeSelector.HandleSettingChange("WindowMetrics"));
        Assert.False(trayIconThemeSelector.HandleSettingChange(null));
        Assert.Equal(TrayIconThemeSelector.LightThemeIconFileName, trayIconThemeSelector.CurrentIconFileName);

        // An ImmersiveColorSet broadcast that leaves the taskbar theme as it was (an accent colour change) needs no swap.
        systemUsesLightThemeValue = 1;
        Assert.False(trayIconThemeSelector.HandleSettingChange("ImmersiveColorSet"));
    }

    [Fact]
    public void MissingRegistryValueSelectsDarkIcon()
    {
        // No SystemUsesLightTheme value (pre-1903 builds had no light taskbar): the default dark taskbar.
        var trayIconThemeSelector = new TrayIconThemeSelector(() => null);

        Assert.Equal(TrayIconThemeSelector.DarkThemeIconFileName, trayIconThemeSelector.CurrentIconFileName);
    }
}
