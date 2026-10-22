using System.Globalization;

namespace Clicky.Windows;

internal sealed class CommandLineUsageException(string message) : Exception(message);

/// <summary>
/// Command line for the tracer bullet. Live mode waits for the shortcut; replay mode runs one interaction
/// against recorded responses and exits (ADR 001 tracer item 4). Replay takes either a single /chat fixture
/// (--replay) or a manifest mapping every Worker route (--replay-manifest, WS-04 part 1).
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
    bool DebugCrashRequested)
{
    public const string UsageText =
        "usage: Clicky.exe [--replay <fixture.sse> | --replay-manifest <manifest.json>] [--event-delay-ms N] [--linger-ms N]\n" +
        "                  [--record <fixture.sse>] [--report <report.json>] [--save-captures <directory>]\n" +
        "                  [--debug-stall-hook-once] [--debug-crash]";

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
                case "--record": recordFixturePath = Path.GetFullPath(NextValue()); break;
                case "--report": reportPath = Path.GetFullPath(NextValue()); break;
                case "--save-captures": saveCapturesDirectory = Path.GetFullPath(NextValue()); break;
                case "--linger-ms": lingerMilliseconds = ParseNonNegativeMilliseconds(argumentName, NextValue()); break;
                case "--debug-stall-hook-once": stallFirstHookCallback = true; break;
                case "--debug-crash": debugCrashRequested = true; break;
                default: throw new CommandLineUsageException($"Unknown argument '{argumentName}'.");
            }
        }

        bool hasAnyReplayMode = replayFixturePath is not null || replayManifestPath is not null;
        if (!hasAnyReplayMode && (replayEventDelayMilliseconds is not null || lingerMilliseconds is not null))
        {
            throw new CommandLineUsageException("--event-delay-ms and --linger-ms only apply with --replay or --replay-manifest.");
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

        return new ClickyCommandLineOptions(
            replayFixturePath,
            TimeSpan.FromMilliseconds(replayEventDelayMilliseconds ?? 0),
            replayManifestPath,
            recordFixturePath,
            reportPath ?? Path.Combine(Path.GetTempPath(), "clicky-report.json"),
            saveCapturesDirectory,
            TimeSpan.FromMilliseconds(lingerMilliseconds ?? 0),
            stallFirstHookCallback,
            debugCrashRequested);
    }

    private static int ParseNonNegativeMilliseconds(string argumentName, string argumentValue) =>
        int.TryParse(argumentValue, NumberStyles.None, CultureInfo.InvariantCulture, out int milliseconds)
            ? milliseconds
            : throw new CommandLineUsageException($"{argumentName} needs a whole number of milliseconds, got '{argumentValue}'.");
}
