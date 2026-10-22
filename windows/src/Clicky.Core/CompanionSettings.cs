using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clicky.Core;

/// <summary>
/// The user's persisted settings (parity spec ONB-04): which Claude model answers, whether the blue
/// cursor stays visible, how far onboarding got, the chosen push-to-talk shortcut and the analytics
/// distinct id. macOS keeps these in UserDefaults (CompanionManager.swift:111-150); Windows keeps
/// them as JSON in %LOCALAPPDATA%\Clicky\settings.json (ADR 001 "Configuration"), so this Core model
/// is the single source of truth for the key names, the defaults and the merge rules. It is the user
/// settings model only — the shell's ClickySettings reader covers the separate appsettings.json,
/// which holds ship-time values next to the executable.
/// </summary>
public sealed record CompanionSettings(
    string SelectedClaudeModel,
    bool IsClickyCursorEnabled,
    bool HasCompletedOnboarding,
    bool HasSubmittedEmail,
    PushToTalkShortcutOption PushToTalkShortcut,
    string? AnalyticsDistinctId)
{
    /// <summary>CompanionManager.swift:111: the model macOS falls back to when UserDefaults has no value.</summary>
    public const string DefaultClaudeModel = "claude-sonnet-4-6";

    /// <summary>
    /// Every setting at its ONB-04 default: Sonnet, cursor shown, onboarding and email not done, the
    /// default Ctrl+Alt shortcut, and no analytics distinct id yet. Windows has no PostHog SDK to
    /// manage an anonymous id, so the shell generates one on first launch and persists it here.
    /// </summary>
    public static CompanionSettings Defaults { get; } = new(
        SelectedClaudeModel: DefaultClaudeModel,
        IsClickyCursorEnabled: true,
        HasCompletedOnboarding: false,
        HasSubmittedEmail: false,
        PushToTalkShortcut: PushToTalkShortcutOption.ControlOption,
        AnalyticsDistinctId: null);
}

/// <summary>The outcome of laying a user settings file over <see cref="CompanionSettings.Defaults"/>.</summary>
/// <param name="Settings">The merged settings: every key present and valid in the file, every other key at its default.</param>
/// <param name="UserSettingsFileWasCorrupt">True when the file could not be read as a JSON object, so all defaults were used. ONB-04 (adr-derived, Windows-only): a corrupt file is rewritten as a valid one on the next settings change.</param>
/// <param name="UnrecognizedUserSettingsValues">Keys this model does not know (e.g. written by a newer Clicky) with their values, kept so the next settings write does not silently drop them. A null value means the file held JSON null.</param>
public sealed record CompanionSettingsMergeResult(
    CompanionSettings Settings,
    bool UserSettingsFileWasCorrupt,
    IReadOnlyDictionary<string, JsonNode?> UnrecognizedUserSettingsValues);

/// <summary>
/// ONB-04 merge rules for the user settings file: a key present and valid in the file replaces its
/// default, a missing or wrong-typed key falls back to its default, a file that is not a JSON object
/// at all yields every default plus the corrupt flag, and unknown keys are carried along rather than
/// rejected so a settings file written by a newer Clicky survives a round-trip through an older one.
/// </summary>
public static class CompanionSettingsMerger
{
    public const string SelectedClaudeModelSettingsFileKeyName = "selectedClaudeModel";
    public const string IsClickyCursorEnabledSettingsFileKeyName = "isClickyCursorEnabled";
    public const string HasCompletedOnboardingSettingsFileKeyName = "hasCompletedOnboarding";
    public const string HasSubmittedEmailSettingsFileKeyName = "hasSubmittedEmail";
    public const string PushToTalkShortcutSettingsFileKeyName = "pushToTalkShortcut";
    public const string AnalyticsDistinctIdSettingsFileKeyName = "analyticsDistinctId";

    /// <summary>Every settings file key this model knows, in the order the plan documents them (WS-12).</summary>
    public static readonly IReadOnlyList<string> KnownSettingsFileKeyNames =
    [
        SelectedClaudeModelSettingsFileKeyName,
        IsClickyCursorEnabledSettingsFileKeyName,
        HasCompletedOnboardingSettingsFileKeyName,
        HasSubmittedEmailSettingsFileKeyName,
        PushToTalkShortcutSettingsFileKeyName,
        AnalyticsDistinctIdSettingsFileKeyName,
    ];

    private static readonly HashSet<string> KnownSettingsFileKeyNameSet = [.. KnownSettingsFileKeyNames];

