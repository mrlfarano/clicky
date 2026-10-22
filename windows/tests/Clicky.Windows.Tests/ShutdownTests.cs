using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-030 / PNL-05: the WinExe lifecycle. The real Clicky.exe runs live with --debug-quit-after-ms against a
/// throwaway CLICKY_DATA_DIR (which also scopes the single-instance mutex, so these runs never meet the
/// developer's own Clicky). Live mode installs a real WH_KEYBOARD_LL hook but injects no input.
/// </summary>
[Collection(ClickyProcessReplayCollection.Name)] // launches Clicky.exe: serialized with the other process-launching suites
public sealed class ShutdownTests
{
    private static readonly TimeSpan FirstInstanceStartupWaitLimit = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task QuitExitsZeroAndLogsShutdownComplete()
    {
        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        try
        {
            ClickyProcessRun quitRun = await ClickyProcessLauncher.RunAsync(dataDirectoryPath, "--debug-quit-after-ms", "500");

            Assert.True(quitRun.ExitCode == 0, $"--debug-quit-after-ms must exit zero, got {quitRun.ExitCode}. Output:\n{quitRun.ProcessOutput}");
            Assert.Contains("shutdown complete", ReadEveryLogFile(quitRun.LogsDirectoryPath));
        }
        finally
        {
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task SecondInstanceInstallsNoSecondHook()
    {
        // Both instances share one data directory, so they share the mutex name and one log.
        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        try
        {
            // Long enough for the second instance to start and exit while the first is still idle and holding the mutex.
            Task<ClickyProcessRun> firstInstanceRun = ClickyProcessLauncher.RunAsync(dataDirectoryPath, "--debug-quit-after-ms", "10000");
            var startupWaitLimit = DateTime.UtcNow + FirstInstanceStartupWaitLimit;
            while (!ReadEveryLogFile(Path.Combine(dataDirectoryPath, "logs")).Contains("live: listening", StringComparison.Ordinal))
            {
                Assert.True(DateTime.UtcNow < startupWaitLimit && !firstInstanceRun.IsCompleted,
                    $"the first instance never reached live mode. Log:\n{ReadEveryLogFile(Path.Combine(dataDirectoryPath, "logs"))}");
                await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            }

            ClickyProcessRun secondInstance = await ClickyProcessLauncher.RunAsync(dataDirectoryPath, "--debug-quit-after-ms", "500");
            ClickyProcessRun firstInstance = await firstInstanceRun;

            string logText = ReadEveryLogFile(firstInstance.LogsDirectoryPath);
            Assert.True(secondInstance.ExitCode == 0, $"the second instance must exit zero, got {secondInstance.ExitCode}. Log:\n{logText}");
            Assert.True(firstInstance.ExitCode == 0, $"the first instance must exit zero, got {firstInstance.ExitCode}. Log:\n{logText}");
            Assert.Contains("single instance: Clicky is already running", logText);
            Assert.Equal(1, CountOccurrences(logText, "hook installed"));
        }
        finally
        {
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task OrderedShutdown()
    {
        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        try
        {
            ClickyProcessRun quitRun = await ClickyProcessLauncher.RunAsync(dataDirectoryPath, "--debug-quit-after-ms", "500");

            string logText = ReadEveryLogFile(quitRun.LogsDirectoryPath);
            Assert.True(quitRun.ExitCode == 0, $"exit code {quitRun.ExitCode}. Log:\n{logText}");
            // PNL-05's stop path order (CompanionManager.swift:290-296), then the exit itself.
            string[] shutdownStepsInOrder =
            [
                "shutdown: hook removed",
                "shutdown: dictation cancelled",
                "shutdown: overlay hidden",
                "shutdown: in-flight work cancelled",
                "shutdown complete",
            ];
            int previousStepIndex = -1;
            foreach (string shutdownStep in shutdownStepsInOrder)
            {
                int stepIndex = logText.IndexOf(shutdownStep, StringComparison.Ordinal);
                Assert.True(stepIndex > previousStepIndex, $"'{shutdownStep}' missing or out of order. Log:\n{logText}");
                previousStepIndex = stepIndex;
            }
        }
        finally
        {
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
    }

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;

    // Opened with FileShare.ReadWrite: the test polls the log while Clicky.exe is still appending to it.
    private static string ReadEveryLogFile(string logsDirectoryPath) =>
        Directory.Exists(logsDirectoryPath)
            ? string.Join(Environment.NewLine, Directory.GetFiles(logsDirectoryPath, "clicky-*.log").Select(logFilePath =>
            {
                using var logFileReader = new StreamReader(new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                return logFileReader.ReadToEnd();
            }))
            : "";
}
