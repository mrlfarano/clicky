using System.Text;
using Xunit;

namespace Clicky.Core.Tests;

/// <summary>
/// shared/prompts/companion-voice-response.txt and shared/prompts/onboarding-demo.txt were verbatim
/// copies of Swift string literals until T-009 landed: the Mac app now loads the files themselves as
/// bundle resources (the byte-for-byte bundle-vs-file check runs on the Mac side, in
/// leanring-buddyTests/GoldenVectorParityTests.swift). This side keeps the extraction honest: the
/// files must exist with the exact content shape the request pipeline depends on, and the Swift
/// source must not grow the literals back (which would reintroduce a second source of truth).
/// </summary>
public sealed class CompanionVoiceResponsePromptTests
{
    [Theory]
    [InlineData("companion-voice-response.txt")]
    [InlineData("onboarding-demo.txt")]
    public void SharedPromptFileIsNonEmptyUtf8WithoutBom(string promptFileName)
    {
        byte[] promptBytes = File.ReadAllBytes(
            Path.Combine(GoldenVectors.RepositoryRootDirectory, "shared", "prompts", promptFileName));

        // Bytes reach the Claude request body unchanged (ONB-02): a BOM would be spoken content.
        Assert.NotEmpty(promptBytes);
        Assert.NotEqual((byte)0xEF, promptBytes[0]);
        Assert.Contains((byte)'\n', promptBytes);
        Assert.DoesNotContain("\r\n"u8.ToArray(), promptBytes);
        // Swift multiline literals carry no trailing newline, and the byte-equality era (pre-T-009)
        // established the files match that exactly — keep it that way.
        Assert.NotEqual((byte)'\n', promptBytes[^1]);
    }

    [Theory]
    [InlineData("companionVoiceResponseSystemPrompt")]
    [InlineData("onboardingDemoSystemPrompt")]
    public void SwiftSourceCarriesNoPromptLiteral(string literalName)
    {
        string swiftSource = File.ReadAllText(
            Path.Combine(GoldenVectors.RepositoryRootDirectory, "leanring-buddy", "CompanionManager.swift"));

        // T-009 moved the literals to shared/prompts; the Swift side loads bundle resources instead.
        // A literal coming back means someone reintroduced a prompt copy that can drift from the file.
        Assert.DoesNotContain($"{literalName} = \"\"\"", swiftSource, StringComparison.Ordinal);
        Assert.Contains($"loadRequiredPromptBundleResource", swiftSource, StringComparison.Ordinal);
    }
}
