using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class PushToTalkGoldenVectorTests
{
    private const string SeamName = "push-to-talk";

    public static TheoryData<string> CaseIds => GoldenVectors.CaseIds(SeamName);

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void TransitionsMatchGoldenVector(string caseId)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);
        JsonNode input = vectorCase["input"]!;
        var shortcutOption = Enum.Parse<PushToTalkShortcutOption>((string)input["option"]!, ignoreCase: true);
        var pushToTalkDetector = new PushToTalkDetector(shortcutOption);

        var actualTransitions = new List<PushToTalkTransition>();
        if (input["rawEvents"] is JsonArray rawEvents)
        {
            // Windows-only cases go through the same translator the keyboard hook feeds.
            var keyboardEventTranslator = new LowLevelKeyboardEventTranslator();
            foreach (JsonNode? rawEvent in rawEvents)
            {
                NeutralKeyEvent? neutralKeyEvent = keyboardEventTranslator.Translate(new LowLevelKeyboardEvent(
                    (uint)rawEvent!["vk"]!,
                    (uint)rawEvent["scanCode"]!,
                    (bool)rawEvent["keyUp"]!));
                actualTransitions.Add(neutralKeyEvent is NeutralKeyEvent translatedEvent
                    ? pushToTalkDetector.Process(translatedEvent)
                    : PushToTalkTransition.None);
            }
        }
        else
        {
            foreach (JsonNode? neutralEvent in input["events"]!.AsArray())
            {
                actualTransitions.Add(pushToTalkDetector.Process(ReadNeutralKeyEvent(neutralEvent!)));
            }
        }

        List<PushToTalkTransition> expectedTransitions = vectorCase["expected"]!["transitions"]!.AsArray()
            .Select(transitionNode => Enum.Parse<PushToTalkTransition>((string)transitionNode!, ignoreCase: true))
            .ToList();
        Assert.Equal(expectedTransitions, actualTransitions);
    }

    private static NeutralKeyEvent ReadNeutralKeyEvent(JsonNode neutralEvent)
    {
        var eventType = Enum.Parse<NeutralKeyEventType>((string)neutralEvent["type"]!, ignoreCase: true);
        NeutralKey key = (string?)neutralEvent["key"] == "space" ? NeutralKey.Space : NeutralKey.Other;

        NeutralModifierKeys modifiers = NeutralModifierKeys.None;
        foreach (JsonNode? modifierName in neutralEvent["modifiers"]!.AsArray())
        {
            modifiers |= (string)modifierName! switch
            {
                "ctrl" => NeutralModifierKeys.Control,
                "alt" => NeutralModifierKeys.Alt,
                "shift" => NeutralModifierKeys.Shift,
                string unknownModifierName => throw new InvalidDataException($"Unknown modifier '{unknownModifierName}'"),
            };
        }
        return new NeutralKeyEvent(eventType, key, modifiers);
    }

    // Branch ids of the detector's two code paths (PushToTalkDetector.ShortcutTransition): the
    // modifier-only options use the flagsChanged path, the space options the keyDown/keyUp path.
    private static readonly string[] ModifierOnlyBranchIds =
    [
        "modifier-only-pressed",
        "modifier-only-released",
        "modifier-only-held",
        "modifier-only-contains",
        "modifier-only-ignores-keys",
    ];

    private static readonly string[] SpaceBranchIds =
    [
        "space-pressed-released",
        "space-release-any-modifiers",
        "space-missing-modifiers",
    ];

    // Branch ids of the Windows translator (LowLevelKeyboardEventTranslator), exercised by the cases
    // tagged platform windows. They are option-independent, so one occurrence anywhere is enough.
    private static readonly string[] WindowsTranslatorBranchIds =
    [
        "windows-left-keys",
        "windows-altgr-filtered",
        "windows-right-keys-ignored",
        "windows-space",
        "windows-shift-side-agnostic",
    ];

    [Fact]
    public void EveryOptionAndBranchCovered()
    {
        List<JsonObject> allVectorCases = GoldenVectors.LoadAllCases(SeamName).ToList();

        // The file is the single parity artefact for all four options (.shiftFunction is dropped on
        // Windows, audit C3), so every option must appear at least once.
        HashSet<PushToTalkShortcutOption> optionsUsedByCases = allVectorCases
            .Select(vectorCase => ParseShortcutOption(vectorCase["input"]!["option"]!))
            .ToHashSet();
        Assert.True(
            optionsUsedByCases.SetEquals(Enum.GetValues<PushToTalkShortcutOption>()),
            $"Options missing from the vector file: {string.Join(", ", Enum.GetValues<PushToTalkShortcutOption>().Except(optionsUsedByCases))}");

        // Each option must reach every detector branch its shape allows, so a regression in any
        // branch trips a case on that option instead of only on its neighbour in the file.
        var expectedBranchIdsPerOption = new Dictionary<PushToTalkShortcutOption, string[]>
        {
            [PushToTalkShortcutOption.ControlOption] = ModifierOnlyBranchIds,
            [PushToTalkShortcutOption.ShiftControl] = ModifierOnlyBranchIds,
            [PushToTalkShortcutOption.ControlOptionSpace] = SpaceBranchIds,
            [PushToTalkShortcutOption.ShiftControlSpace] = SpaceBranchIds,
        };
        HashSet<(PushToTalkShortcutOption Option, string? Branch)> optionBranchPairsCoveredByCases = allVectorCases
            .Select(vectorCase => (Option: ParseShortcutOption(vectorCase["input"]!["option"]!), Branch: (string?)vectorCase["branch"]))
            .ToHashSet();
        foreach (KeyValuePair<PushToTalkShortcutOption, string[]> expectedBranchIdsForOneOption in expectedBranchIdsPerOption)
        {
            foreach (string expectedBranchId in expectedBranchIdsForOneOption.Value)
            {
                Assert.Contains((expectedBranchIdsForOneOption.Key, expectedBranchId), optionBranchPairsCoveredByCases);
            }
        }

        HashSet<string?> branchIdsUsedByCases = allVectorCases
            .Select(vectorCase => (string?)vectorCase["branch"])
            .ToHashSet();
        foreach (string expectedWindowsBranchId in WindowsTranslatorBranchIds)
        {
            Assert.Contains(expectedWindowsBranchId, branchIdsUsedByCases);
        }

        // T-006 settled that right Shift counts, so the placeholder "shortcut:" comment that deferred
        // that decision to a later plan must not come back. It lived beside the translator's Shift
        // handling; check both Core files that own the shortcut semantics, including the detector.
        AssertFileHasNoStaleShortcutComment("PushToTalkDetector.cs");
        AssertFileHasNoStaleShortcutComment("LowLevelKeyboardEventTranslator.cs");
    }

    private static PushToTalkShortcutOption ParseShortcutOption(JsonNode optionNode) =>
        Enum.Parse<PushToTalkShortcutOption>((string)optionNode!, ignoreCase: true);

    private static void AssertFileHasNoStaleShortcutComment(string coreSourceFileName)
    {
        string coreSourceFilePath = Path.Combine(
            GoldenVectors.RepositoryRootDirectory, "windows", "src", "Clicky.Core", coreSourceFileName);
        Assert.False(
            File.ReadAllLines(coreSourceFilePath).Any(sourceLine =>
                sourceLine.Contains("// shortcut:", StringComparison.Ordinal)),
            $"A stale '// shortcut:' placeholder comment is back in {coreSourceFileName}");
    }
}
