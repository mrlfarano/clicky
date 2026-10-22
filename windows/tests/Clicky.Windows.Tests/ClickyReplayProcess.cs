using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Windows.Tests;

internal sealed record ReplayRun(int ExitCode, JsonNode Report, string ProcessOutput);

/// <summary>Launches the built Clicky.exe in replay mode and returns its exit code and JSON report.</summary>
internal static class ClickyReplayProcess
{
    private static readonly TimeSpan ReplayTimeout = TimeSpan.FromSeconds(60);

    public static string ClickyExecutablePath { get; } = ReadAssemblyMetadata("ClickyExecutablePath");

    public static string RepositoryRootDirectory { get; } = ReadAssemblyMetadata("RepositoryRootDirectory");

    public static async Task<ReplayRun> RunAsync(string fixtureFileName, params string[] extraArguments)
    {
        Assert.True(File.Exists(ClickyExecutablePath), $"Build Clicky.Windows first; not found: {ClickyExecutablePath}");

        string reportPath = Path.Combine(Path.GetTempPath(), $"clicky-replay-report-{Guid.NewGuid():N}.json");
        var clickyStartInfo = new ProcessStartInfo(ClickyExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        clickyStartInfo.ArgumentList.Add("--replay");
        clickyStartInfo.ArgumentList.Add(Path.Combine(RepositoryRootDirectory, "shared", "fixtures", "sse", fixtureFileName));
        clickyStartInfo.ArgumentList.Add("--report");
        clickyStartInfo.ArgumentList.Add(reportPath);
        foreach (string extraArgument in extraArguments)
        {
            clickyStartInfo.ArgumentList.Add(extraArgument);
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
                Assert.Fail($"Clicky.exe --replay {fixtureFileName} did not exit within {ReplayTimeout.TotalSeconds} s");
            }
        }
        string processOutput = await standardOutputText + await standardErrorText;

        Assert.True(File.Exists(reportPath), $"Clicky.exe wrote no report. Output:\n{processOutput}");
        JsonNode report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath, testCancellationToken))!;
        File.Delete(reportPath);
        return new ReplayRun(clickyProcess.ExitCode, report, processOutput);
    }

    private static string ReadAssemblyMetadata(string metadataKey) => typeof(ClickyReplayProcess).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(metadataAttribute => metadataAttribute.Key == metadataKey)
        .Value!;
}
