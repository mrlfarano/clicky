using System.Text.RegularExpressions;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-013 / T-014 (WS-08) and T-015 (WS-09): pins the Velopack packaging, signing and
/// installed-copy contract. These
/// tests read the sources that GitHub Actions executes (ci.yml, Program.cs, the csproj), so
/// they prove the workflow's content on any machine. The pack, the silent install and the
/// second package apply only run on the windows-build runner; that half of the contract is
/// verifiable on CI alone.
/// </summary>
public sealed class PackagingWorkflowTests
{
    [Fact]
    public void VelopackIsFirstLineOfMain()
    {
        string[] programSourceLines = File.ReadAllLines(
            RepositoryFile("windows", "src", "Clicky.Windows", "Program.cs"));

        int mainSignatureLineIndex = Array.FindIndex(programSourceLines, line => line.Contains("static int Main("));
        Assert.True(mainSignatureLineIndex >= 0, "Program.Main was not found in Program.cs.");

        int mainOpeningBraceLineIndex = Array.FindIndex(
            programSourceLines, mainSignatureLineIndex, line => line.Trim() == "{");
        Assert.True(mainOpeningBraceLineIndex > mainSignatureLineIndex, "The opening brace of Main was not found.");

        // Blank lines and comments are not statements. Velopack requires its hook to run
        // before any other code in the process, so the first statement of Main must be it.
        // The statement spans several lines because the install hooks (T-034) are chained
        // onto the builder before Run().
        string[] mainBodyLinesFromFirstStatement = programSourceLines
            .Skip(mainOpeningBraceLineIndex + 1)
            .Select(sourceLine => sourceLine.Trim())
            .SkipWhile(trimmedLine => trimmedLine.Length == 0 || trimmedLine.StartsWith("//"))
            .ToArray();
        int firstStatementLastLineIndex = Array.FindIndex(mainBodyLinesFromFirstStatement, line => line.EndsWith(';'));
        string firstMainStatement = string.Concat(mainBodyLinesFromFirstStatement[..(firstStatementLastLineIndex + 1)]);
        Assert.StartsWith("VelopackApp.Build()", firstMainStatement);
        Assert.EndsWith(".Run();", firstMainStatement);
    }

    [Fact]
    public void PackUsesWinBetaChannelAndPinnedVpk()
    {
        string packageStep = CiWorkflowSource.WindowsBuildStep("Package");

        Assert.Contains("vpk pack", packageStep);
        Assert.Contains("--channel win-beta", packageStep);
        Assert.Contains("--mainExe Clicky.exe", packageStep);
        Assert.Contains("--packId Clicky", packageStep);
        Assert.Contains("--packTitle Clicky", packageStep);

        // The packer and the app's VelopackApp hooks must be the same Velopack version, or
        // the package format and the install hooks can disagree.
        Match vpkToolVersionMatch = Regex.Match(packageStep, @"dotnet tool install -g vpk --version (?<version>\S+)");
        Assert.True(vpkToolVersionMatch.Success,
            "The Package step must install the vpk global tool with a pinned --version.");
        Assert.Equal(VelopackNuGetVersion(), vpkToolVersionMatch.Groups["version"].Value);
    }

    [Fact]
    public void AssertArtifactsStepChecksSetupExeFullNupkgAndReleasesManifest()
    {
        string assertArtifactsStep = CiWorkflowSource.WindowsBuildStep("assert-artifacts");

        Assert.Contains("Setup.exe", assertArtifactsStep);
        Assert.Contains("-full.nupkg", assertArtifactsStep);
        Assert.Contains("releases.win-beta.json", assertArtifactsStep);
    }

    [Fact]
    public void DefenderScanCoversTheVelopackPackageOutput()
    {
        // vpk writes Setup.exe and the full .nupkg into its output directory, so the scan
        // must include that directory (it also keeps scanning the raw publish directory
        // the packages were built from).
        string packageStep = CiWorkflowSource.WindowsBuildStep("Package");
        Assert.Contains("--outputDir ../artifacts/package", packageStep);

        string defenderScanStep = CiWorkflowSource.WindowsBuildStep("Defender scan of packaged output");
        Assert.Contains("../artifacts/package", defenderScanStep);
    }

