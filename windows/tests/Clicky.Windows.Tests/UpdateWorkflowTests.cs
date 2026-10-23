using System.Text.RegularExpressions;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-036 (WS-10 part 2): pins the update-n-to-n1 CI step, the win-v* tag release flow and the
/// --debug-update-now flag they rely on. Like PackagingWorkflowTests these read the ci.yml GitHub Actions
/// executes, so they prove the workflow's content on any machine; the install, the self-update and the
/// GitHub upload themselves only run on the runner. The two flag tests run the real Clicky.exe.
/// </summary>
[Collection(ClickyProcessReplayCollection.Name)]
public sealed class UpdateWorkflowTests
{
    [Fact]
    public void UpdateNToN1InstallsNAndUpdatesItselfFromALocalFeed()
    {
        // T-036 acceptance 1.
        string updateStep = CiWorkflowSource.WindowsBuildStep("update-n-to-n1");

        // N is installed fresh from the Package step's Setup.exe.
        Assert.Contains("../artifacts/package/*-Setup.exe", updateStep);
        Assert.Contains("\"--silent\"", updateStep);
        // N+1 is a second pack into a local directory feed that also holds N's release set.
        Assert.Contains("Copy-Item -Path \"../artifacts/package/*\" -Destination $localFeedDirectory", updateStep);
        Assert.Matches(@"vpk pack [^\n]*--packVersion \$versionN1", updateStep);
        Assert.Contains("--outputDir $localFeedDirectory", updateStep);
        Assert.Contains("--channel win-beta", updateStep);
        // The INSTALLED copy's appsettings.json is pointed at that feed, and the installed exe updates itself.
        Assert.Contains("$installedAppSettings.UpdateFeedUrl = $localFeedDirectory", updateStep);
        Assert.Contains("Join-Path $installedCurrentDirectory \"appsettings.json\"", updateStep);
        Assert.Contains("-FilePath $installedClickyExecutablePath -ArgumentList \"--debug-update-now\"", updateStep);
        Assert.Contains("$debugUpdateProcess.ExitCode -ne 0", updateStep);
        // The installed version is N+1: current\sq.version reports it.
        Assert.Contains("\"sq.version\"", updateStep);
        Assert.Contains("-match [Regex]::Escape($versionN1)", updateStep);

        // Runs on what was packed, signed and scanned, after installed-replay (whose install it replaces).
        int updateStepIndex = CiWorkflowSource.WindowsBuildStepIndex("update-n-to-n1");
        Assert.True(CiWorkflowSource.WindowsBuildStepIndex("Defender scan of packaged output") < updateStepIndex);
        Assert.True(CiWorkflowSource.WindowsBuildStepIndex("installed-replay") < updateStepIndex);
        Assert.True(updateStepIndex < CiWorkflowSource.WindowsBuildStepIndex("Upload artifacts"));
    }

    [Fact]
    public void UpdateNToN1KeepsSettingsJsonByteIdentical()
    {
        // T-036 acceptance 2: settings.json is seeded beside current\, hashed before --debug-update-now and
        // compared after the version check, so a changed or deleted file fails the step.
        string updateStep = CiWorkflowSource.WindowsBuildStep("update-n-to-n1");

        Assert.Contains("$userSettingsFilePath = Join-Path $velopackRootDirectory \"settings.json\"", updateStep);
        int seedIndex = updateStep.IndexOf("Set-Content -Path $userSettingsFilePath", StringComparison.Ordinal);
        int hashBeforeIndex = updateStep.IndexOf("$settingsHashBeforeUpdate = (Get-FileHash -LiteralPath $userSettingsFilePath", StringComparison.Ordinal);
        int debugUpdateIndex = updateStep.IndexOf("\"--debug-update-now\"", StringComparison.Ordinal);
        int versionCheckIndex = updateStep.IndexOf("-match [Regex]::Escape($versionN1)", StringComparison.Ordinal);
        int hashAfterIndex = updateStep.IndexOf("$settingsHashAfterUpdate = (Get-FileHash -LiteralPath $userSettingsFilePath", StringComparison.Ordinal);
        int compareIndex = updateStep.IndexOf("$settingsHashAfterUpdate -ne $settingsHashBeforeUpdate", StringComparison.Ordinal);
        Assert.True(seedIndex >= 0 && hashBeforeIndex > seedIndex && debugUpdateIndex > hashBeforeIndex
            && versionCheckIndex > debugUpdateIndex && hashAfterIndex > versionCheckIndex && compareIndex > hashAfterIndex,
            "update-n-to-n1 must seed settings.json, hash it, run --debug-update-now, wait for N+1, then re-hash and compare.");
    }

