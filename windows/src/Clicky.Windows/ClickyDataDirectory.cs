namespace Clicky.Windows;

/// <summary>
/// The root of Clicky's per-user data on disk: %LOCALAPPDATA%\Clicky by default, holding the rolling
/// logs (logs\clicky-*.log, WS-11) and the local crash reports (crashes\&lt;utc&gt;.txt/.dmp). The
/// CLICKY_DATA_DIR environment variable overrides the root so tests can point a real Clicky.exe at a
/// throwaway directory instead of the user's real profile (WS-11 and WS-12 both rely on this).
/// Resolved once per process, so the override must be set before the process starts.
/// </summary>
internal static class ClickyDataDirectory
{
    public const string OverrideEnvironmentVariableName = "CLICKY_DATA_DIR";

    public static string RootPath { get; } = ResolveRootPath();

    private static string ResolveRootPath()
    {
        string? environmentOverridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(environmentOverridePath))
        {
            // GetFullPath normalizes a relative override against the launching process's current directory.
            return Path.GetFullPath(environmentOverridePath);
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clicky");
    }
}