    [Fact]
    public void InstalledReplaySilentlyInstallsRunsInstalledExeReplayAndAssertsExitZero()
    {
        // T-014 acceptance 1: the installed-replay step silently installs the Setup.exe
        // the Package step built, then runs the INSTALLED Clicky.exe (under
        // %LOCALAPPDATA%\Clicky\current, not the build output) against the point-basic
        // fixture and requires exit 0.
        string installedReplayStep = CiWorkflowSource.WindowsBuildStep("installed-replay");

        // The silent flag observed from the generated Setup.exe --help ('-s, --silent').
        Assert.Contains("--silent", installedReplayStep);
        // The exe under test is the installed copy beside the checkout's build output.
        Assert.Contains("Join-Path $env:LOCALAPPDATA \"Clicky\"", installedReplayStep);
        Assert.Contains("\"current\"", installedReplayStep);
        // The replay invocation and its fixture, plus the exit-0 assertion on that run.
        Assert.Contains("\"--replay\"", installedReplayStep);
        Assert.Contains("point-basic.sse", installedReplayStep);
        Assert.Contains("$installedReplayProcess.ExitCode -ne 0", installedReplayStep);

        // Order: after Package (it consumes that step's Setup.exe and publish output)
        // and after the Defender scan (nothing executes before it has been scanned).
        int installedReplayStepIndex = CiWorkflowSource.WindowsBuildStepIndex("installed-replay");
        Assert.True(CiWorkflowSource.WindowsBuildStepIndex("Package") < installedReplayStepIndex,
            "installed-replay must come after the Package step that builds the Setup.exe it installs.");
        Assert.True(CiWorkflowSource.WindowsBuildStepIndex("Defender scan of packaged output") < installedReplayStepIndex,
            "installed-replay must come after the Defender scan so nothing executes before it has been scanned.");
    }

    [Fact]
    public void UserDataOutsideCurrentSurvives()
    {
        // T-014 acceptance 2: the installed-replay step seeds settings.json, logs\ and
        // crashes\ beside current\ (the WS-08 user-data locations), applies a SECOND
        // package, and fails unless every seeded file is byte-identical afterwards.
        string installedReplayStep = CiWorkflowSource.WindowsBuildStep("installed-replay");

        // The three user-data locations WS-08 keeps beside current\.
        Assert.Contains("settings.json", installedReplayStep);
        Assert.Contains("\"logs\"", installedReplayStep);
        Assert.Contains("\"crashes\"", installedReplayStep);
        Assert.Contains("\"current\"", installedReplayStep);

        // A second package is applied, and through the staged update path (Update.exe
        // apply), NOT by re-running Setup.exe: with the pinned vpk 1.2.161, Setup.exe's
        // full install replaces the whole %LOCALAPPDATA%\Clicky root and deletes the
        // user data beside current\, while Update.exe apply replaces only current\.
        // --norestart keeps the updater from launching the app (live mode waits for
        // Ctrl+C and would hang the job).
        Assert.Contains("vpk pack", installedReplayStep);
        Assert.Contains("Update.exe", installedReplayStep);
        Assert.Contains("\"apply\"", installedReplayStep);
        Assert.Contains("--norestart", installedReplayStep);
        // current\sq.version reporting the second version proves the apply really
        // replaced current\ rather than exiting 0 as a silent no-op.
        Assert.Contains("sq.version", installedReplayStep);

        // The untouched assertion itself: the step hashes every user-data file before
        // the apply and compares the state after it, so an added, removed or modified
        // file fails the build.
        Assert.Contains("Get-FileHash", installedReplayStep);
        Assert.Contains("Compare-Object", installedReplayStep);
    }

    [Fact]
    public void SignStepSkipsWithoutSecrets()
    {
        // T-015 acceptance 2: fork PRs receive no secrets, so the Sign step must take a
        // green skip path that logs exactly 'signing: SKIPPED', and nothing downstream may
        // try to sign or verify.
        string signStep = CiWorkflowSource.WindowsBuildStep("Sign");

        Assert.Contains("Write-Host \"signing: SKIPPED\"", signStep);
        Assert.Contains("\"mode=skipped\" >> $env:GITHUB_OUTPUT", signStep);
        string skipBranch = signStep[signStep.IndexOf("\"mode=skipped\"", StringComparison.Ordinal)..];
        Assert.Contains("exit 0", skipBranch);
        Assert.DoesNotContain("exit 1", skipBranch);

        // The mode comes only from secrets, so a fork (no secrets) can only reach the skip branch.
        foreach (string signingSecretName in new[]
                 {
                     "TRUSTED_SIGNING_ENDPOINT", "TRUSTED_SIGNING_ACCOUNT_NAME", "TRUSTED_SIGNING_CERTIFICATE_PROFILE",
                     "AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET",
                     "SIGNING_CERT_PFX_BASE64", "SIGNING_PASSWORD",
                 })
        {
            Assert.Contains($"{signingSecretName}: ${{{{ secrets.{signingSecretName} }}}}", signStep);
        }

        // Skipped mode adds no vpk signing arguments, and verify-signature does not run.
        string packageStep = CiWorkflowSource.WindowsBuildStep("Package");
        Assert.Contains("SIGNING_MODE: ${{ steps.sign.outputs.mode }}", packageStep);
        Assert.Contains("@vpkSigningArguments", packageStep);
        Assert.Contains("if: steps.sign.outputs.mode != 'skipped'", CiWorkflowSource.WindowsBuildStep("verify-signature"));
    }

