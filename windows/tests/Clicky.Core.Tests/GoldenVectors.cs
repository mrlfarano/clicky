using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Core.Tests;

/// <summary>Loads shared/golden/&lt;seam&gt;.json (format: docs/02-parity-spec.md, "Golden vector file format").</summary>
internal static class GoldenVectors
{
    public static string RepositoryRootDirectory { get; } = typeof(GoldenVectors).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(metadataAttribute => metadataAttribute.Key == "RepositoryRootDirectory")
        .Value!;

    /// <summary>One theory row per case id, so each case passes or fails on its own in the test report.</summary>
    public static TheoryData<string> CaseIds(string seamName)
    {
        var caseIds = new TheoryData<string>();
        foreach (JsonNode? vectorCase in LoadVectorFile(seamName)["cases"]!.AsArray())
        {
            caseIds.Add((string)vectorCase!["id"]!);
        }
        return caseIds;
    }

    public static JsonObject LoadCase(string seamName, string caseId) =>
        LoadVectorFile(seamName)["cases"]!.AsArray()
            .Single(vectorCase => (string?)vectorCase!["id"] == caseId)!
            .AsObject();

    /// <summary>Every case of a seam, for suite-shape assertions such as option and branch coverage.</summary>
    public static IEnumerable<JsonObject> LoadAllCases(string seamName) =>
        LoadVectorFile(seamName)["cases"]!.AsArray().Select(vectorCase => vectorCase!.AsObject());

    private static JsonObject LoadVectorFile(string seamName)
    {
        string vectorFilePath = Path.Combine(RepositoryRootDirectory, "shared", "golden", seamName + ".json");
        return JsonNode.Parse(File.ReadAllText(vectorFilePath))!.AsObject();
    }
}
