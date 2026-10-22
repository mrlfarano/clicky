using System.Text.Json.Nodes;
using Clicky.Core;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// WS-12 part 2 / T-018: user-settings persistence under the Clicky data directory (parity spec
/// ONB-04, CLA-08, PNL-03). The file-level rules (defaults, merge, corrupt-file handling, unknown-key
/// preservation) are pinned in Core by SettingsDefaultsGoldenVectorTests against
/// shared/golden/settings-defaults.json; these tests pin the persistence layer itself. The corrupt-file
/// and CLICKY_DATA_DIR tests launch the real Clicky.exe ("the app starts and one setting changes",
/// via --set-setting, the tracer stand-in for the panel's settings controls), because only a real run
/// proves startup really loads, merges and rewrites the file. The atomic-write and ship-time-keys tests
/// run in-process: an interrupted write can only be forced deterministically from inside the store, and
/// the shipped appsettings.json is a file read, not a behavior.
/// </summary>
[Collection(ClickyProcessReplayCollection.Name)] // two tests launch Clicky.exe: serialized with the other process-launching suites
public sealed class SettingsPersistenceTests
{
    [Fact]
    public async Task CorruptFileBecomesValidAfterOneChange()
    {
        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        try
        {
            // The golden vector's corrupt-file text (settings-defaults.json, case "corrupt-file"): JSON
            // truncated mid-key, which cannot parse. Its selectedClaudeModel fragment must NOT come back.
            File.WriteAllText(
                Path.Combine(dataDirectoryPath, "settings.json"),
                "{\"selectedClaudeModel\": \"claude-opus-4-6\", \"hasSub");

            ClickyProcessRun setSettingRun = await ClickyProcessLauncher.RunAsync(
                dataDirectoryPath, "--set-setting", "hasCompletedOnboarding=true");

            Assert.True(setSettingRun.ExitCode == 0,
                $"--set-setting must exit zero, got {setSettingRun.ExitCode}. Output:\n{setSettingRun.ProcessOutput}");
            // The run really noticed the corrupt file (the merger flagged it during startup).
            Assert.Contains("corrupt", setSettingRun.ProcessOutput);

            // ONB-04 (adr-derived): after that one change the file parses and holds the defaults plus
            // exactly that change — the corrupt flag path must not persist the corruption.
            string settingsFileContentsAfterChange = await ReadSettingsFileContentsAsync(dataDirectoryPath);
            CompanionSettingsMergeResult mergedAfterChange =
                CompanionSettingsMerger.MergeUserSettingsFileContentsOverDefaults(settingsFileContentsAfterChange);
            Assert.False(mergedAfterChange.UserSettingsFileWasCorrupt,
                $"the corrupt file must be rewritten valid by one change; contents:\n{settingsFileContentsAfterChange}");
            Assert.Equal(CompanionSettings.Defaults with { HasCompletedOnboarding = true }, mergedAfterChange.Settings);
        }
        finally
        {
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task DataDirOverrideHonoured()
    {
        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        try
        {
            ClickyProcessRun setSettingRun = await ClickyProcessLauncher.RunAsync(
                dataDirectoryPath, "--set-setting", "isClickyCursorEnabled=false");

            Assert.True(setSettingRun.ExitCode == 0,
                $"--set-setting must exit zero, got {setSettingRun.ExitCode}. Output:\n{setSettingRun.ProcessOutput}");

            // The write must land under the CLICKY_DATA_DIR the launcher set (WS-12: the override exists
            // so tests never touch the user's real profile), as settings.json at the directory root.
            Assert.Single(Directory.GetFiles(dataDirectoryPath, "settings.json", SearchOption.AllDirectories));

            // And what landed under the override parses with the defaults plus the change.
            string settingsFileContentsAfterChange = await ReadSettingsFileContentsAsync(dataDirectoryPath);
            CompanionSettingsMergeResult mergedAfterChange =
                CompanionSettingsMerger.MergeUserSettingsFileContentsOverDefaults(settingsFileContentsAfterChange);
            Assert.False(mergedAfterChange.UserSettingsFileWasCorrupt);
            Assert.Equal(CompanionSettings.Defaults with { IsClickyCursorEnabled = false }, mergedAfterChange.Settings);
        }
        finally
        {
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public void WriteIsAtomic()
    {
        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        try
        {
            string settingsFilePath = Path.Combine(dataDirectoryPath, "settings.json");
            string temporarySettingsFilePath = settingsFilePath + ".tmp";
            // A valid old file: Opus selected, everything else at its default.
            File.WriteAllText(settingsFilePath, "{\n  \"selectedClaudeModel\": \"claude-opus-4-6\"\n}");

            // The store's interrupt seam fires exactly between the temp-file write and the swap — the
            // interruption point the AC names — and throws the way abrupt process death would.
            var interruptedStore = new ClickyUserSettingsStore(
                dataDirectoryPath,
                interruptActionToRunAfterTempFileIsWritten: () => throw new IOException(
                    "simulated process death after the temp file was written, before the swap"));
            CompanionSettingsMergeResult loadedFromOldFile = interruptedStore.Load();
            Assert.False(loadedFromOldFile.UserSettingsFileWasCorrupt);
            CompanionSettings changedSettings = loadedFromOldFile.Settings with { HasSubmittedEmail = true };

            Assert.Throws<IOException>(() => interruptedStore.Save(changedSettings));

            // The write really got as far as the temp file, and settings.json itself is a whole file —
            // old or new, never a partial mix of the two.
            Assert.True(File.Exists(temporarySettingsFilePath),
                "the interrupted write must have produced the temp file");
            string settingsFileContentsAfterInterrupt = File.ReadAllText(settingsFilePath);
            CompanionSettingsMergeResult mergedAfterInterrupt =
                CompanionSettingsMerger.MergeUserSettingsFileContentsOverDefaults(settingsFileContentsAfterInterrupt);
            Assert.False(mergedAfterInterrupt.UserSettingsFileWasCorrupt,
                $"an interrupted write must leave settings.json old or new, never partial; contents:\n{settingsFileContentsAfterInterrupt}");
            Assert.True(mergedAfterInterrupt.Settings == loadedFromOldFile.Settings || mergedAfterInterrupt.Settings == changedSettings,
                $"settings.json must hold the old or the new settings, got something else; contents:\n{settingsFileContentsAfterInterrupt}");

            // The next process (a store with no interruption) recovers: it rewrites over the leftover
            // temp file and consumes it, and the change it carries survives a fresh load.
            var nextProcessStore = new ClickyUserSettingsStore(dataDirectoryPath);
            nextProcessStore.Load();
            nextProcessStore.Save(changedSettings);
            Assert.False(File.Exists(temporarySettingsFilePath), "the leftover temp file must be consumed by the next write");
            CompanionSettingsMergeResult mergedAfterRecovery =
                CompanionSettingsMerger.MergeUserSettingsFileContentsOverDefaults(File.ReadAllText(settingsFilePath));
            Assert.False(mergedAfterRecovery.UserSettingsFileWasCorrupt);
            Assert.Equal(changedSettings, mergedAfterRecovery.Settings);
        }
        finally
        {
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public void ShipTimeKeysPresent()
    {
        string shippedAppSettingsFilePath = Path.Combine(
            ClickyReplayProcess.RepositoryRootDirectory, "windows", "src", "Clicky.Windows", "appsettings.json");
        Assert.True(File.Exists(shippedAppSettingsFilePath), $"the shipped appsettings.json is missing at {shippedAppSettingsFilePath}");

        JsonObject shippedAppSettingsRoot = JsonNode.Parse(File.ReadAllText(shippedAppSettingsFilePath))!.AsObject();

        // WS-12 pins the ship-time keys that live next to the exe: WorkerBaseUrl (ships empty — no
        // placeholder), UpdateFeedUrl (empty = the updater stays off, WS-10), the PostHog host and
        // project key, and the Claude model list the picker offers.
        Assert.NotNull(shippedAppSettingsRoot["WorkerBaseUrl"]);
        Assert.NotNull(shippedAppSettingsRoot["UpdateFeedUrl"]);
        Assert.NotNull(shippedAppSettingsRoot["PostHogHost"]);
        Assert.NotNull(shippedAppSettingsRoot["PostHogApiKey"]);
        Assert.NotNull(shippedAppSettingsRoot["ClaudeModels"]);

        ClickySettings shipTimeSettings = ClickySettings.LoadFromFile(shippedAppSettingsFilePath);
        // The PostHog host is parity-pinned to the Swift client's host (AN-03, ClickyAnalytics.swift:16-22).
        Assert.Equal("https://us.i.posthog.com", shipTimeSettings.PostHogHost);
        Assert.False(string.IsNullOrWhiteSpace(shipTimeSettings.PostHogApiKey), "the PostHog project key must ship non-empty");
        // The model list offers exactly the macOS picker's Sonnet default (CompanionManager.swift:111)
        // and the optional Opus (CompanionPanelView.swift:610-611).
        Assert.Contains(CompanionSettings.DefaultClaudeModel, shipTimeSettings.ClaudeModels);
        Assert.Contains("claude-opus-4-6", shipTimeSettings.ClaudeModels);

        // A7: the version comes from the assembly, so the shipped file must not carry one of its own.
        Assert.All(shippedAppSettingsRoot, shippedSettingProperty =>
            Assert.DoesNotContain("version", shippedSettingProperty.Key, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            typeof(ClickySettings).Assembly.GetName().Version?.ToString(),
            shipTimeSettings.ApplicationVersion);
        Assert.False(string.IsNullOrWhiteSpace(shipTimeSettings.ApplicationVersion));
    }

    private static async Task<string> ReadSettingsFileContentsAsync(string dataDirectoryPath)
    {
        string settingsFilePath = Path.Combine(dataDirectoryPath, "settings.json");
        Assert.True(File.Exists(settingsFilePath), $"no settings.json was written under {dataDirectoryPath}");
        return await File.ReadAllTextAsync(settingsFilePath, TestContext.Current.CancellationToken);
    }
}
