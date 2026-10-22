using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// Reads step blocks out of .github/workflows/ci.yml for the windows-build job. This is a
/// purpose-built line reader for this file's layout (job keys at two spaces of indentation,
/// step list items at six spaces), not a general YAML parser. The packaging tests use it to
/// pin the workflow's shape, so reformatting ci.yml surfaces as a test failure rather than
/// a silent pass.
/// </summary>
internal static class CiWorkflowSource
{
    private const string WindowsBuildJobKeyLine = "  windows-build:";
    private const string StepListItemPrefix = "      - ";

    /// <summary>Returns the full text (name line, comments, env and run block) of one windows-build step.</summary>
    public static string WindowsBuildStep(string stepName)
    {
        List<string> stepBlocks = ReadWindowsBuildStepBlocks();

        string? matchedStepBlock = stepBlocks.FirstOrDefault(stepBlock =>
            FirstLineOf(stepBlock) == $"{StepListItemPrefix}name: {stepName}");
        Assert.True(matchedStepBlock is not null, $"The windows-build job in ci.yml has no step named '{stepName}'.");
        return matchedStepBlock!;
    }

    /// <summary>
    /// The zero-based position of one windows-build step in the job's step list, for order-sensitive
    /// assertions (the installed-replay step, for example, must come after the Package step that
    /// builds the Setup.exe it installs).
    /// </summary>
    public static int WindowsBuildStepIndex(string stepName)
    {
        List<string> stepBlocks = ReadWindowsBuildStepBlocks();

        int matchedStepBlockIndex = stepBlocks.FindIndex(stepBlock =>
            FirstLineOf(stepBlock) == $"{StepListItemPrefix}name: {stepName}");
        Assert.True(matchedStepBlockIndex >= 0, $"The windows-build job in ci.yml has no step named '{stepName}'.");
        return matchedStepBlockIndex;
    }

    private static List<string> ReadWindowsBuildStepBlocks()
    {
        string[] workflowLines = File.ReadAllLines(Path.Combine(
            ClickyReplayProcess.RepositoryRootDirectory, ".github", "workflows", "ci.yml"));

        int windowsBuildJobStartLineIndex = Array.FindIndex(workflowLines, line => line == WindowsBuildJobKeyLine);
        Assert.True(windowsBuildJobStartLineIndex >= 0, "ci.yml has no 'windows-build' job.");

        return CollectWindowsBuildStepBlocks(workflowLines, windowsBuildJobStartLineIndex);
    }

    private static List<string> CollectWindowsBuildStepBlocks(string[] workflowLines, int jobStartLineIndex)
    {
        // A step block runs from its '      - ' list item to the next one, to the next job
        // key, or to the end of the file. Deeper-indented lines (step keys, block scalars,
        // comments) belong to the step item they follow.
        var stepBlocks = new List<string>();
        int currentStepStartLineIndex = -1;
        for (int lineIndex = jobStartLineIndex + 1; lineIndex < workflowLines.Length; lineIndex++)
        {
            string line = workflowLines[lineIndex];
            bool isNextJobKeyLine = line.StartsWith("  ") && !line.StartsWith("    ") && line.TrimEnd().EndsWith(':');
            if (isNextJobKeyLine)
            {
                break;
            }
            if (line.StartsWith(StepListItemPrefix))
            {
                if (currentStepStartLineIndex >= 0)
                {
                    stepBlocks.Add(JoinLines(workflowLines, currentStepStartLineIndex, lineIndex));
                }
                currentStepStartLineIndex = lineIndex;
            }
        }
        if (currentStepStartLineIndex >= 0)
        {
            stepBlocks.Add(JoinLines(workflowLines, currentStepStartLineIndex, workflowLines.Length));
        }
        return stepBlocks;
    }

    private static string JoinLines(string[] workflowLines, int startLineIndex, int endLineIndexExclusive) =>
        string.Join(Environment.NewLine, workflowLines[startLineIndex..endLineIndexExclusive]);

    private static string FirstLineOf(string stepBlock)
    {
        int firstLineBreakIndex = stepBlock.IndexOf(Environment.NewLine);
        return firstLineBreakIndex < 0 ? stepBlock : stepBlock[..firstLineBreakIndex];
    }
}
