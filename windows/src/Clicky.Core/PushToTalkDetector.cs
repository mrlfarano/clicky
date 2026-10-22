namespace Clicky.Core;

/// <summary>
/// Platform-neutral modifier set (seam 7). Each platform's event source decides which physical keys
/// count: on Windows, Control means left Ctrl and Alt means left Alt only (ADR 001 "Shortcut"), while
/// either Shift key counts because macOS's .shift flag is side-agnostic and Shift carries no AltGr
/// hazard (decision PTT-04).
/// Fn is left out because the only option that used it (.shiftFunction) is dropped on Windows (audit C3).
/// </summary>
[Flags]
public enum NeutralModifierKeys
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
}

/// <summary>Mirrors Swift's ShortcutEventType: a modifier changed, or a non-modifier key went down or up.</summary>
public enum NeutralKeyEventType
{
    FlagsChanged,
    KeyDown,
    KeyUp,
}

/// <summary>The only non-modifier key a shortcut uses is Space (macOS key code 49); every other key is Other.</summary>
public enum NeutralKey
{
    Other,
    Space,
}

public readonly record struct NeutralKeyEvent(NeutralKeyEventType EventType, NeutralKey Key, NeutralModifierKeys Modifiers);

/// <summary>The macOS shortcut options minus .shiftFunction (BuddyDictationManager.swift:17-22, ADR 001 "Shortcut").</summary>
public enum PushToTalkShortcutOption
{
    ControlOption,
    ShiftControl,
    ControlOptionSpace,
    ShiftControlSpace,
}

public enum PushToTalkTransition
{
    None,
    Pressed,
    Released,
}

/// <summary>
/// Port of BuddyPushToTalkShortcut.shortcutTransition (BuddyDictationManager.swift:157-199). The Swift
/// function is stateless and its caller (GlobalPushToTalkShortcutMonitor.swift:111-128) remembers whether
/// the shortcut is held; this class keeps both halves together.
/// </summary>
public sealed class PushToTalkDetector(PushToTalkShortcutOption shortcutOption)
{
    public bool IsShortcutCurrentlyPressed { get; private set; }

    public PushToTalkTransition Process(NeutralKeyEvent keyEvent)
    {
        PushToTalkTransition transition = ShortcutTransition(shortcutOption, keyEvent, IsShortcutCurrentlyPressed);
        if (transition == PushToTalkTransition.Pressed)
        {
            IsShortcutCurrentlyPressed = true;
        }
        else if (transition == PushToTalkTransition.Released)
        {
            IsShortcutCurrentlyPressed = false;
        }
        return transition;
    }

    /// <summary>Forget a held shortcut without reporting a release, for when key events may have been lost.</summary>
    public void Reset() => IsShortcutCurrentlyPressed = false;

    public static PushToTalkTransition ShortcutTransition(
        PushToTalkShortcutOption shortcutOption,
        NeutralKeyEvent keyEvent,
        bool wasShortcutPreviouslyPressed)
    {
        NeutralModifierKeys? modifierOnlyKeys = ModifierOnlyKeys(shortcutOption);
        if (modifierOnlyKeys is NeutralModifierKeys requiredModifierKeys)
        {
            if (keyEvent.EventType != NeutralKeyEventType.FlagsChanged)
            {
                return PushToTalkTransition.None;
            }

            // "Contains", not "equals": holding an extra modifier such as Shift does not block the shortcut.
            bool isShortcutCurrentlyPressed = keyEvent.Modifiers.HasFlag(requiredModifierKeys);
            if (isShortcutCurrentlyPressed && !wasShortcutPreviouslyPressed)
            {
                return PushToTalkTransition.Pressed;
            }
            if (!isShortcutCurrentlyPressed && wasShortcutPreviouslyPressed)
            {
                return PushToTalkTransition.Released;
            }
            return PushToTalkTransition.None;
        }

        NeutralModifierKeys spaceShortcutModifierKeys = SpaceShortcutModifierKeys(shortcutOption);
        bool matchesModifierKeys = keyEvent.Modifiers.HasFlag(spaceShortcutModifierKeys);

        if (keyEvent.EventType == NeutralKeyEventType.KeyDown
            && keyEvent.Key == NeutralKey.Space
            && matchesModifierKeys
            && !wasShortcutPreviouslyPressed)
        {
            return PushToTalkTransition.Pressed;
        }

        // Space-up releases whatever the modifiers are, so lifting Ctrl/Alt first does not end the press.
        if (keyEvent.EventType == NeutralKeyEventType.KeyUp
            && keyEvent.Key == NeutralKey.Space
            && wasShortcutPreviouslyPressed)
        {
            return PushToTalkTransition.Released;
        }

        return PushToTalkTransition.None;
    }

    private static NeutralModifierKeys? ModifierOnlyKeys(PushToTalkShortcutOption shortcutOption) => shortcutOption switch
    {
        PushToTalkShortcutOption.ControlOption => NeutralModifierKeys.Control | NeutralModifierKeys.Alt,
        PushToTalkShortcutOption.ShiftControl => NeutralModifierKeys.Shift | NeutralModifierKeys.Control,
        _ => null,
    };

    private static NeutralModifierKeys SpaceShortcutModifierKeys(PushToTalkShortcutOption shortcutOption) => shortcutOption switch
    {
        PushToTalkShortcutOption.ControlOptionSpace => NeutralModifierKeys.Control | NeutralModifierKeys.Alt,
        PushToTalkShortcutOption.ShiftControlSpace => NeutralModifierKeys.Shift | NeutralModifierKeys.Control,
        _ => throw new ArgumentOutOfRangeException(nameof(shortcutOption), shortcutOption, "Not a space shortcut option."),
    };
}
