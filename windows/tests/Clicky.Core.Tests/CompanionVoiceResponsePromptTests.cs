using System.Text;
using Xunit;

namespace Clicky.Core.Tests;

/// <summary>
/// shared/prompts/companion-voice-response.txt and shared/prompts/onboarding-demo.txt are verbatim copies
/// of the Swift literals at CompanionManager.swift:544-577 and :950-962 until the Mac app loads the files
/// itself (seam 10, T-009). This catches drift on either side.
/// </summary>
public sealed class CompanionVoiceResponsePromptTests
{
    [Fact]
    public void SharedPromptFileMatchesSwiftLiteral()
    {
        string sharedPromptText = File.ReadAllText(
            Path.Combine(GoldenVectors.RepositoryRootDirectory, "shared", "prompts", "companion-voice-response.txt"));

        Assert.Equal(ReadSwiftPromptLiteral("companionVoiceResponseSystemPrompt"), sharedPromptText);
    }

    [Fact]
    public void OnboardingDemoPromptFileByteEqualsSwiftLiteral()
    {
        // Bytes, not text: a BOM or CRLF in the shared file would reach the request body unchanged (ONB-02).
        byte[] sharedPromptBytes = File.ReadAllBytes(
            Path.Combine(GoldenVectors.RepositoryRootDirectory, "shared", "prompts", "onboarding-demo.txt"));

        Assert.Equal(Encoding.UTF8.GetBytes(ReadSwiftPromptLiteral("onboardingDemoSystemPrompt")), sharedPromptBytes);
    }

    private static string ReadSwiftPromptLiteral(string literalName)
    {
        string[] swiftSourceLines = File.ReadAllLines(
            Path.Combine(GoldenVectors.RepositoryRootDirectory, "leanring-buddy", "CompanionManager.swift"));
        int openingLineIndex = Array.FindIndex(
            swiftSourceLines,
            sourceLine => sourceLine.Contains($"{literalName} = \"\"\"", StringComparison.Ordinal));
        Assert.True(openingLineIndex >= 0, $"Swift prompt literal {literalName} not found");
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
        return string.Join("\n", literalLines);
    }
}
