using System.Diagnostics;
using Xunit;

namespace Clicky.Windows.Tests;

internal sealed record ClickyProcessRun(int ExitCode, string ProcessOutput, string DataDirectoryPath)
{
    public string LogsDirectoryPath => Path.Combine(DataDirectoryPath, "logs");

    public string CrashesDirectoryPath => Path.Combine(DataDirectoryPath, "crashes");
}

/// <summary>
/// Runs the built Clicky.exe against a throwaway data directory so crash-reporting tests can inspect
/// exactly what the app wrote there (T-016). Unlike ClickyReplayProcess this launcher makes no
/// assumptions about a report file or a zero exit: --debug-crash exits non-zero and writes no report.
/// The data directory is redirected through the CLICKY_DATA_DIR environment variable the app reads.
/// </summary>
internal static class ClickyProcessLauncher
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Creates an empty temporary directory to hand to the app as its data directory root.</summary>
    public static string CreateTempDataDirectory()
    {
        string dataDirectoryPath = Path.Combine(Path.GetTempPath(), $"clicky-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectoryPath);
        return dataDirectoryPath;
    }

    public static async Task<ClickyProcessRun> RunAsync(string dataDirectoryPath, params string[] clickyArguments)
    {
        Assert.True(File.Exists(ClickyReplayProcess.ClickyExecutablePath), $"Build Clicky.Windows first; not found: {ClickyReplayProcess.ClickyExecutablePath}");

        var clickyStartInfo = new ProcessStartInfo(ClickyReplayProcess.ClickyExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        clickyStartInfo.Environment["CLICKY_DATA_DIR"] = dataDirectoryPath;
        foreach (string clickyArgument in clickyArguments)
        {
            clickyStartInfo.ArgumentList.Add(clickyArgument);
        }

        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;
        using Process clickyProcess = Process.Start(clickyStartInfo)!;
        Task<string> standardOutputText = clickyProcess.StandardOutput.ReadToEndAsync(testCancellationToken);
        Task<string> standardErrorText = clickyProcess.StandardError.ReadToEndAsync(testCancellationToken);
        using (var runTimeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellationToken))
        {
            runTimeoutCancellation.CancelAfter(RunTimeout);
            try
            {
                await clickyProcess.WaitForExitAsync(runTimeoutCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                clickyProcess.Kill(entireProcessTree: true);
                Assert.Fail($"Clicky.exe ({string.Join(' ', clickyArguments)}) did not exit within {RunTimeout.TotalSeconds} s");
            }
        }
        string processOutput = await standardOutputText + await standardErrorText;
        return new ClickyProcessRun(clickyProcess.ExitCode, processOutput, dataDirectoryPath);
    }
}
