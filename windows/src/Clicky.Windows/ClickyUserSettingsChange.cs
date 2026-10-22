using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// One --set-setting change: a settings-file key and the text value to persist for it. The panel with
/// the model picker and toggles lands in WS-18/19; until then this is its tracer stand-in (the same
/// role --debug-crash plays for crash reporting), and it is how tests and QA perform "the app started
/// and one setting changed" against the real persistence path. Parsing is strict: an unknown key or a
/// value of the wrong shape is a usage error, never a silently ignored write.
/// </summary>
internal sealed record ClickyUserSettingsChange(string SettingsFileKeyName, string SettingsValueText)
{
    /// <summary>
    /// Parses "--set-setting &lt;settingsFileKey&gt;=&lt;value&gt;" text. Validation runs by applying the change to
    /// the defaults, so a bad key or value fails right here — inside startup's usage-error handling —
    /// instead of mid-run, and there is exactly one conversion implementation.
    /// </summary>
    public static ClickyUserSettingsChange Parse(string argumentText)
    {
        string[] argumentParts = argumentText.Split('=', count: 2);
        if (argumentParts.Length != 2 || string.IsNullOrWhiteSpace(argumentParts[0]) || string.IsNullOrWhiteSpace(argumentParts[1]))
        {
            throw new CommandLineUsageException($"--set-setting needs <settingsFileKey>=<value>, got '{argumentText}'.");
        }
        var parsedChange = new ClickyUserSettingsChange(argumentParts[0].Trim(), argumentParts[1].Trim());
        parsedChange.ApplyToCompanionSettings(CompanionSettings.Defaults);
        return parsedChange;
    }

    /// <summary>
    /// Returns the settings with this one change applied. Throws <see cref="CommandLineUsageException"/>
    /// for a key this model does not know or a value of the wrong shape (bool keys need true/false; the
    /// shortcut needs one of the four option names, matching the merger's reading rules).
    /// </summary>
    public CompanionSettings ApplyToCompanionSettings(CompanionSettings currentSettings) => SettingsFileKeyName switch
    {
        CompanionSettingsMerger.SelectedClaudeModelSettingsFileKeyName when !string.IsNullOrWhiteSpace(SettingsValueText)
            => currentSettings with { SelectedClaudeModel = SettingsValueText },
        CompanionSettingsMerger.IsClickyCursorEnabledSettingsFileKeyName when bool.TryParse(SettingsValueText, out bool parsedCursorEnabled)
            => currentSettings with { IsClickyCursorEnabled = parsedCursorEnabled },
        CompanionSettingsMerger.HasCompletedOnboardingSettingsFileKeyName when bool.TryParse(SettingsValueText, out bool parsedCompletedOnboarding)
            => currentSettings with { HasCompletedOnboarding = parsedCompletedOnboarding },
        CompanionSettingsMerger.HasSubmittedEmailSettingsFileKeyName when bool.TryParse(SettingsValueText, out bool parsedSubmittedEmail)
            => currentSettings with { HasSubmittedEmail = parsedSubmittedEmail },
        // The letter requirement is the merger's rule (TryReadShortcutSetting): a digit-only value such
        // as "2" would parse as an enum number and is not a shortcut name.
        CompanionSettingsMerger.PushToTalkShortcutSettingsFileKeyName
            when SettingsValueText.Any(char.IsLetter)
                && Enum.TryParse(SettingsValueText, ignoreCase: true, out PushToTalkShortcutOption parsedShortcutOption)
            => currentSettings with { PushToTalkShortcut = parsedShortcutOption },
        CompanionSettingsMerger.AnalyticsDistinctIdSettingsFileKeyName
            => currentSettings with { AnalyticsDistinctId = SettingsValueText },
        _ => throw new CommandLineUsageException(BuildInvalidChangeMessage()),
    };

    private string BuildInvalidChangeMessage() =>
        $"--set-setting cannot apply '{SettingsFileKeyName}={SettingsValueText}'. Keys and values: " +
        "selectedClaudeModel=<model id>, isClickyCursorEnabled=true|false, hasCompletedOnboarding=true|false, " +
        "hasSubmittedEmail=true|false, pushToTalkShortcut=controlOption|shiftControl|controlOptionSpace|shiftControlSpace, " +
        "analyticsDistinctId=<id>.";
}
