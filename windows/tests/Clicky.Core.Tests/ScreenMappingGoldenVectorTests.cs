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

            case "placeOverlay":
                OverlayPlacementResult placement = OverlayPlacement.Place(
                    ReadVirtualScreenPoint(input["targetPx"]!),
                    ReadPhysicalPixelRect(input["workAreaPx"]!),
                    (double)input["dpiScale"]!,
                    new DipSize(Width: (double)input["bubbleSizeDip"]!["width"]!, Height: (double)input["bubbleSizeDip"]!["height"]!));
                Assert.Equal(ReadVirtualScreenPoint(expected["hotspotPx"]!), placement.HotspotPx);
                Assert.Equal(ReadVirtualScreenRect(expected["windowRectPx"]!), placement.WindowRectPx);
                Assert.Equal(ReadVirtualScreenRect(expected["bubbleRectPx"]!), placement.BubbleRectPx);
                Assert.Equal(ReadBubbleHorizontalSide(expected["bubbleHorizontalSide"]!), placement.BubbleHorizontalSide);
                Assert.Equal(ReadBubbleVerticalSide(expected["bubbleVerticalSide"]!), placement.BubbleVerticalSide);
                break;

            default:
                Assert.Fail($"Unknown operation in case {caseId}");
                break;
        }
    }

    [Fact]
    public void CursorFallbackCasesRemainInTheVector()
    {
        // Task T-003 requires the three PNT-02 cursor-fallback cases to keep running (and staying
        // green via MatchesGoldenVector above) beside the new placement code, so a future edit to
        // this seam file must not be able to drop them silently.
        string[] cursorFallbackCaseIds =
        [
            "screen0-falls-back",
            "screen3-of-2-falls-back",
            "missing-screen-uses-cursor",
        ];
        HashSet<string> presentCaseIds = [.. GoldenVectors.LoadAllCases(SeamName).Select(vectorCase => (string)vectorCase["id"]!)];
        foreach (string cursorFallbackCaseId in cursorFallbackCaseIds)
        {
            Assert.Contains(cursorFallbackCaseId, presentCaseIds);
        }
    }

    private static PixelSize ReadPixelSize(JsonNode sizeNode) => new((int)sizeNode["width"]!, (int)sizeNode["height"]!);

    private static PhysicalPixelRect ReadPhysicalPixelRect(JsonNode rectNode) =>
        new((int)rectNode["left"]!, (int)rectNode["top"]!, (int)rectNode["width"]!, (int)rectNode["height"]!);

    private static VirtualScreenPoint ReadVirtualScreenPoint(JsonNode pointNode) =>
        new((double)pointNode["x"]!, (double)pointNode["y"]!);

    private static VirtualScreenRect ReadVirtualScreenRect(JsonNode rectNode) =>
        new((double)rectNode["left"]!, (double)rectNode["top"]!, (double)rectNode["width"]!, (double)rectNode["height"]!);

    private static OverlayBubbleHorizontalSide ReadBubbleHorizontalSide(JsonNode sideNode) => sideNode.GetValue<string>() switch
    {
        "right" => OverlayBubbleHorizontalSide.RightOfHotspot,
        "left" => OverlayBubbleHorizontalSide.LeftOfHotspot,
        _ => throw new InvalidOperationException($"Unknown bubble horizontal side '{sideNode.ToJsonString()}'"),
    };

    private static OverlayBubbleVerticalSide ReadBubbleVerticalSide(JsonNode sideNode) => sideNode.GetValue<string>() switch
    {
        "below" => OverlayBubbleVerticalSide.BelowHotspot,
        "above" => OverlayBubbleVerticalSide.AboveHotspot,
        _ => throw new InvalidOperationException($"Unknown bubble vertical side '{sideNode.ToJsonString()}'"),
    };
}
