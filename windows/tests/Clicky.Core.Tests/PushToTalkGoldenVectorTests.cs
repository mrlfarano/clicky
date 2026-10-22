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
}
