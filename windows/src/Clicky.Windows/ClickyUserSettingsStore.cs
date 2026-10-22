using System.Text.Json;
using System.Text.Json.Nodes;
using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// The user's settings file (ONB-04 / WS-12): settings.json at the root of the Clicky data directory
/// (%LOCALAPPDATA%\Clicky by default; <see cref="ClickyDataDirectory"/> honors CLICKY_DATA_DIR, which is
/// how every test points the app at a throwaway directory). Loading merges the file over
/// <see cref="CompanionSettings.Defaults"/> with the Core merger, whose rules — missing key takes its
/// default, corrupt file takes every default and is flagged, unknown keys are preserved — are pinned by
/// shared/golden/settings-defaults.json. Saving is atomic: the new JSON is written to a temp file next to
/// settings.json and swapped in with File.Replace, so a write interrupted after the temp file leaves
/// settings.json either the whole old file or the whole new file, never a partial mix.
/// </summary>
internal sealed class ClickyUserSettingsStore
{
    private const string SettingsFileName = "settings.json";
    private const string TempSettingsFileSuffix = ".tmp";

    /// <summary>Indented so QA can read the file contents as parity evidence (ONB-04 "file contents before and after a restart").</summary>
    private static readonly JsonSerializerOptions SettingsFileJsonOptions = new() { WriteIndented = true };

    private readonly Action? interruptActionToRunAfterTempFileIsWritten;

    /// <summary>
    /// Unrecognized keys captured by the last <see cref="Load"/>, written back verbatim by the next
    /// <see cref="Save"/> so a settings file written by a newer Clicky survives a round-trip through an
    /// older one (the merger's preservation contract).
    /// </summary>
    private Dictionary<string, JsonNode?> unrecognizedUserSettingsValuesFromLastLoad = [];

    public ClickyUserSettingsStore(string? settingsDirectoryPath = null, Action? interruptActionToRunAfterTempFileIsWritten = null)
    {
        // An explicit directory keeps the in-process atomicity test off the real profile; the app itself
        // passes nothing and lands on ClickyDataDirectory.RootPath, where CLICKY_DATA_DIR is honored.
        SettingsDirectoryPath = settingsDirectoryPath ?? ClickyDataDirectory.RootPath;
        SettingsFilePath = Path.Combine(SettingsDirectoryPath, SettingsFileName);
        this.interruptActionToRunAfterTempFileIsWritten = interruptActionToRunAfterTempFileIsWritten;
    }

    public string SettingsDirectoryPath { get; }

    public string SettingsFilePath { get; }

    /// <summary>
    /// Reads settings.json (a missing file is first launch, not corruption) and merges it over the
    /// defaults with the Core merger. The result's <see cref="CompanionSettingsMergeResult.Settings"/>
    /// is what the app runs with; a corrupt file yields every default and the corrupt flag, and the
    /// next <see cref="Save"/> rewrites the file valid (ONB-04, adr-derived Windows-only rule).
    /// </summary>
    public CompanionSettingsMergeResult Load()
    {
        string? userSettingsFileContents = File.Exists(SettingsFilePath)
            ? File.ReadAllText(SettingsFilePath)
            : null;
        CompanionSettingsMergeResult mergeResult = CompanionSettingsMerger.MergeUserSettingsFileContentsOverDefaults(userSettingsFileContents);
        unrecognizedUserSettingsValuesFromLastLoad = new Dictionary<string, JsonNode?>(mergeResult.UnrecognizedUserSettingsValues);
        return mergeResult;
    }

    /// <summary>
    /// Writes every known setting (plus the unrecognized keys the last Load preserved) atomically:
    /// temp file first, then File.Replace (a plain rename on the first-ever write, because
    /// File.Replace needs an existing destination). Either the whole new file lands or the old file
    /// stays whole — an interrupted write can never leave a partial settings.json behind.
    /// </summary>
    public void Save(CompanionSettings settingsToWrite)
    {
        Directory.CreateDirectory(SettingsDirectoryPath);
        string settingsFileJson = BuildSettingsFileJson(settingsToWrite);
        string temporarySettingsFilePath = SettingsFilePath + TempSettingsFileSuffix;
        File.WriteAllText(temporarySettingsFilePath, settingsFileJson);
        // Test seam: simulates the process dying exactly between the temp-file write and the swap, the
        // interruption point SettingsPersistenceTests.WriteIsAtomic pins. Never set by the app itself.
        interruptActionToRunAfterTempFileIsWritten?.Invoke();
        if (File.Exists(SettingsFilePath))
        {
            File.Replace(temporarySettingsFilePath, SettingsFilePath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporarySettingsFilePath, SettingsFilePath);
        }
    }

    private string BuildSettingsFileJson(CompanionSettings settingsToWrite)
    {
        // Known keys in the merger's documented order (WS-12), then preserved unknown keys, so the file
        // is stable across writes and diffs cleanly in the QA evidence.
        var settingsFileObject = new JsonObject
        {
            [CompanionSettingsMerger.SelectedClaudeModelSettingsFileKeyName] = settingsToWrite.SelectedClaudeModel,
            [CompanionSettingsMerger.IsClickyCursorEnabledSettingsFileKeyName] = settingsToWrite.IsClickyCursorEnabled,
            [CompanionSettingsMerger.HasCompletedOnboardingSettingsFileKeyName] = settingsToWrite.HasCompletedOnboarding,
            [CompanionSettingsMerger.HasSubmittedEmailSettingsFileKeyName] = settingsToWrite.HasSubmittedEmail,
            [CompanionSettingsMerger.PushToTalkShortcutSettingsFileKeyName] = BuildPersistedShortcutName(settingsToWrite.PushToTalkShortcut),
            [CompanionSettingsMerger.AnalyticsDistinctIdSettingsFileKeyName] = settingsToWrite.AnalyticsDistinctId,
        };
        foreach (KeyValuePair<string, JsonNode?> unrecognizedUserSettingsValue in unrecognizedUserSettingsValuesFromLastLoad)
        {
            settingsFileObject[unrecognizedUserSettingsValue.Key] = unrecognizedUserSettingsValue.Value?.DeepClone();
        }
        return settingsFileObject.ToJsonString(SettingsFileJsonOptions);
    }

    /// <summary>
    /// The persisted camelCase shortcut name ("controlOption"), the spelling the settings-defaults golden
    /// vector and BuddyDictationManager.swift:17-22 use. The merger parses case-insensitively, so writing
    /// the enum's PascalCase would round-trip; camelCase keeps the file matching every other settings file.
    /// </summary>
    private static string BuildPersistedShortcutName(PushToTalkShortcutOption shortcutOption)
    {
        string shortcutOptionName = shortcutOption.ToString();
        return char.ToLowerInvariant(shortcutOptionName[0]) + shortcutOptionName[1..];
    }
}
