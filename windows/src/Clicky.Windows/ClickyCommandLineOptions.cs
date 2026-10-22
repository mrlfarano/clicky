using System.Globalization;

namespace Clicky.Windows;

internal sealed class CommandLineUsageException(string message) : Exception(message);

/// <summary>
/// Command line for the tracer bullet. Live mode waits for the shortcut; replay mode runs one interaction
/// against recorded responses and exits (ADR 001 tracer item 4). Replay takes either a single /chat fixture
/// (--replay) or a manifest mapping every Worker route (--replay-manifest, WS-04 part 1). The parity-harness
/// flags --settings-override, --script and --debug-trigger-onboarding-demo (WS-04 part 2) shape that same
/// replay: overridden user settings in the report, scripted input events on the virtual clock, and the
/// onboarding demo interaction instead of the tracer prompt. --debug-update-now (WS-10) runs one update
/// check against UpdateFeedUrl, applies what it staged and exits.
/// </summary>
internal sealed record ClickyCommandLineOptions(
    string? ReplayFixturePath,
    TimeSpan ReplayEventDelay,
    string? ReplayManifestPath,
    string? RecordFixturePath,
    string ReportPath,
    string? SaveCapturesDirectory,
    TimeSpan LingerBeforeExit,
    bool StallFirstHookCallback,
    bool DebugCrashRequested,
    IReadOnlyList<SettingsOverridePair> SettingsOverrides,
    string? ScriptPath,
    bool DebugTriggerOnboardingDemo,
    ClickyUserSettingsChange? UserSettingsChange,
    bool DebugUpdateNowRequested,
    TimeSpan? DebugQuitAfter)
{
    public const string UsageText =
        "usage: Clicky.exe [--replay <fixture.sse> | --replay-manifest <manifest.json>] [--event-delay-ms N] [--linger-ms N]\n" +
        "                  [--settings-override key=value,...] [--script <script.json>] [--debug-trigger-onboarding-demo]\n" +
        "                  [--record <fixture.sse>] [--report <report.json>] [--save-captures <directory>]\n" +
        "                  [--set-setting <settingsFileKey>=<value>] [--debug-stall-hook-once] [--debug-crash]\n" +
        "                  [--debug-update-now] [--debug-quit-after-ms N]";

    public bool IsReplay => ReplayFixturePath is not null || ReplayManifestPath is not null;

    public static ClickyCommandLineOptions Parse(IReadOnlyList<string> commandLineArguments)
    {
        string? replayFixturePath = null;
        int? replayEventDelayMilliseconds = null;
        string? replayManifestPath = null;
        string? recordFixturePath = null;
        string? reportPath = null;
        string? saveCapturesDirectory = null;
        int? lingerMilliseconds = null;
        bool stallFirstHookCallback = false;
        bool debugCrashRequested = false;
        List<SettingsOverridePair>? settingsOverrides = null;
        string? scriptPath = null;
        bool debugTriggerOnboardingDemo = false;
        ClickyUserSettingsChange? userSettingsChange = null;
        bool debugUpdateNowRequested = false;
        int? debugQuitAfterMilliseconds = null;

        for (int argumentIndex = 0; argumentIndex < commandLineArguments.Count; argumentIndex++)
        {
            string argumentName = commandLineArguments[argumentIndex];
            string NextValue() => argumentIndex + 1 < commandLineArguments.Count
                ? commandLineArguments[++argumentIndex]
                : throw new CommandLineUsageException($"{argumentName} needs a value.");

            switch (argumentName)
            {
                case "--replay": replayFixturePath = Path.GetFullPath(NextValue()); break;
                case "--replay-manifest": replayManifestPath = Path.GetFullPath(NextValue()); break;
                case "--event-delay-ms": replayEventDelayMilliseconds = ParseNonNegativeMilliseconds(argumentName, NextValue()); break;
                case "--settings-override": (settingsOverrides ??= []).AddRange(ParseSettingsOverridePairs(argumentName, NextValue())); break;
                case "--script": scriptPath = Path.GetFullPath(NextValue()); break;
                case "--debug-trigger-onboarding-demo": debugTriggerOnboardingDemo = true; break;
                case "--record": recordFixturePath = Path.GetFullPath(NextValue()); break;
                case "--report": reportPath = Path.GetFullPath(NextValue()); break;
                case "--save-captures": saveCapturesDirectory = Path.GetFullPath(NextValue()); break;
                case "--linger-ms": lingerMilliseconds = ParseNonNegativeMilliseconds(argumentName, NextValue()); break;
                case "--debug-stall-hook-once": stallFirstHookCallback = true; break;
                case "--debug-crash": debugCrashRequested = true; break;
                case "--set-setting": userSettingsChange = ClickyUserSettingsChange.Parse(NextValue()); break;
                case "--debug-update-now": debugUpdateNowRequested = true; break;
                case "--debug-quit-after-ms": debugQuitAfterMilliseconds = ParseNonNegativeMilliseconds(argumentName, NextValue()); break;
                default: throw new CommandLineUsageException($"Unknown argument '{argumentName}'.");
            }
        }

        bool hasAnyReplayMode = replayFixturePath is not null || replayManifestPath is not null;
        if (!hasAnyReplayMode && (replayEventDelayMilliseconds is not null || lingerMilliseconds is not null))
        {
            throw new CommandLineUsageException("--event-delay-ms and --linger-ms only apply with --replay or --replay-manifest.");
        }
        // The harness flags drive a deterministic replay run: --script stages input events on the virtual
        // clock (live input comes from the real keyboard hook instead), and --settings-override exists so
        // parity scenarios can pin settings without touching the user's real settings file.
        if (!hasAnyReplayMode && (settingsOverrides is not null || scriptPath is not null || debugTriggerOnboardingDemo))
        {
            throw new CommandLineUsageException(
                "--settings-override, --script and --debug-trigger-onboarding-demo only apply with --replay or --replay-manifest.");
        }
        if (replayFixturePath is not null && replayManifestPath is not null)
        {
            throw new CommandLineUsageException("--replay and --replay-manifest are two ways to stage the same run; pass only one.");
        }
        if (hasAnyReplayMode && recordFixturePath is not null)
        {
            throw new CommandLineUsageException("--record tees a live response, so it cannot be combined with --replay or --replay-manifest.");
        }
        if (replayFixturePath is not null && !File.Exists(replayFixturePath))
        {
            throw new CommandLineUsageException($"Replay fixture not found: {replayFixturePath}");
        }
        if (replayManifestPath is not null && !File.Exists(replayManifestPath))
        {
            throw new CommandLineUsageException($"Replay manifest not found: {replayManifestPath}");
        }
        if (scriptPath is not null && !File.Exists(scriptPath))
        {
            throw new CommandLineUsageException($"Input script not found: {scriptPath}");
        }
        if (userSettingsChange is not null && (hasAnyReplayMode || recordFixturePath is not null || debugCrashRequested))
        {
            // --set-setting applies one user setting through the real persistence path and exits; mixing
            // it into a replay or a crash-test run would make both halves' exit codes ambiguous.
            throw new CommandLineUsageException("--set-setting applies one user setting and exits; it cannot be combined with --replay, --replay-manifest, --record or --debug-crash.");
        }
        if (debugUpdateNowRequested && (hasAnyReplayMode || recordFixturePath is not null || debugCrashRequested || userSettingsChange is not null))
        {
            // Like --set-setting, --debug-update-now is its own one-shot run: the apply ends the process.
            throw new CommandLineUsageException("--debug-update-now runs one update check and exits; it cannot be combined with --replay, --replay-manifest, --record, --debug-crash or --set-setting.");
        }
        if (debugQuitAfterMilliseconds is not null && (hasAnyReplayMode || debugCrashRequested || userSettingsChange is not null || debugUpdateNowRequested))
        {
            // --debug-quit-after-ms (T-030) times the live app's ordered shutdown; the other modes end on their own.
            throw new CommandLineUsageException("--debug-quit-after-ms runs the live app and quits; it cannot be combined with --replay, --replay-manifest, --debug-crash, --set-setting or --debug-update-now.");
        }

        return new ClickyCommandLineOptions(
            replayFixturePath,
            TimeSpan.FromMilliseconds(replayEventDelayMilliseconds ?? 0),
            replayManifestPath,
            recordFixturePath,
            reportPath ?? Path.Combine(Path.GetTempPath(), "clicky-report.json"),
            saveCapturesDirectory,
            TimeSpan.FromMilliseconds(lingerMilliseconds ?? 0),
            stallFirstHookCallback,
            debugCrashRequested,
            settingsOverrides ?? [],
            scriptPath,
            debugTriggerOnboardingDemo,
            userSettingsChange,
            debugUpdateNowRequested,
            debugQuitAfterMilliseconds is int quitAfterMilliseconds ? TimeSpan.FromMilliseconds(quitAfterMilliseconds) : null);
    }

    private static int ParseNonNegativeMilliseconds(string argumentName, string argumentValue) =>
        int.TryParse(argumentValue, NumberStyles.None, CultureInfo.InvariantCulture, out int milliseconds)
            ? milliseconds
            : throw new CommandLineUsageException($"{argumentName} needs a whole number of milliseconds, got '{argumentValue}'.");

    /// <summary>
    /// Splits a --settings-override value ("key=value,key=value") into pairs. Only the comma format is
    /// checked here; which keys exist and what values they accept is decided by
    /// <see cref="CompanionSettingsOverrideApplier"/>, which knows the settings model. Values therefore
    /// cannot contain commas.
    /// </summary>
    private static IEnumerable<SettingsOverridePair> ParseSettingsOverridePairs(string argumentName, string settingsOverrideText)
    {
        if (string.IsNullOrWhiteSpace(settingsOverrideText))
        {
            throw new CommandLineUsageException($"{argumentName} needs comma-separated key=value pairs, got an empty value.");
        }
        foreach (string overrideItem in settingsOverrideText.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int separatorIndex = overrideItem.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == overrideItem.Length - 1)
            {
                throw new CommandLineUsageException(
                    $"{argumentName} needs comma-separated key=value pairs, got '{settingsOverrideText}'.");
            }
            yield return new SettingsOverridePair(overrideItem[..separatorIndex], overrideItem[(separatorIndex + 1)..]);
        }
    }
}
