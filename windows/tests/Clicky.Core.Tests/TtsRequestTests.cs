using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Core.Tests;

/// <summary>Parity TTS-01 and TTS-03 against shared/fixtures/parity/tts-*.json.</summary>
public sealed class TtsRequestTests
{
    [Fact]
    public void BodyMatchesElevenLabsContract()
    {
        JsonObject requestBody = TtsRequest.BuildRequestBodyOrNullWhenBlank("look at the save button")!;

        Assert.Equal("look at the save button", (string)requestBody["text"]!);
        Assert.Equal("eleven_flash_v2_5", (string)requestBody["model_id"]!);
        Assert.Equal(0.5, (double)requestBody["voice_settings"]!["stability"]!);
        Assert.Equal(0.75, (double)requestBody["voice_settings"]!["similarity_boost"]!);
        Assert.Equal(3, requestBody.Count);
        Assert.Equal(2, requestBody["voice_settings"]!.AsObject().Count);
        Assert.Equal("audio/mpeg", TtsRequest.AcceptHeaderValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void BlankMakesNoCall(string blankSpokenText)
    {
        Assert.Null(TtsRequest.BuildRequestBodyOrNullWhenBlank(blankSpokenText));
    }

    [Fact]
    public void NonSuccessMapsToFallback()
    {
        const string expectedFallbackUtterance =
            "I'm all out of credits. Please DM Farza and tell him to bring me back to life.";

        Assert.Equal(expectedFallbackUtterance, TtsRequest.FallbackUtteranceForHttpStatus(ReadFixtureStatus("tts-http-402.json")));
        Assert.Null(TtsRequest.FallbackUtteranceForHttpStatus(ReadFixtureStatus("tts-ok.json")));

        // Catches drift if the Swift literal at CompanionManager.swift:762 changes.
        string companionManagerSource = File.ReadAllText(
            Path.Combine(GoldenVectors.RepositoryRootDirectory, "leanring-buddy", "CompanionManager.swift"));
        Assert.Contains($"let utterance = \"{expectedFallbackUtterance}\"", companionManagerSource, StringComparison.Ordinal);
    }

    private static int ReadFixtureStatus(string fixtureFileName)
    {
        string fixtureFilePath = Path.Combine(
            GoldenVectors.RepositoryRootDirectory, "shared", "fixtures", "parity", fixtureFileName);
        return (int)JsonNode.Parse(File.ReadAllText(fixtureFilePath))!["status"]!;
    }
}
