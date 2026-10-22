using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class PointTagGoldenVectorTests
{
    private const string SeamName = "point-tag";

    public static TheoryData<string> CaseIds => GoldenVectors.CaseIds(SeamName);

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void ParseMatchesGoldenVector(string caseId)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);
        JsonNode expected = vectorCase["expected"]!;

        PointTagParseResult parseResult = PointTag.Parse((string)vectorCase["input"]!["text"]!);

        Assert.Equal((string?)expected["spokenText"], parseResult.SpokenText);
        Assert.Equal((double?)expected["x"], parseResult.Coordinate?.X);
        Assert.Equal((double?)expected["y"], parseResult.Coordinate?.Y);
        Assert.Equal((string?)expected["label"], parseResult.ElementLabel);
        Assert.Equal((long?)expected["screen"], parseResult.ScreenNumber);
    }
}
