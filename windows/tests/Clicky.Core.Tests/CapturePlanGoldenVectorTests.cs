using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Core.Tests;

/// <summary>Parity spec CLA-01: the order, size and label of every screenshot sent to Claude (T-024).</summary>
public sealed class CapturePlanGoldenVectorTests
{
    private const string SeamName = "capture-plan";

    public static TheoryData<string> CaseIds => GoldenVectors.CaseIds(SeamName);

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void MatchesGoldenVector(string caseId)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);
        List<CaptureMonitor> enumeratedMonitors = vectorCase["input"]!["monitors"]!.AsArray()
            .Select(monitor => new CaptureMonitor(
                new PixelSize((int)monitor!["width"]!, (int)monitor["height"]!),
                (bool)monitor["isCursorMonitor"]!))
            .ToList();
        List<PlannedCapture> expectedCaptures = vectorCase["expected"]!["captures"]!.AsArray()
            .Select(capture => new PlannedCapture(
                (int)capture!["monitorIndex"]!,
                new PixelSize((int)capture["width"]!, (int)capture["height"]!),
                (string)capture["label"]!))
            .ToList();

        Assert.Equal(expectedCaptures, ScreenCapturePlanning.PlanCaptures(enumeratedMonitors));
    }

    [Fact]
    public void AcceptanceCasesRemainInTheVector()
    {
        // T-024 AC1: cursor's monitor first, the ' — ' labels with the dimension suffix, and 1512x982 → 1280x831.
        JsonObject truncatedCase = GoldenVectors.LoadCase(SeamName, "cursor-on-second-truncated-1512x982");
        JsonArray truncatedCaptures = truncatedCase["expected"]!["captures"]!.AsArray();

        Assert.Equal(1, (int)truncatedCaptures[0]!["monitorIndex"]!);
        Assert.Equal(831, (int)truncatedCaptures[0]!["height"]!);
        Assert.Equal(
            "screen 1 of 2 — cursor is on this screen (primary focus) (image dimensions: 1280x831 pixels)",
            (string?)truncatedCaptures[0]!["label"]);
    }
}