    [Fact]
    public void BothSigningPathsTimestampWithSha256()
    {
        string packageStep = CiWorkflowSource.WindowsBuildStep("Package");

        // Trusted Signing: vpk's embedded signtool adds /fd SHA256 /tr <acs timestamp> /td SHA256.
        Assert.Contains("'trusted-signing' { '--azureTrustedSignFile'", packageStep);
        // OV fallback: explicit signtool parameters with an RFC 3161 timestamp.
        Assert.Contains("/fd SHA256 /tr http://timestamp.digicert.com /td SHA256", packageStep);
    }

    [Fact]
    public void VerifySignatureChecksAllThreeBinariesBeforeDefenderScanAndInstall()
    {
        // T-015 acceptance 1.
        string verifySignatureStep = CiWorkflowSource.WindowsBuildStep("verify-signature");

        Assert.Contains("Get-AuthenticodeSignature", verifySignatureStep);
        Assert.Contains("$signature.Status -ne 'Valid'", verifySignatureStep);
        Assert.Contains("$null -eq $signature.TimeStamperCertificate", verifySignatureStep);
        Assert.Contains("$signerSubject -ne $env:SIGNING_EXPECTED_SUBJECT", verifySignatureStep);
        Assert.Contains("'Clicky.exe' =", verifySignatureStep);
        Assert.Contains("'Update.exe' =", verifySignatureStep);
        Assert.Contains("'Setup.exe' =", verifySignatureStep);

        // Signed binaries are what gets scanned and installed.
        int verifySignatureStepIndex = CiWorkflowSource.WindowsBuildStepIndex("verify-signature");
        Assert.True(CiWorkflowSource.WindowsBuildStepIndex("Sign") < CiWorkflowSource.WindowsBuildStepIndex("Package"));
        Assert.True(CiWorkflowSource.WindowsBuildStepIndex("Package") < verifySignatureStepIndex);
        Assert.True(verifySignatureStepIndex < CiWorkflowSource.WindowsBuildStepIndex("Defender scan of packaged output"));
        Assert.True(verifySignatureStepIndex < CiWorkflowSource.WindowsBuildStepIndex("installed-replay"));
    }

    [Fact]
    public void PfxPlaceholderStepIsGoneAndNoEvPathExists()
    {
        // T-015 acceptance 3.
        string workflowText = File.ReadAllText(RepositoryFile(".github", "workflows", "ci.yml"));

        Assert.DoesNotContain("Sign (phase-gated", workflowText);
        Assert.DoesNotContain("SIGNING_CERT_PASSWORD", workflowText);
        Assert.DoesNotContain("Windows Kits", workflowText);
        Assert.DoesNotMatch(@"\bEV\b|(?i:extended validation)", workflowText);
    }

    private static string VelopackNuGetVersion()
    {
        string projectText = File.ReadAllText(
            RepositoryFile("windows", "src", "Clicky.Windows", "Clicky.Windows.csproj"));
        Match velopackPackageMatch = Regex.Match(
            projectText, """<PackageReference Include="Velopack" Version="(?<version>[^"]+)" />""");
        Assert.True(velopackPackageMatch.Success, "Clicky.Windows.csproj must reference the Velopack NuGet package.");
        return velopackPackageMatch.Groups["version"].Value;
    }

    private static string RepositoryFile(params string[] pathSegmentsBelowRepositoryRoot) =>
        Path.Combine([ClickyReplayProcess.RepositoryRootDirectory, .. pathSegmentsBelowRepositoryRoot]);
}
