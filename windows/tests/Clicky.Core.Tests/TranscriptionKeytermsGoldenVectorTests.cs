using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class TranscriptionKeytermsGoldenVectorTests
{
    private const string SeamName = "transcription-keyterms";

    public static TheoryData<string> CaseIds => GoldenVectors.CaseIds(SeamName);

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void KeytermsAndWebsocketUrlMatchGoldenVector(string caseId)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);
        JsonNode input = vectorCase["input"]!;
        JsonNode expected = vectorCase["expected"]!;

        IReadOnlyList<string> transcriptionKeyterms =
            TranscriptionKeyterms.BuildTranscriptionKeyterms(ReadStrings(input["contextualKeyterms"]!));
        string websocketUrl =
            AssemblyAiWebsocketUrlBuilder.BuildWebsocketUrl((string)input["temporaryToken"]!, transcriptionKeyterms);

        Assert.Equal(ReadStrings(expected["keyterms"]!), transcriptionKeyterms);
        Assert.Equal((string)expected["websocketUrl"]!, websocketUrl);
    }

    [Fact]
    public void BaseKeytermsMatchSwiftVerbatim()
    {
        List<string> vectorBaseKeyterms = ReadStrings(GoldenVectors.LoadVectorDocument(SeamName)["baseKeyterms"]!);

        Assert.Equal(11, vectorBaseKeyterms.Count);
        Assert.Equal(vectorBaseKeyterms, TranscriptionKeyterms.BaseKeyterms);
        Assert.Equal(vectorBaseKeyterms, TranscriptionKeyterms.BuildTranscriptionKeyterms([]));
    }

    [Fact]
    public void WebsocketUrlOmitsKeytermsPromptWhenNoKeytermSurvivesTrimming()
    {
        // Swift adds keyterms_prompt only when a non-blank keyterm remains (:454-462).
        const string expectedWebsocketUrl =
            "wss://streaming.assemblyai.com/v3/ws?sample_rate=16000&encoding=pcm_s16le&format_turns=true&speech_model=u3-rt-pro&token=temporary-token-abc123";

        Assert.Equal(expectedWebsocketUrl, AssemblyAiWebsocketUrlBuilder.BuildWebsocketUrl("temporary-token-abc123", []));
        Assert.Equal(expectedWebsocketUrl, AssemblyAiWebsocketUrlBuilder.BuildWebsocketUrl("temporary-token-abc123", ["", "  \n"]));
    }

    private static List<string> ReadStrings(JsonNode arrayNode) =>
        arrayNode.AsArray().Select(stringNode => (string)stringNode!).ToList();
}
