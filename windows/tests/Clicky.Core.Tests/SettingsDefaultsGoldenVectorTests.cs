using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class SettingsDefaultsGoldenVectorTests
{
    private const string SeamName = "settings-defaults";

    public static TheoryData<string> CaseIds => GoldenVectors.CaseIds(SeamName);

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void MergedSettingsMatchGoldenVector(string caseId)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);
        string? userSettingsFileContents = (string?)vectorCase["input"]!["userSettingsFileJson"];
        JsonObject expected = vectorCase["expected"]!.AsObject();

        CompanionSettingsMergeResult mergeResult =
            CompanionSettingsMerger.MergeUserSettingsFileContentsOverDefaults(userSettingsFileContents);

        AssertMergedSettingsMatchExpected(expected["settings"]!.AsObject(), mergeResult.Settings);
        Assert.Equal((bool)expected["userSettingsFileWasCorrupt"]!, mergeResult.UserSettingsFileWasCorrupt);
        AssertUnrecognizedValuesMatchExpected(
            expected["unrecognizedUserSettingsValues"]!.AsObject(),
            mergeResult.UnrecognizedUserSettingsValues);
    }

    [Fact]
    public void VectorCoversEveryKnownSettingsFileKey()
    {
        JsonObject vectorDocument = GoldenVectors.LoadVectorDocument(SeamName);

        // The vector declares the persisted key set (plan WS-12); it must match the model's known
        // keys exactly, so a new settings key cannot ship without its vector coverage.
        Assert.Equal(
            CompanionSettingsMerger.KnownSettingsFileKeyNames,
            vectorDocument["settingsFileKeys"]!.AsArray().Select(keyNameNode => (string)keyNameNode!));

        // Every case must pin the full merged settings object, so each key's value is really asserted.
        foreach (JsonNode? vectorCase in vectorDocument["cases"]!.AsArray())
        {
            JsonObject expectedSettings = vectorCase!["expected"]!["settings"]!.AsObject();
            foreach (string knownSettingsFileKeyName in CompanionSettingsMerger.KnownSettingsFileKeyNames)
            {
                Assert.True(expectedSettings.ContainsKey(knownSettingsFileKeyName),
                    $"Case '{(string)vectorCase["id"]!}' does not pin settings key '{knownSettingsFileKeyName}'.");
            }
        }
    }

    private static void AssertMergedSettingsMatchExpected(JsonObject expectedSettings, CompanionSettings mergedSettings)
    {
        // The expected object is indexed with the model's own key-name constants, so a rename in the
        // model fails here instead of silently reading nothing.
        Assert.Equal(
            (string)expectedSettings[CompanionSettingsMerger.SelectedClaudeModelSettingsFileKeyName]!,
            mergedSettings.SelectedClaudeModel);
        Assert.Equal(
            (bool)expectedSettings[CompanionSettingsMerger.IsClickyCursorEnabledSettingsFileKeyName]!,
            mergedSettings.IsClickyCursorEnabled);
        Assert.Equal(
            (bool)expectedSettings[CompanionSettingsMerger.HasCompletedOnboardingSettingsFileKeyName]!,
            mergedSettings.HasCompletedOnboarding);
        Assert.Equal(
            (bool)expectedSettings[CompanionSettingsMerger.HasSubmittedEmailSettingsFileKeyName]!,
            mergedSettings.HasSubmittedEmail);
        Assert.Equal(
            Enum.Parse<PushToTalkShortcutOption>(
                (string)expectedSettings[CompanionSettingsMerger.PushToTalkShortcutSettingsFileKeyName]!, ignoreCase: true),
            mergedSettings.PushToTalkShortcut);
        // A JSON null distinct id reads back as null, the "no analytics id generated yet" default.
        Assert.Equal(
            (string?)expectedSettings[CompanionSettingsMerger.AnalyticsDistinctIdSettingsFileKeyName],
            mergedSettings.AnalyticsDistinctId);
    }

    private static void AssertUnrecognizedValuesMatchExpected(
        JsonObject expectedUnrecognizedValues,
        IReadOnlyDictionary<string, JsonNode?> actualUnrecognizedValues)
    {
        // Sorted on both sides so the key-set comparison does not depend on iteration order.
        Assert.Equal(
            expectedUnrecognizedValues.Select(recognizedProperty => recognizedProperty.Key).Order(StringComparer.Ordinal),
            actualUnrecognizedValues.Keys.Order(StringComparer.Ordinal));
        foreach (KeyValuePair<string, JsonNode?> expectedProperty in expectedUnrecognizedValues)
        {
            Assert.True(
                JsonNode.DeepEquals(expectedProperty.Value, actualUnrecognizedValues[expectedProperty.Key]),
                $"Unrecognized settings key '{expectedProperty.Key}' was not preserved with its value.");
        }
    }
}
