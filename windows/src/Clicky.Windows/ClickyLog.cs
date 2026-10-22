namespace Clicky.Windows;

/// <summary>
/// Timestamped log to the console and to %LOCALAPPDATA%\Clicky\clicky.log. QA evidence for the acceptance
/// items is read from these lines (for example "ptt pressed", "watchdog: hook reinstalled").
/// </summary>
internal static class ClickyLog
{
    private static readonly Lock WriteLock = new();
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clicky", "clicky.log");

    public static void Write(string message)
    {
        string timestampedLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (WriteLock)
        {
            Console.WriteLine(timestampedLine);
            Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
            File.AppendAllText(LogFilePath, timestampedLine + Environment.NewLine);
        }
    }
}
