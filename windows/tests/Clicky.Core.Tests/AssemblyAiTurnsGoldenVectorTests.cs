using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class AssemblyAiTurnsGoldenVectorTests
{
    private const string SeamName = "assemblyai-turns";

    public static TheoryData<string> CaseIds => GoldenVectors.CaseIds(SeamName);

    public static TheoryData<string> TokenResponseErrorCaseIds => TokenResponseCaseIds(expectedKey: "errorMessage");

    public static TheoryData<string> TokenResponseSuccessCaseIds => TokenResponseCaseIds(expectedKey: "token");

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void ComposedTranscriptMatchesGoldenVector(string caseId)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);
        JsonNode expected = vectorCase["expected"]!;

        var turnTranscriptAssembler = new AssemblyAiTurnTranscriptAssembler();
        var transcriptAfterEachMessage = new List<string>();
        foreach (JsonNode? turnMessageNode in vectorCase["input"]!["messages"]!.AsArray())
        {
            var turnMessage = new AssemblyAiTurnMessage(
                Transcript: (string?)turnMessageNode!["transcript"],
                TurnOrder: (int?)turnMessageNode["turn_order"],
                EndOfTurn: (bool?)turnMessageNode["end_of_turn"],
                TurnIsFormatted: (bool?)turnMessageNode["turn_is_formatted"]);
            transcriptAfterEachMessage.Add(turnTranscriptAssembler.ApplyTurnMessage(turnMessage));
        }

        Assert.Equal(
            expected["transcriptAfterEachMessage"]!.AsArray().Select(transcriptNode => (string)transcriptNode!),
            transcriptAfterEachMessage);
        Assert.Equal((string)expected["fullTranscript"]!, turnTranscriptAssembler.ComposeFullTranscript());
    }

    [Theory]
    [MemberData(nameof(TokenResponseErrorCaseIds))]
    public void TokenResponseErrors(string caseId)
    {
        JsonObject tokenResponseCase = LoadTokenResponseCase(caseId);
        JsonNode input = tokenResponseCase["input"]!;

        var tokenResponseException = Assert.Throws<AssemblyAiTranscriptionException>(() =>
            AssemblyAiTemporaryTokenResponseParser.ParseTemporaryToken(
                (int)input["httpStatusCode"]!, (string)input["responseBody"]!));

        Assert.Equal((string)tokenResponseCase["expected"]!["errorMessage"]!, tokenResponseException.Message);
    }

    [Theory]
    [MemberData(nameof(TokenResponseSuccessCaseIds))]
    public void TokenResponseReturnsToken(string caseId)
    {
        JsonObject tokenResponseCase = LoadTokenResponseCase(caseId);
        JsonNode input = tokenResponseCase["input"]!;

        string temporaryToken = AssemblyAiTemporaryTokenResponseParser.ParseTemporaryToken(
            (int)input["httpStatusCode"]!, (string)input["responseBody"]!);

        Assert.Equal((string)tokenResponseCase["expected"]!["token"]!, temporaryToken);
    }

    private static IEnumerable<JsonObject> LoadTokenResponseCases() =>
        GoldenVectors.LoadVectorDocument(SeamName)["tokenResponseCases"]!.AsArray()
            .Select(tokenResponseCase => tokenResponseCase!.AsObject());

    private static JsonObject LoadTokenResponseCase(string caseId) =>
        LoadTokenResponseCases().Single(tokenResponseCase => (string?)tokenResponseCase["id"] == caseId);

    private static TheoryData<string> TokenResponseCaseIds(string expectedKey)
    {
        var tokenResponseCaseIds = new TheoryData<string>();
        foreach (JsonObject tokenResponseCase in LoadTokenResponseCases()
            .Where(tokenResponseCase => tokenResponseCase["expected"]!.AsObject().ContainsKey(expectedKey)))
        {
            tokenResponseCaseIds.Add((string)tokenResponseCase["id"]!);
        }
        return tokenResponseCaseIds;
    }
}
