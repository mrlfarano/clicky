using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// One "key=value" item of a --settings-override flag. The key is a user-settings file key name (the
/// <see cref="CompanionSettingsMerger"/> key set, e.g. "selectedClaudeModel"), and the value is still the
/// raw command-line text; the applier below turns it into a typed value.
/// </summary>
internal sealed record SettingsOverridePair(string SettingsFileKeyName, string RawValue);

/// <summary>
/// Applies --settings-override pairs over the settings the app loaded (WS-04 part 2). Replay scenarios pin
/// user settings this way (parity spec CLA-08, ONB-03) without writing the user's real settings file, and
/// the result lands in the report's effectiveSettings section so a report shows exactly what the run used.
/// Pairs apply in command-line order, so repeating a key keeps only its last value.
/// </summary>
internal static class CompanionSettingsOverrideApplier
{
    public static CompanionSettings Apply(CompanionSettings loadedUserSettings, IReadOnlyList<SettingsOverridePair> settingsOverridePairs)
    {
        CompanionSettings effectiveUserSettings = loadedUserSettings;
        foreach (SettingsOverridePair overridePair in settingsOverridePairs)
        {
            effectiveUserSettings = overridePair.SettingsFileKeyName switch
            {
                CompanionSettingsMerger.SelectedClaudeModelSettingsFileKeyName => effectiveUserSettings with
                {
                    SelectedClaudeModel = RequireNonEmptyValue(overridePair),
                },
                CompanionSettingsMerger.IsClickyCursorEnabledSettingsFileKeyName => effectiveUserSettings with
                {
                    IsClickyCursorEnabled = ParseBooleanValue(overridePair),
                },
                CompanionSettingsMerger.HasCompletedOnboardingSettingsFileKeyName => effectiveUserSettings with
                {
                    HasCompletedOnboarding = ParseBooleanValue(overridePair),
                },
                CompanionSettingsMerger.HasSubmittedEmailSettingsFileKeyName => effectiveUserSettings with
                {
                    HasSubmittedEmail = ParseBooleanValue(overridePair),
                },
                CompanionSettingsMerger.PushToTalkShortcutSettingsFileKeyName => effectiveUserSettings with
                {
                    PushToTalkShortcut = ParseShortcutValue(overridePair),
                },
                CompanionSettingsMerger.AnalyticsDistinctIdSettingsFileKeyName => effectiveUserSettings with
                {
                    AnalyticsDistinctId = RequireNonEmptyValue(overridePair),
                },
                _ => throw new CommandLineUsageException(
                    $"Unknown settings key '{overridePair.SettingsFileKeyName}'; known keys: " +
                    $"{string.Join(", ", CompanionSettingsMerger.KnownSettingsFileKeyNames)}."),
            };
        }
        return effectiveUserSettings;
    }

    /// <summary>An override exists to set a value, so an empty one is a typo, not a clearing operation.</summary>
    private static string RequireNonEmptyValue(SettingsOverridePair overridePair) =>
        !string.IsNullOrWhiteSpace(overridePair.RawValue)
            ? overridePair.RawValue
            : throw new CommandLineUsageException($"Settings key '{overridePair.SettingsFileKeyName}' needs a non-empty value.");

    private static bool ParseBooleanValue(SettingsOverridePair overridePair) =>
        bool.TryParse(overridePair.RawValue, out bool parsedBooleanValue)
            ? parsedBooleanValue
            : throw new CommandLineUsageException(
                $"Settings key '{overridePair.SettingsFileKeyName}' needs 'true' or 'false', got '{overridePair.RawValue}'.");

    /// <summary>
    /// Accepts the four shortcut names ("controlOption", ...) case-insensitively, with the same letters-only
    /// guard as <see cref="CompanionSettingsMerger"/>: a digit-only value must not slip through as an enum
    /// position, it is not a shortcut name.
    /// </summary>
    private static PushToTalkShortcutOption ParseShortcutValue(SettingsOverridePair overridePair)
    {
        if (!overridePair.RawValue.Any(char.IsLetter)
            || !Enum.TryParse(overridePair.RawValue, ignoreCase: true, out PushToTalkShortcutOption parsedShortcutOption))
        {
            throw new CommandLineUsageException(
                $"Settings key '{overridePair.SettingsFileKeyName}' needs one of " +
                $"{string.Join(", ", Enum.GetNames<PushToTalkShortcutOption>().Select(name => name.ToLowerInvariant()))}, " +
                $"got '{overridePair.RawValue}'.");
        }
        return parsedShortcutOption;
    }
}
