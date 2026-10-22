using Xunit;

namespace Clicky.Core.Tests;

/// <summary>
/// shared/prompts/companion-voice-response.txt is a verbatim copy of the Swift literal at
/// CompanionManager.swift:544-577 until the Mac app loads the file itself (seam 10). This catches drift
/// on either side.
/// </summary>
public sealed class CompanionVoiceResponsePromptTests
{
    [Fact]
    public void SharedPromptFileMatchesSwiftLiteral()
    {
        string[] swiftSourceLines = File.ReadAllLines(
            Path.Combine(GoldenVectors.RepositoryRootDirectory, "leanring-buddy", "CompanionManager.swift"));
        int openingLineIndex = Array.FindIndex(
            swiftSourceLines,
            sourceLine => sourceLine.Contains("companionVoiceResponseSystemPrompt = \"\"\"", StringComparison.Ordinal));
        Assert.True(openingLineIndex >= 0, "Swift prompt literal not found");
        int closingLineIndex = Array.FindIndex(
            swiftSourceLines,
            openingLineIndex + 1,
            sourceLine => sourceLine.Trim() == "\"\"\"");

        // A Swift multi-line string drops the closing delimiter's indentation from every line.
        string closingDelimiterIndentation = swiftSourceLines[closingLineIndex][..swiftSourceLines[closingLineIndex].IndexOf('"')];
        IEnumerable<string> literalLines = swiftSourceLines[(openingLineIndex + 1)..closingLineIndex]
            .Select(literalLine => literalLine.StartsWith(closingDelimiterIndentation, StringComparison.Ordinal)
                ? literalLine[closingDelimiterIndentation.Length..]
                : literalLine);
        string swiftPromptText = string.Join("\n", literalLines);

        string sharedPromptText = File.ReadAllText(
            Path.Combine(GoldenVectors.RepositoryRootDirectory, "shared", "prompts", "companion-voice-response.txt"));

        Assert.Equal(swiftPromptText, sharedPromptText);
    }
}