    /// <summary>
    /// Applies the exact text of %LOCALAPPDATA%\Clicky\settings.json over
    /// <see cref="CompanionSettings.Defaults"/>. A null <paramref name="userSettingsFileContents"/>
    /// means the file does not exist yet (first launch), which is not corruption: every key simply
    /// takes its default.
    /// </summary>
    public static CompanionSettingsMergeResult MergeUserSettingsFileContentsOverDefaults(string? userSettingsFileContents)
    {
        if (userSettingsFileContents is null)
        {
            return new CompanionSettingsMergeResult(
                CompanionSettings.Defaults,
                UserSettingsFileWasCorrupt: false,
                UnrecognizedUserSettingsValues: new Dictionary<string, JsonNode?>());
        }

        JsonNode? parsedUserSettingsRoot;
        try
        {
            parsedUserSettingsRoot = JsonNode.Parse(userSettingsFileContents);
        }
        catch (JsonException)
        {
            return CorruptFileResult();
        }

        // A settings file is a JSON object of keys; a JSON array, string or number gets the same
        // all-defaults corrupt result as unparsable text (ONB-04).
        if (parsedUserSettingsRoot is not JsonObject userSettingsObject)
        {
            return CorruptFileResult();
        }

        var unrecognizedUserSettingsValues = new Dictionary<string, JsonNode?>();
        foreach (KeyValuePair<string, JsonNode?> settingsFileProperty in userSettingsObject)
        {
            if (KnownSettingsFileKeyNameSet.Contains(settingsFileProperty.Key))
            {
                continue;
            }
            // Deep-clone so the preserved value is detached from this parse's tree and stays valid
            // for the caller's next write (the persistence layer writes it back verbatim).
            unrecognizedUserSettingsValues[settingsFileProperty.Key] = settingsFileProperty.Value?.DeepClone();
        }

        CompanionSettings defaultSettings = CompanionSettings.Defaults;
        return new CompanionSettingsMergeResult(
            Settings: new CompanionSettings(
                SelectedClaudeModel: TryReadStringSetting(userSettingsObject, SelectedClaudeModelSettingsFileKeyName)
                    ?? defaultSettings.SelectedClaudeModel,
                IsClickyCursorEnabled: TryReadBooleanSetting(userSettingsObject, IsClickyCursorEnabledSettingsFileKeyName)
                    ?? defaultSettings.IsClickyCursorEnabled,
                HasCompletedOnboarding: TryReadBooleanSetting(userSettingsObject, HasCompletedOnboardingSettingsFileKeyName)
                    ?? defaultSettings.HasCompletedOnboarding,
                HasSubmittedEmail: TryReadBooleanSetting(userSettingsObject, HasSubmittedEmailSettingsFileKeyName)
                    ?? defaultSettings.HasSubmittedEmail,
                PushToTalkShortcut: TryReadShortcutSetting(userSettingsObject, PushToTalkShortcutSettingsFileKeyName)
                    ?? defaultSettings.PushToTalkShortcut,
                AnalyticsDistinctId: TryReadStringSetting(userSettingsObject, AnalyticsDistinctIdSettingsFileKeyName)
                    ?? defaultSettings.AnalyticsDistinctId),
            UserSettingsFileWasCorrupt: false,
            UnrecognizedUserSettingsValues: unrecognizedUserSettingsValues);
    }

    private static CompanionSettingsMergeResult CorruptFileResult() => new(
        CompanionSettings.Defaults,
        UserSettingsFileWasCorrupt: true,
        // A file that could not be parsed cannot be trusted for anything, so nothing is preserved
        // from it; ONB-04 says the next settings change rewrites it as a valid file.
        UnrecognizedUserSettingsValues: new Dictionary<string, JsonNode?>());

    /// <summary>
    /// Reads a string setting. Returns null when the key is absent, JSON null, or holds another JSON
    /// type, because each of those means "no usable value was written" and the default applies.
    /// TryGetValue is used instead of a cast because casts throw on a JSON type mismatch.
    /// </summary>
    private static string? TryReadStringSetting(JsonObject userSettingsObject, string settingsFileKeyName) =>
        userSettingsObject[settingsFileKeyName] is JsonValue stringValueNode
            && stringValueNode.TryGetValue(out string? stringValue)
                ? stringValue
                : null;

    /// <summary>Reads a boolean setting; null means absent, JSON null or a non-boolean value, so the default applies.</summary>
    private static bool? TryReadBooleanSetting(JsonObject userSettingsObject, string settingsFileKeyName) =>
        userSettingsObject[settingsFileKeyName] is JsonValue booleanValueNode
            && booleanValueNode.TryGetValue(out bool booleanValue)
                ? booleanValue
                : null;

    /// <summary>
    /// Reads the shortcut option by its persisted name (e.g. "controlOption"), case-insensitively.
    /// Only the four option names count: a number such as "2" would parse via Enum.TryParse but is
    /// not a shortcut name, so values without any letter are rejected and the default applies.
    /// </summary>
    private static PushToTalkShortcutOption? TryReadShortcutSetting(JsonObject userSettingsObject, string settingsFileKeyName)
    {
        string? shortcutNameInFile = TryReadStringSetting(userSettingsObject, settingsFileKeyName);
        if (shortcutNameInFile is null || !shortcutNameInFile.Any(char.IsLetter))
        {
            return null;
        }
        return Enum.TryParse(shortcutNameInFile, ignoreCase: true, out PushToTalkShortcutOption parsedShortcutOption)
            ? parsedShortcutOption
            : null;
    }
}
