using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class ScreenMappingGoldenVectorTests
{
    private const string SeamName = "screen-mapping";

    public static TheoryData<string> CaseIds => GoldenVectors.CaseIds(SeamName);

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void MatchesGoldenVector(string caseId)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);
        JsonNode input = vectorCase["input"]!;
        JsonNode expected = vectorCase["expected"]!;

        switch ((string)input["operation"]!)
        {
            case "selectTargetScreen":
                int selectedScreenIndex = ScreenshotToScreenMapper.SelectTargetScreenIndex(
                    (long?)input["screenNumber"],
                    (int)input["captureCount"]!,
                    (int)input["cursorScreenIndex"]!);
                Assert.Equal((int)expected["screenIndex"]!, selectedScreenIndex);
                break;

            case "mapPoint":
                JsonNode monitorRect = input["monitorRectPx"]!;
                VirtualScreenPoint mappedPoint = ScreenshotToScreenMapper.MapToVirtualScreen(
                    new ScreenshotPoint((double)input["point"]!["x"]!, (double)input["point"]!["y"]!),
                    ReadPixelSize(input["screenshotSize"]!),
                    new PhysicalPixelRect(
                        (int)monitorRect["left"]!,
                        (int)monitorRect["top"]!,
                        (int)monitorRect["width"]!,
                        (int)monitorRect["height"]!));
                Assert.Equal((double)expected["x"]!, mappedPoint.X, precision: 9);
                Assert.Equal((double)expected["y"]!, mappedPoint.Y, precision: 9);
                break;

            case "computeScreenshotSize":
                PixelSize monitorSize = ReadPixelSize(input["monitorSizePx"]!);
                Assert.Equal(
                    ReadPixelSize(expected),
                    ScreenCapturePlanning.ComputeScreenshotSize(monitorSize.Width, monitorSize.Height));
                break;

            default:
                Assert.Fail($"Unknown operation in case {caseId}");
                break;
        }
    }

    private static PixelSize ReadPixelSize(JsonNode sizeNode) => new((int)sizeNode["width"]!, (int)sizeNode["height"]!);
}
