using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class SseStreamGoldenVectorTests
{
    private const string SeamName = "sse-stream";

    public static TheoryData<string> CaseIds => GoldenVectors.CaseIds(SeamName);

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void ParsingAndBubbleTextMatchGoldenVector(string caseId)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);
        JsonNode expected = vectorCase["expected"]!;

        var textDeltas = new List<string>();
        var displayedAfterEachEvent = new List<string>();
        string accumulatedResponseText = "";
        bool endedByDoneMarker = false;

        foreach (JsonNode? lineNode in vectorCase["input"]!["lines"]!.AsArray())
        {
            SseLineParseResult parsedLine = SseLineParser.ParseLine((string)lineNode!);
            if (parsedLine.Kind == SseLineKind.EndOfStream)
            {
                endedByDoneMarker = true;
                break;
            }
            if (parsedLine.Kind == SseLineKind.TextDelta)
            {
                textDeltas.Add(parsedLine.TextDelta!);
                accumulatedResponseText += parsedLine.TextDelta;
                displayedAfterEachEvent.Add(StreamingBubbleText.VisibleTextFor(accumulatedResponseText));
            }
        }

        Assert.Equal((string)expected["text"]!, accumulatedResponseText);
        Assert.Equal(ReadStrings(expected["textDeltas"]!), textDeltas);
        Assert.Equal((bool)expected["endedByDoneMarker"]!, endedByDoneMarker);
        Assert.Equal(ReadStrings(expected["displayedAfterEachEvent"]!), displayedAfterEachEvent);
    }

    private static List<string> ReadStrings(JsonNode arrayNode) =>
        arrayNode.AsArray().Select(stringNode => (string)stringNode!).ToList();
}
