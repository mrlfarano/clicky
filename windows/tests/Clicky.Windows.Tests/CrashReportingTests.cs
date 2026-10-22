using System.Text;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// WS-11 / T-016: the rolling file log and local crash reporting. Every test launches the real
/// Clicky.exe against a throwaway CLICKY_DATA_DIR, because crash reporting only means anything when
/// the process actually dies and the dump-writing interop actually runs.
/// </summary>
[Collection(ClickyProcessReplayCollection.Name)] // launches Clicky.exe: serialized with the other process-launching suites (T-001 flake fix)
public sealed class CrashReportingTests
{
    // Same value as ReplayEndToEndTests.PointBasicSpokenText: the spoken text the point-basic fixture streams.
    private const string PointBasicSpokenText = "that's the save button up in the toolbar. click it and you're done.";

    private static string PointBasicFixturePath =>
        Path.Combine(ClickyReplayProcess.RepositoryRootDirectory, "shared", "fixtures", "sse", "point-basic.sse");

    [Fact]
    public async Task DebugCrashLeavesOneTxtAndOneDmp()
    {
        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        try
        {
            ClickyProcessRun debugCrashRun = await ClickyProcessLauncher.RunAsync(dataDirectoryPath, "--debug-crash");

            Assert.True(debugCrashRun.ExitCode != 0, $"--debug-crash must exit non-zero, got {debugCrashRun.ExitCode}. Output:\n{debugCrashRun.ProcessOutput}");
            Assert.True(Directory.Exists(debugCrashRun.CrashesDirectoryPath), $"no crashes directory under {dataDirectoryPath}. Output:\n{debugCrashRun.ProcessOutput}");

            string crashTextFilePath = Assert.Single(Directory.GetFiles(debugCrashRun.CrashesDirectoryPath, "*.txt"));
            string crashDumpFilePath = Assert.Single(Directory.GetFiles(debugCrashRun.CrashesDirectoryPath, "*.dmp"));
            string crashTextFileContents = await File.ReadAllTextAsync(crashTextFilePath, TestContext.Current.CancellationToken);
            Assert.Contains("DeliberateDebugCrashException", crashTextFileContents);
            Assert.True(new FileInfo(crashDumpFilePath).Length > 0, "the minidump file is empty");
        }
        finally
        {
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task NoFileContainsResponseText()
    {
        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        try
        {
            // Replay first so the response text genuinely transits the app, then crash, so the crash
            // text file (with its embedded log lines) is scanned too — not just the log itself.
            ClickyProcessRun replayAndCrashRun = await ClickyProcessLauncher.RunAsync(
                dataDirectoryPath, "--replay", PointBasicFixturePath, "--debug-crash");

            Assert.True(replayAndCrashRun.ExitCode != 0, $"--replay --debug-crash must exit non-zero, got {replayAndCrashRun.ExitCode}. Output:\n{replayAndCrashRun.ProcessOutput}");

            // The interaction really ran and logged the spoken text — by length only, which is the
            // assertion under test — so the scan below cannot pass vacuously.
            string logText = ReadEveryLogFile(replayAndCrashRun.LogsDirectoryPath);
            Assert.Contains($"spoken text: {PointBasicSpokenText.Length} chars", logText);

            foreach (string dataFilePath in Directory.EnumerateFiles(dataDirectoryPath, "*", SearchOption.AllDirectories))
            {
                byte[] dataFileBytes = await File.ReadAllBytesAsync(dataFilePath, TestContext.Current.CancellationToken);
                // The log and crash text files are UTF-8; the minidump is binary, where an embedded
                // managed string would sit as UTF-16, so both decodings are checked.
                Assert.DoesNotContain(PointBasicSpokenText, Encoding.UTF8.GetString(dataFileBytes));
                Assert.DoesNotContain(PointBasicSpokenText, Encoding.Unicode.GetString(dataFileBytes));
            }
        }
        finally
        {
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task LogRecordsLengthNotContent()
    {
        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        try
        {
            ClickyProcessRun replayRun = await ClickyProcessLauncher.RunAsync(dataDirectoryPath, "--replay", PointBasicFixturePath);

            Assert.True(replayRun.ExitCode == 0, $"replay must exit zero, got {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");
            string logText = ReadEveryLogFile(replayRun.LogsDirectoryPath);
            Assert.Contains($"spoken text: {PointBasicSpokenText.Length} chars", logText);
            Assert.DoesNotContain(PointBasicSpokenText, logText);
        }
        finally
        {
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task KeepsNewestFive()
    {
        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        // Six stale crashes and six stale log files, dated 2020 so every name the run writes sorts
        // after them. The newest seeded log is over the 1 MB roll size, so the run's first log write
        // must roll to a fresh file instead of appending.
        string seededNewestLogFilePath = Path.Combine(dataDirectoryPath, "logs", "clicky-20200101T000005Z.log");
        Directory.CreateDirectory(Path.Combine(dataDirectoryPath, "crashes"));
        try
        {
            for (int seededCrashIndex = 0; seededCrashIndex < 6; seededCrashIndex++)
            {
                string seededCrashFileStem = $"20200101T00000{seededCrashIndex}Z";
                File.WriteAllText(
                    Path.Combine(dataDirectoryPath, "crashes", seededCrashFileStem + ".txt"),
                    $"seeded crash {seededCrashFileStem} for KeepsNewestFive");
                File.WriteAllBytes(
                    Path.Combine(dataDirectoryPath, "crashes", seededCrashFileStem + ".dmp"), [1, 2, 3, 4]);
                WriteSeededLogFile(
                    Path.Combine(dataDirectoryPath, "logs", $"clicky-20200101T00000{seededCrashIndex}Z.log"),
                    minimumSeededLogByteSize: seededCrashIndex == 5 ? 1100 * 1024 : 1024);
            }
            long seededNewestLogByteSize = new FileInfo(seededNewestLogFilePath).Length;

            ClickyProcessRun debugCrashRun = await ClickyProcessLauncher.RunAsync(dataDirectoryPath, "--debug-crash");
            Assert.True(debugCrashRun.ExitCode != 0, $"--debug-crash must exit non-zero, got {debugCrashRun.ExitCode}. Output:\n{debugCrashRun.ProcessOutput}");

            // Crashes: the six seeded pairs plus the new one are pruned to the newest five.
            string[] remainingCrashTextFilePaths = Directory.GetFiles(debugCrashRun.CrashesDirectoryPath, "*.txt");
            string[] remainingCrashDumpFilePaths = Directory.GetFiles(debugCrashRun.CrashesDirectoryPath, "*.dmp");
            Assert.Equal(5, remainingCrashTextFilePaths.Length);
            Assert.Equal(5, remainingCrashDumpFilePaths.Length);
            Assert.False(File.Exists(Path.Combine(debugCrashRun.CrashesDirectoryPath, "20200101T000000Z.txt")), "the oldest seeded crash survived pruning");
            Assert.False(File.Exists(Path.Combine(debugCrashRun.CrashesDirectoryPath, "20200101T000001Z.txt")), "the second-oldest seeded crash survived pruning");
            Assert.True(File.Exists(Path.Combine(debugCrashRun.CrashesDirectoryPath, "20200101T000005Z.txt")), "the newest seeded crash must be kept");
            // The run's own crash is the one whose text file names the deliberate exception.
            Assert.Contains(remainingCrashTextFilePaths,
                remainingCrashTextFilePath => File.ReadAllText(remainingCrashTextFilePath).Contains("DeliberateDebugCrashException"));

            // Logs: rolled at the 1 MB cap and pruned to five files. The unchanged seeded size proves
            // the run rolled to a fresh file rather than appending past the cap; the fresh file holds
            // the run's lines only.
            string[] remainingLogFilePaths = Directory.GetFiles(debugCrashRun.LogsDirectoryPath, "clicky-*.log");
            Assert.Equal(5, remainingLogFilePaths.Length);
            Assert.False(File.Exists(Path.Combine(debugCrashRun.LogsDirectoryPath, "clicky-20200101T000000Z.log")), "the oldest seeded log survived pruning");
            Assert.False(File.Exists(Path.Combine(debugCrashRun.LogsDirectoryPath, "clicky-20200101T000001Z.log")), "the second-oldest seeded log survived pruning");
            Assert.Equal(seededNewestLogByteSize, new FileInfo(seededNewestLogFilePath).Length);
            string[] freshlyRolledLogFilePaths = remainingLogFilePaths
                .Where(remainingLogFilePath => Path.GetFileName(remainingLogFilePath) != "clicky-20200101T000002Z.log"
                    && Path.GetFileName(remainingLogFilePath) != "clicky-20200101T000003Z.log"
                    && Path.GetFileName(remainingLogFilePath) != "clicky-20200101T000004Z.log"
                    && Path.GetFileName(remainingLogFilePath) != "clicky-20200101T000005Z.log")
                .ToArray();
            string freshlyRolledLogFilePath = Assert.Single(freshlyRolledLogFilePaths);
            string freshlyRolledLogText = await File.ReadAllTextAsync(freshlyRolledLogFilePath, TestContext.Current.CancellationToken);
            Assert.Contains("debug crash", freshlyRolledLogText);
            Assert.True(new FileInfo(freshlyRolledLogFilePath).Length < 1024 * 1024, "the freshly rolled log already exceeds the 1 MB cap");
        }
        finally
        {
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
    }

    private static string ReadEveryLogFile(string logsDirectoryPath)
    {
        Assert.True(Directory.Exists(logsDirectoryPath), $"no logs directory at {logsDirectoryPath}");
        return string.Join(Environment.NewLine,
            Directory.GetFiles(logsDirectoryPath, "clicky-*.log").Select(File.ReadAllText));
    }

    /// <summary>Fills a seeded log file past a target size with padding that shares nothing with fixture text.</summary>
    private static void WriteSeededLogFile(string seededLogFilePath, long minimumSeededLogByteSize)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(seededLogFilePath)!);
        var seededPaddingLines = new List<string>();
        long seededLogByteSize = 0;
        for (int seededPaddingLineIndex = 0; seededLogByteSize < minimumSeededLogByteSize; seededPaddingLineIndex++)
        {
            string seededPaddingLine = $"seed log padding line {seededPaddingLineIndex:D6} exists only to push the seeded log file past the 1 MB roll size";
            seededPaddingLines.Add(seededPaddingLine);
            seededLogByteSize += seededPaddingLine.Length + Environment.NewLine.Length;
        }
        File.WriteAllLines(seededLogFilePath, seededPaddingLines);
    }
}
