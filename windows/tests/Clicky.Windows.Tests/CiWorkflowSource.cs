using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// Reads job and step blocks out of .github/workflows/ci.yml (the windows-build job by default). This is a
/// purpose-built line reader for this file's layout (job keys at two spaces of indentation,
/// step list items at six spaces), not a general YAML parser. The packaging tests use it to
/// pin the workflow's shape, so reformatting ci.yml surfaces as a test failure rather than
/// a silent pass.
/// </summary>
internal static class CiWorkflowSource
{
    private const string WindowsBuildJobName = "windows-build";
    private const string StepListItemPrefix = "      - ";

    /// <summary>Returns the full text (name line, comments, env and run block) of one windows-build step.</summary>
    public static string WindowsBuildStep(string stepName) => JobStep(WindowsBuildJobName, stepName);

    /// <summary>Returns the full text of one step of the given job.</summary>
    public static string JobStep(string jobName, string stepName)
    {
        List<string> stepBlocks = ReadStepBlocks(jobName);

        string? matchedStepBlock = stepBlocks.FirstOrDefault(stepBlock =>
            FirstLineOf(stepBlock) == $"{StepListItemPrefix}name: {stepName}");
        Assert.True(matchedStepBlock is not null, $"The {jobName} job in ci.yml has no step named '{stepName}'.");
        return matchedStepBlock!;
    }

    /// <summary>Returns the job's header (if, needs, permissions, comments...) up to its step list.</summary>
    public static string JobHeader(string jobName)
    {
        string[] workflowLines = ReadWorkflowLines();
        int jobStartLineIndex = FindJobStartLineIndex(workflowLines, jobName);
        int stepsLineIndex = Array.FindIndex(workflowLines, jobStartLineIndex, line => line == "    steps:");
        Assert.True(stepsLineIndex > jobStartLineIndex, $"The {jobName} job in ci.yml has no steps list.");
        return JoinLines(workflowLines, jobStartLineIndex, stepsLineIndex);
    }

    /// <summary>The top-level 'on:' block, which decides which pushes and tags start a run.</summary>
    public static string TriggerBlock()
    {
        string[] workflowLines = ReadWorkflowLines();
        int onLineIndex = Array.IndexOf(workflowLines, "on:");
        Assert.True(onLineIndex >= 0, "ci.yml has no top-level 'on:' block.");
        int nextTopLevelLineIndex = Array.FindIndex(workflowLines, onLineIndex + 1,
            line => line.Length > 0 && !char.IsWhiteSpace(line[0]));
        return JoinLines(workflowLines, onLineIndex, nextTopLevelLineIndex < 0 ? workflowLines.Length : nextTopLevelLineIndex);
    }

    /// <summary>
    /// The zero-based position of one windows-build step in the job's step list, for order-sensitive
    /// assertions (the installed-replay step, for example, must come after the Package step that
    /// builds the Setup.exe it installs).
    /// </summary>
    public static int WindowsBuildStepIndex(string stepName)
    {
        List<string> stepBlocks = ReadStepBlocks(WindowsBuildJobName);

        int matchedStepBlockIndex = stepBlocks.FindIndex(stepBlock =>
            FirstLineOf(stepBlock) == $"{StepListItemPrefix}name: {stepName}");
        Assert.True(matchedStepBlockIndex >= 0, $"The windows-build job in ci.yml has no step named '{stepName}'.");
        return matchedStepBlockIndex;
    }

    private static string[] ReadWorkflowLines() => File.ReadAllLines(Path.Combine(
        ClickyReplayProcess.RepositoryRootDirectory, ".github", "workflows", "ci.yml"));

    private static int FindJobStartLineIndex(string[] workflowLines, string jobName)
    {
        int jobStartLineIndex = Array.IndexOf(workflowLines, $"  {jobName}:");
        Assert.True(jobStartLineIndex >= 0, $"ci.yml has no '{jobName}' job.");
        return jobStartLineIndex;
    }

    private static List<string> ReadStepBlocks(string jobName)
    {
        string[] workflowLines = ReadWorkflowLines();
        return CollectStepBlocks(workflowLines, FindJobStartLineIndex(workflowLines, jobName));
    }

    private static List<string> CollectStepBlocks(string[] workflowLines, int jobStartLineIndex)
    {
        // A step block runs from its '      - ' list item to the next one, to the next job
        // key, or to the end of the file. Deeper-indented lines (step keys, block scalars,
        // comments) belong to the step item they follow.
        var stepBlocks = new List<string>();
        int currentStepStartLineIndex = -1;
        int jobEndLineIndexExclusive = workflowLines.Length;
        for (int lineIndex = jobStartLineIndex + 1; lineIndex < workflowLines.Length; lineIndex++)
        {
            string line = workflowLines[lineIndex];
            bool isNextJobKeyLine = line.StartsWith("  ") && !line.StartsWith("    ") && line.TrimEnd().EndsWith(':');
            if (isNextJobKeyLine)
            {
                jobEndLineIndexExclusive = lineIndex;
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
            stepBlocks.Add(JoinLines(workflowLines, currentStepStartLineIndex, jobEndLineIndexExclusive));
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
