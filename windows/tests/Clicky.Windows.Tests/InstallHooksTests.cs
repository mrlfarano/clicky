using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-034 (WS-20): pins the per-user install hooks contract. Like PackagingWorkflowTests, these
/// read the sources GitHub Actions executes (ci.yml, Program.cs, ClickyInstallHooks.cs): the
/// silent install, the update and the silent uninstall only run in the windows-build job's
/// install-hooks step, so the process-level half of the contract is verifiable on CI alone.
/// </summary>
public sealed class InstallHooksTests
{
    [Fact]
    public void SilentInstallWritesRunValueAndStartMenuShortcut()
    {
        // The app writes Run\Clicky from Velopack's after-install hook and removes it from the
        // before-uninstall hook; both are registered on the VelopackApp builder in Main.
        string programSource = File.ReadAllText(RepositoryFile("windows", "src", "Clicky.Windows", "Program.cs"));
        Assert.Contains(".OnAfterInstallFastCallback(ClickyInstallHooks.WriteStartAtLoginRunValue)", programSource);
        Assert.Contains(".OnBeforeUninstallFastCallback(ClickyInstallHooks.DeleteStartAtLoginRunValue)", programSource);
        string installHooksSource = File.ReadAllText(RepositoryFile("windows", "src", "Clicky.Windows", "ClickyInstallHooks.cs"));
        Assert.Contains("Registry.CurrentUser", installHooksSource);
        Assert.Contains(@"""Software\Microsoft\Windows\CurrentVersion\Run""", installHooksSource);

        // T-034 acceptance 1: silent install, then Run\Clicky must equal the quoted stable
        // current\Clicky.exe path and the Start-menu shortcut must exist.
        string installHooksStep = CiWorkflowSource.WindowsBuildStep("install-hooks");
        Assert.Contains("-ArgumentList \"--silent\"", installHooksStep);
        Assert.Contains(@"""HKCU:\Software\Microsoft\Windows\CurrentVersion\Run""", installHooksStep);
        Assert.Contains("$expectedRunValue = \"`\"$installedClickyExecutablePath`\"\"", installHooksStep);
        Assert.Contains("$runValueAfterInstall -ne $expectedRunValue", installHooksStep);
        Assert.Contains(@"""Microsoft\Windows\Start Menu\Programs\Clicky.lnk""", installHooksStep);
        Assert.Contains("-not (Test-Path $startMenuShortcutPath)", installHooksStep);

        // Leftovers from the earlier steps' installs are cleared first, so they cannot satisfy
        // the "created" assertions.
        Assert.Contains("Remove-ItemProperty -Path $runRegistryKeyPath -Name \"Clicky\"", installHooksStep);
        Assert.Contains("Remove-Item -LiteralPath $startMenuShortcutPath", installHooksStep);

        Assert.True(CiWorkflowSource.WindowsBuildStepIndex("installed-replay") < CiWorkflowSource.WindowsBuildStepIndex("install-hooks"),
            "install-hooks must come after installed-replay, whose second package it applies as the update.");
    }

    [Fact]
    public void SilentUninstallRemovesRunValueAndStartMenuShortcut()
    {
        // T-034 acceptance 2.
        string installHooksStep = CiWorkflowSource.WindowsBuildStep("install-hooks");
        Assert.Contains("-ArgumentList @(\"uninstall\", \"-s\")", installHooksStep);
        Assert.Contains("$uninstallProcess.ExitCode -ne 0", installHooksStep);
        Assert.Contains("$null -ne $runValueAfterUninstall", installHooksStep);
        Assert.Contains("if (Test-Path $startMenuShortcutPath)", installHooksStep);

        // The uninstall assertions come after the update, so the uninstall hook that runs is
        // the updated install's.
        int uninstallIndex = installHooksStep.IndexOf("\"uninstall\", \"-s\"", StringComparison.Ordinal);
        Assert.True(installHooksStep.IndexOf("$runValueAfterUpdate", StringComparison.Ordinal) < uninstallIndex);
    }

    [Fact]
    public void RunKeyStableAcrossUpdate()
    {
        // T-034 acceptance 3: a newer package is applied through Update.exe apply (the update
        // path users ride; re-running Setup.exe is a full reinstall), the apply must really take
        // effect (sq.version), and the Run value re-read afterwards must equal the one the
        // install wrote. ClickyInstallHooks registers no after-update hook, so only Velopack
        // keeping current\ at a stable path keeps this true.
        string installHooksStep = CiWorkflowSource.WindowsBuildStep("install-hooks");
        Assert.Contains("-ArgumentList @(\"apply\", \"-s\", \"--norestart\", \"-p\"", installHooksStep);
        Assert.Contains("sq.version", installHooksStep);
        Assert.Contains("$runValueAfterUpdate = Get-ClickyRunValue", installHooksStep);
        Assert.Contains("$runValueAfterUpdate -ne $runValueAfterInstall", installHooksStep);

        string programSource = File.ReadAllText(RepositoryFile("windows", "src", "Clicky.Windows", "Program.cs"));
        Assert.DoesNotContain("OnAfterUpdateFastCallback", programSource);
    }

    private static string RepositoryFile(params string[] pathSegmentsBelowRepositoryRoot) =>
        Path.Combine([ClickyReplayProcess.RepositoryRootDirectory, .. pathSegmentsBelowRepositoryRoot]);
}
