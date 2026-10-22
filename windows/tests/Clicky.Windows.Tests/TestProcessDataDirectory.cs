using Xunit;

[assembly: AssemblyFixture(typeof(Clicky.Windows.Tests.TestProcessDataDirectory))]

namespace Clicky.Windows.Tests;

/// <summary>
/// Points this test process's own ClickyLog at a throwaway data directory. HookRecoveryTests call
/// ClickyLog.Write in-process (T-007), and ClickyDataDirectory resolves CLICKY_DATA_DIR once per
/// process, so it must be set before any test runs or test lines land in the developer's real
/// %LOCALAPPDATA%\Clicky\logs. Launched Clicky.exe runs set their own directory and are unaffected.
/// </summary>
public sealed class TestProcessDataDirectory : IDisposable
{
    private readonly string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();

    public TestProcessDataDirectory() =>
        Environment.SetEnvironmentVariable(ClickyDataDirectory.OverrideEnvironmentVariableName, dataDirectoryPath);

    public void Dispose() => Directory.Delete(dataDirectoryPath, recursive: true);
}