    [Fact]
    public void TagRunsPackSignUpload()
    {
        // T-036 acceptance 3: a win-v* tag starts a run...
        string triggerBlock = CiWorkflowSource.TriggerBlock();
        Assert.Contains("tags: ['win-v*']", triggerBlock);
        Assert.Contains("branches: ['**']", triggerBlock);

        // ...that packs, signs (vpk signs while packing in the mode the Sign step picked, and
        // verify-signature checks the result) and only then uploads.
        string packageStep = CiWorkflowSource.WindowsBuildStep("Package");
        Assert.Contains("vpk pack", packageStep);
        Assert.Contains("@vpkSigningArguments", packageStep);
        Assert.True(CiWorkflowSource.WindowsBuildStepIndex("Sign") < CiWorkflowSource.WindowsBuildStepIndex("Package"));
        Assert.True(CiWorkflowSource.WindowsBuildStepIndex("Package") < CiWorkflowSource.WindowsBuildStepIndex("verify-signature"));

        string releaseJobHeader = CiWorkflowSource.JobHeader("windows-release");
        Assert.Contains("if: startsWith(github.ref, 'refs/tags/win-v')", releaseJobHeader);
        // The upload runs only after windows-build (pack, sign, tests) succeeded, and only it may write.
        Assert.Contains("needs: [windows-build, release-gate]", releaseJobHeader);
        Assert.Contains("contents: write", releaseJobHeader);
        Assert.Contains("signing-mode: ${{ steps.sign.outputs.mode }}", CiWorkflowSource.JobHeader("windows-build"));

        string uploadStep = CiWorkflowSource.JobStep("windows-release", "vpk upload github");
        Assert.Contains("SIGNING_MODE: ${{ needs.windows-build.outputs.signing-mode }}", uploadStep);
        int unsignedRefusalIndex = uploadStep.IndexOf("$env:SIGNING_MODE -notin @('trusted-signing', 'ov-certificate')", StringComparison.Ordinal);
        int uploadIndex = uploadStep.IndexOf("vpk upload github --outputDir artifacts/package --channel win-beta", StringComparison.Ordinal);
        Assert.True(unsignedRefusalIndex >= 0 && uploadIndex > unsignedRefusalIndex,
            "windows-release must refuse an unsigned package before it runs vpk upload github.");
        // It ships the package windows-build tested; it never repacks.
        Assert.DoesNotContain("vpk pack", uploadStep);
        Assert.Contains("name: windows-build-${{ github.run_attempt }}",
            CiWorkflowSource.JobStep("windows-release", "Download windows-build package"));
        // Same vpk pin as the Package step.
        Assert.Equal(
            Regex.Match(packageStep, @"dotnet tool install -g vpk --version \S+").Value,
            Regex.Match(uploadStep, @"dotnet tool install -g vpk --version \S+").Value);

        // The Mac Sparkle feed is untouched: scripts/release.sh builds and pushes its appcast.xml from a Mac,
        // and no step of this workflow runs that script, Sparkle's tools or writes an appcast.
        string workflowText = File.ReadAllText(Path.Combine(ClickyReplayProcess.RepositoryRootDirectory, ".github", "workflows", "ci.yml"));
        Assert.DoesNotMatch(@"(?i)release\.sh|generate_appcast|sign_update|appcast", workflowText);
    }

    [Fact]
    public async Task DebugUpdateNowWithEmptyFeedMakesNoRequestAndFails()
    {
        // The build output ships UpdateFeedUrl empty, so the debug trigger has nothing to check and must say so.
        ClickyStartupFailureRun clickyProcessRun = await ClickyReplayProcess.RunArgumentsAsync("--debug-update-now");

        Assert.Equal(1, clickyProcessRun.ExitCode);
        Assert.Contains("UpdateFeedUrl is empty; --debug-update-now has no feed to check", clickyProcessRun.ProcessOutput);
    }

    [Fact]
    public async Task DebugUpdateNowCannotBeCombinedWithReplay()
    {
        string pointBasicFixturePath = Path.Combine(
            ClickyReplayProcess.RepositoryRootDirectory, "shared", "fixtures", "sse", "point-basic.sse");
        ClickyStartupFailureRun clickyProcessRun = await ClickyReplayProcess.RunArgumentsAsync(
            "--debug-update-now", "--replay", pointBasicFixturePath);

        Assert.Equal(2, clickyProcessRun.ExitCode);
        Assert.Contains("startup error", clickyProcessRun.ProcessOutput);
        Assert.Contains("--debug-update-now runs one update check and exits", clickyProcessRun.ProcessOutput);
    }
}
