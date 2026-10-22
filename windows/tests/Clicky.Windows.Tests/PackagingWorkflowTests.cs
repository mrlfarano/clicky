using System.Text.RegularExpressions;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-013 / WS-08: pins the Velopack packaging contract. These tests read the sources that
/// GitHub Actions executes (ci.yml, Program.cs, the csproj), so they prove the workflow's
/// content on any machine. The pack itself only runs on the windows-build runner; that half
/// of the contract is verifiable on CI alone.
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
        string firstMainStatement = programSourceLines
            .Skip(mainOpeningBraceLineIndex + 1)
            .Select(sourceLine => sourceLine.Trim())
            .SkipWhile(trimmedLine => trimmedLine.Length == 0 || trimmedLine.StartsWith("//"))
            .First();
        Assert.Equal("VelopackApp.Build().Run();", firstMainStatement);
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
