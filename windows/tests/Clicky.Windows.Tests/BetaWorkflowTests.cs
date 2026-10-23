using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-037 (WS-21): pins the signed-beta release gate and the beta release-notes template. Like
/// UpdateWorkflowTests these read the sources GitHub Actions executes; the gate's signature check and the
/// upload themselves only run on a win-v* tag build on the runner.
/// </summary>
public sealed class BetaWorkflowTests
{
    [Fact]
    public void ReleaseGateFailsOnUnsignedOutputBeforeUpload()
    {
        // T-037 acceptance 1: release-gate runs on win-v* tags against windows-build's uploaded package...
        string releaseGateHeader = CiWorkflowSource.JobHeader("release-gate");
        Assert.Contains("if: startsWith(github.ref, 'refs/tags/win-v')", releaseGateHeader);
        Assert.Contains("needs: windows-build", releaseGateHeader);
        Assert.Contains("name: windows-build-${{ github.run_attempt }}",
            CiWorkflowSource.JobStep("release-gate", "Download windows-build package"));

        // ...reads the signature of every shipped binary, and fails on anything but Valid (an unsigned fork
        // build is NotSigned) with a message that points at the signing secrets contract.
        string gateStep = CiWorkflowSource.JobStep("release-gate", "Refuse unsigned output");
        Assert.Contains("-Include Clicky.exe", gateStep);
        Assert.Contains("-Include Update.exe, Squirrel.exe", gateStep);
        Assert.Contains("\"*-Setup.exe\"", gateStep);
        Assert.Contains("(Get-AuthenticodeSignature -LiteralPath $binaryFiles[0].FullName).Status", gateStep);
        Assert.Contains("$signatureStatus -ne 'Valid'", gateStep);
        Assert.Contains("refusing to upload unsigned output", gateStep);
        Assert.Contains("SECRETS CONTRACT", gateStep);
        Assert.Contains("SECRETS CONTRACT", CiWorkflowSource.WindowsBuildStep("Sign"));
        Assert.Contains("if ($failed) { exit 1 }", gateStep);
        // Nothing in the gate signs, packs or uploads.
        Assert.DoesNotContain("vpk", gateStep);

        // The upload job cannot start until the gate succeeded.
        Assert.Contains("needs: [windows-build, release-gate]", CiWorkflowSource.JobHeader("windows-release"));
    }

    [Fact]
    public void SignedBetaPassesTheGateToTheWinBetaChannel()
    {
        // T-037 acceptance 2: only win-v<semver>-beta.N tags pass, the package must hold the win-beta release
        // set and nothing else, and the upload targets exactly that channel.
        string gateStep = CiWorkflowSource.JobStep("release-gate", "Refuse unsigned output");
        Assert.Contains("$env:GITHUB_REF_NAME -notmatch '^win-v\\d+\\.\\d+\\.\\d+-beta\\.\\d+$'", gateStep);
        Assert.Contains("($releasesManifestNames -join ',') -ne 'releases.win-beta.json'", gateStep);
        Assert.Contains("release-gate: signed; target channel win-beta", gateStep);

        string uploadStep = CiWorkflowSource.JobStep("windows-release", "vpk upload github");
        Assert.Contains("vpk upload github --outputDir artifacts/package --channel win-beta ", uploadStep);
        Assert.DoesNotContain("--channel win ", uploadStep);
    }

    [Fact]
    public void NotesMentionSmartScreen()
    {
        // T-037 acceptance 3: testers are told how to get past SmartScreen, and QA records the clean-VM result.
        string releaseNotesTemplate = File.ReadAllText(
            Path.Combine(ClickyReplayProcess.RepositoryRootDirectory, ".github", "RELEASE_NOTES_beta.md"));

        Assert.Contains("\"Windows protected your PC\"", releaseNotesTemplate);
        int moreInfoIndex = releaseNotesTemplate.IndexOf("**More info**", StringComparison.Ordinal);
        int runAnywayIndex = releaseNotesTemplate.IndexOf("**Run anyway**", StringComparison.Ordinal);
        Assert.True(moreInfoIndex >= 0 && runAnywayIndex > moreInfoIndex,
            "The release notes must say More info, then Run anyway.");
        Assert.Contains("- SmartScreen-on-clean-VM: <", releaseNotesTemplate);
    }
}
