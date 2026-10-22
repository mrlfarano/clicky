using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Windows.Tests;

internal sealed record ReplayRun(int ExitCode, JsonNode Report, string ProcessOutput);

/// <summary>Used for runs that fail during startup (before any report is written), such as a bad manifest.</summary>
internal sealed record ClickyProcessRun(int ExitCode, string ProcessOutput);

/// <summary>
/// Puts every test class that launches Clicky.exe into one xunit collection, so the runs are sequential.
/// Each run does real multi-monitor capture and WPF rendering; two instances at once starve the
/// render-latency assertions (EventDelayReplayRendersTheFirstTokenBeforeTheStreamEnds bounds them at 300 ms).
/// </summary>
[CollectionDefinition(Name)]
public sealed class ClickyProcessReplayCollection
{
    public const string Name = "Clicky process replay";
}

/// <summary>Launches the built Clicky.exe in replay mode and returns its exit code and JSON report.</summary>
internal static class ClickyReplayProcess
{
    private static readonly TimeSpan ReplayTimeout = TimeSpan.FromSeconds(60);

    public static string ClickyExecutablePath { get; } = ReadAssemblyMetadata("ClickyExecutablePath");

    public static string RepositoryRootDirectory { get; } = ReadAssemblyMetadata("RepositoryRootDirectory");

    public static async Task<ReplayRun> RunAsync(string fixtureFileName, params string[] extraArguments)
    {
        string replayFixturePath = Path.Combine(RepositoryRootDirectory, "shared", "fixtures", "sse", fixtureFileName);
        return await RunReportingModeAsync(
            ["--replay", replayFixturePath],
            $"--replay {fixtureFileName}",
            extraArguments);
    }

    /// <summary>Runs Clicky.exe --replay-manifest <paramref name="manifestPath"/> and returns its exit code and JSON report.</summary>
    public static Task<ReplayRun> RunManifestAsync(string manifestPath, params string[] extraArguments) =>
        RunReportingModeAsync(["--replay-manifest", manifestPath], $"--replay-manifest {manifestPath}", extraArguments);

    /// <summary>
    /// Runs Clicky.exe with exactly the given arguments and no report expectations, for invocations that are
    /// expected to fail during startup (for example an invalid manifest), where no report is ever written.
    /// </summary>
    public static async Task<ClickyProcessRun> RunArgumentsAsync(params string[] clickyArguments)
    {
        string reportPath = Path.Combine(Path.GetTempPath(), $"clicky-replay-report-{Guid.NewGuid():N}.json");
        var clickyProcessRun = await StartClickyAndWaitForExitAsync(
            [.. clickyArguments, "--report", reportPath], $"Clicky.exe {string.Join(" ", clickyArguments)}");
        File.Delete(reportPath);
        return clickyProcessRun;
    }

    /// <summary>Runs one of the report-writing replay modes and parses the report the run must have written.</summary>
    private static async Task<ReplayRun> RunReportingModeAsync(string[] replayModeArguments, string replayModeDescription, string[] extraArguments)
    {
        Assert.True(File.Exists(ClickyExecutablePath), $"Build Clicky.Windows first; not found: {ClickyExecutablePath}");

        string reportPath = Path.Combine(Path.GetTempPath(), $"clicky-replay-report-{Guid.NewGuid():N}.json");
        ClickyProcessRun clickyProcessRun = await StartClickyAndWaitForExitAsync(
            [.. replayModeArguments, "--report", reportPath, .. extraArguments], replayModeDescription);

        Assert.True(File.Exists(reportPath), $"Clicky.exe {replayModeDescription} wrote no report. Output:\n{clickyProcessRun.ProcessOutput}");
        JsonNode report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken))!;
        File.Delete(reportPath);
        return new ReplayRun(clickyProcessRun.ExitCode, report, clickyProcessRun.ProcessOutput);
    }

    private static async Task<ClickyProcessRun> StartClickyAndWaitForExitAsync(string[] clickyArguments, string invocationDescription)
    {
        var clickyStartInfo = new ProcessStartInfo(ClickyExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string clickyArgument in clickyArguments)
        {
            clickyStartInfo.ArgumentList.Add(clickyArgument);
        }

        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;
        using Process clickyProcess = Process.Start(clickyStartInfo)!;
        Task<string> standardOutputText = clickyProcess.StandardOutput.ReadToEndAsync(testCancellationToken);
        Task<string> standardErrorText = clickyProcess.StandardError.ReadToEndAsync(testCancellationToken);
        using (var replayTimeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellationToken))
        {
            replayTimeoutCancellation.CancelAfter(ReplayTimeout);
            try
            {
                await clickyProcess.WaitForExitAsync(replayTimeoutCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                clickyProcess.Kill(entireProcessTree: true);
                Assert.Fail($"Clicky.exe {invocationDescription} did not exit within {ReplayTimeout.TotalSeconds} s");
            }
        }
        string processOutput = await standardOutputText + await standardErrorText;
        return new ClickyProcessRun(clickyProcess.ExitCode, processOutput);
    }

    private static string ReadAssemblyMetadata(string metadataKey) => typeof(ClickyReplayProcess).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(metadataAttribute => metadataAttribute.Key == metadataKey)
        .Value!;
}
