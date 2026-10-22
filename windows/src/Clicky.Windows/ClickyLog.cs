using System.Globalization;

namespace Clicky.Windows;

/// <summary>
/// Timestamped rolling file log (WS-11): every line goes to the console and to up to five log files
/// of at most 1 MB each under &lt;data directory&gt;\logs, named clicky-&lt;utc&gt;.log. QA reads acceptance
/// evidence from these lines (for example "ptt pressed", "watchdog: hook reinstalled").
/// Privacy rule (WS-11): the log records text LENGTHS, never transcript or response text, because
/// the crash report embeds the last lines of this log and must stay shareable without leaking what
/// Claude answered or what the user said.
/// </summary>
internal static class ClickyLog
{
    /// <summary>WS-11: the log rolls at 1 MB per file and keeps five files.</summary>
    private const long MaximumLogFileSizeBytes = 1024 * 1024;
    private const int KeptLogFileCount = 5;
    private const string LogFileNamePrefix = "clicky-";
    private const string LogFileNameSearchPattern = "clicky-*.log";

    private static readonly Lock WriteLock = new();

    /// <summary>The file currently being appended to; null until the first write resolves it. Guarded by WriteLock.</summary>
    private static string? currentLogFilePath;

    public static string LogDirectoryPath { get; } = Path.Combine(ClickyDataDirectory.RootPath, "logs");

    public static void Write(string message)
    {
        string timestampedLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (WriteLock)
        {
            string logFilePath = ResolveCurrentLogFilePathLocked(out bool logRolledWithThisWrite);
            // Shared with other writers and readers: a second instance logs its quiet exit into the running
            // instance's file (T-030), and File.AppendAllText would fail there with a sharing violation.
            using (var logFileWriter = new StreamWriter(new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)))
            {
                logFileWriter.WriteLine(timestampedLine);
            }
            if (logRolledWithThisWrite)
            {
                // Prune after the append so the fresh file is on disk and counted among the kept five.
                DeleteOldestLogFilesBeyondKeptCountLocked();
            }
            Console.WriteLine(timestampedLine);
        }
    }

    /// <summary>
    /// The newest log lines across the rolling files, for the crash report's "last 200 log lines"
    /// section (WS-11). Files are read newest-first and reading stops once enough lines are found,
    /// so a crash right after a roll still sees history instead of a fresh empty file.
    /// </summary>
    public static IReadOnlyList<string> ReadLastLoggedLines(int maximumLineCount)
    {
        lock (WriteLock)
        {
            var collectedLines = new List<string>(maximumLineCount);
            if (!Directory.Exists(LogDirectoryPath))
            {
                return collectedLines;
            }

            // File names are clicky-<fixed-width utc>, so ordinal name order is newest-file-first.
            foreach (string logFilePath in Directory.EnumerateFiles(LogDirectoryPath, LogFileNameSearchPattern)
                         .OrderDescending(StringComparer.Ordinal))
            {
                foreach (string logLine in File.ReadAllLines(logFilePath).Reverse())
                {
                    collectedLines.Add(logLine);
                    if (collectedLines.Count == maximumLineCount)
                    {
                        break;
                    }
                }
                if (collectedLines.Count == maximumLineCount)
                {
                    break;
                }
            }

            // The lines were collected newest-first; present them in the order they were written.
            collectedLines.Reverse();
            return collectedLines;
        }
    }

    /// <summary>Returns the file to append to, rolling to a fresh file when the current one hits the size cap.</summary>
    private static string ResolveCurrentLogFilePathLocked(out bool logRolledWithThisWrite)
    {
        logRolledWithThisWrite = false;
        if (currentLogFilePath is null)
        {
            Directory.CreateDirectory(LogDirectoryPath);
            // A restart continues the newest existing file rather than starting a new one, so five
            // files hold as much recent history as possible.
            currentLogFilePath = FindNewestExistingLogFilePathLocked() ?? BuildNewLogFilePath();
        }
        // A freshly built path does not exist until the append creates it, so the size check must not
        // assume the file is on disk yet.
        var currentLogFileInfo = new FileInfo(currentLogFilePath);
        if (currentLogFileInfo.Exists && currentLogFileInfo.Length >= MaximumLogFileSizeBytes)
        {
            // Roll: the current file reached the WS-11 size cap, so the next append goes to a fresh
            // file (pruning happens in Write once that file exists).
            currentLogFilePath = BuildNewLogFilePath();
            logRolledWithThisWrite = true;
        }
        return currentLogFilePath;
    }

    private static string? FindNewestExistingLogFilePathLocked() =>
        Directory.EnumerateFiles(LogDirectoryPath, LogFileNameSearchPattern)
            .OrderDescending(StringComparer.Ordinal)
            .FirstOrDefault();

    private static string BuildNewLogFilePath() => Path.Combine(
        LogDirectoryPath,
        // Fixed-width UTC digits sort chronologically by plain ordinal name comparison, which is what
        // the roll pruning and the newest-file lookup above rely on. InvariantCulture pins the digits.
        LogFileNamePrefix + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + ".log");

    private static void DeleteOldestLogFilesBeyondKeptCountLocked()
    {
        foreach (string oldLogFilePath in Directory.EnumerateFiles(LogDirectoryPath, LogFileNameSearchPattern)
                     .OrderDescending(StringComparer.Ordinal)
                     .Skip(KeptLogFileCount))
        {
            File.Delete(oldLogFilePath);
        }
    }
}
