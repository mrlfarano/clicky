using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Core.Tests;

/// <summary>Parity spec PNT-06: the buddy's bezier flight — position, rotation, scale and duration (T-041).</summary>
public sealed class FlightPathGoldenVectorTests
{
    private const string SeamName = "flight-path";

    // Nine decimals absorbs last-bit differences in Atan2/Sin between the Windows and Linux runtimes.
    private const int FloatingPointPrecision = 9;

    public static TheoryData<string> CaseIds => GoldenVectors.CaseIds(SeamName);

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void MatchesGoldenVector(string caseId)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);
        JsonNode input = vectorCase["input"]!;
        JsonNode expected = vectorCase["expected"]!;
        DipPoint startPosition = ReadDipPoint(input["start"]!);
        DipPoint endPosition = ReadDipPoint(input["end"]!);

        switch ((string)input["operation"]!)
        {
            case "sample":
                FlightPathSample flightPathSample = FlightPath.Sample((double)input["linearProgress"]!, startPosition, endPosition);
                Assert.Equal((double)expected["x"]!, flightPathSample.Position.X, FloatingPointPrecision);
                Assert.Equal((double)expected["y"]!, flightPathSample.Position.Y, FloatingPointPrecision);
                Assert.Equal((double)expected["rotationDegrees"]!, flightPathSample.RotationDegrees, FloatingPointPrecision);
                Assert.Equal((double)expected["scale"]!, flightPathSample.Scale, FloatingPointPrecision);
                break;

            case "flightDuration":
                Assert.Equal(
                    (double)expected["seconds"]!,
                    FlightPath.ComputeFlightDurationSeconds(startPosition, endPosition),
                    FloatingPointPrecision);
                break;

            default:
                Assert.Fail($"Unknown operation in case {caseId}");
                break;
        }
    }

    [Fact]
    public void ShortAndLongHopsAreSampledAtFixedProgressValues()
    {
        // T-041 AC1: the vector must keep sampling at least a short and a long hop, so an edit to the
        // seam file cannot silently reduce MatchesGoldenVector to one distance.
        HashSet<string> presentBranches = [.. GoldenVectors.LoadAllCases(SeamName).Select(vectorCase => (string)vectorCase["branch"]!)];
        Assert.Contains("sample-short-hop", presentBranches);
        Assert.Contains("sample-long-hop", presentBranches);
    }

    [Fact]
    public void FlightDurationIsClampedBetweenPoint6AndOnePoint4Seconds()
    {
        // T-041 AC2: a short hop is raised to 0.6 s and a long hop lowered to 1.4 s. The vector pins the
        // exact values; this sweeps distances from zero to far past any monitor to hold the bounds everywhere.
        DipPoint startPosition = new(0, 0);
        foreach (double distance in new[] { 0.0, 1.0, 300.0, 479.9, 480.0, 800.0, 1120.0, 1120.1, 1500.0, 100_000.0 })
        {
            double flightDurationSeconds = FlightPath.ComputeFlightDurationSeconds(startPosition, new DipPoint(distance, 0));
            Assert.InRange(flightDurationSeconds, 0.6, 1.4);
        }

        Assert.Equal(0.6, FlightPath.ComputeFlightDurationSeconds(startPosition, new DipPoint(300, 0)));
        Assert.Equal(1.4, FlightPath.ComputeFlightDurationSeconds(startPosition, new DipPoint(1500, 0)));
    }

    private static DipPoint ReadDipPoint(JsonNode pointNode) => new((double)pointNode["x"]!, (double)pointNode["y"]!);
}
