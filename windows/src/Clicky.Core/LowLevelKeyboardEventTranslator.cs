namespace Clicky.Core;

/// <summary>The fields of a Win32 KBDLLHOOKSTRUCT the shortcut needs. Plain numbers, so this stays testable off Windows.</summary>
public readonly record struct LowLevelKeyboardEvent(uint VirtualKeyCode, uint ScanCode, bool IsKeyUp);

/// <summary>
/// Turns raw WH_KEYBOARD_LL events into neutral key events for <see cref="PushToTalkDetector"/>
/// (ADR 001 tracer item 1). This is where the Windows keyboard rules live: only the left Ctrl and left
/// Alt keys count, and the fake left Ctrl that AltGr injects is dropped (audit C1, risk 2).
/// </summary>
public sealed class LowLevelKeyboardEventTranslator
{
    // Virtual-key codes from WinUser.h. Low-level hooks report the left/right-specific codes.
    private const uint VirtualKeySpace = 0x20;
    private const uint VirtualKeyLeftShift = 0xA0;
    private const uint VirtualKeyRightShift = 0xA1;
    private const uint VirtualKeyLeftControl = 0xA2;
    private const uint VirtualKeyRightControl = 0xA3;
    private const uint VirtualKeyLeftAlt = 0xA4;
    private const uint VirtualKeyRightAlt = 0xA5;

    // On AltGr layouts (German, French, Polish...) AltGr arrives as this synthetic left Ctrl plus right Alt.
    // Counting it as Ctrl would fire push-to-talk on every typed @, € or {.
    private const uint AltGrSyntheticLeftControlScanCode = 0x21D;

    private bool isLeftControlDown;
    private bool isLeftAltDown;
    private bool isLeftShiftDown;
    private bool isRightShiftDown;

    /// <summary>Returns null for events that must not reach the detector at all (the AltGr fake Ctrl).</summary>
    public NeutralKeyEvent? Translate(LowLevelKeyboardEvent keyboardEvent)
    {
        bool isKeyDown = !keyboardEvent.IsKeyUp;
        switch (keyboardEvent.VirtualKeyCode)
        {
            case VirtualKeyLeftControl when keyboardEvent.ScanCode == AltGrSyntheticLeftControlScanCode:
                // Dropped on the way up as well, so releasing AltGr cannot clear a real left Ctrl that is still held.
                return null;
            case VirtualKeyLeftControl:
                isLeftControlDown = isKeyDown;
                return ModifiersChangedEvent();
            case VirtualKeyLeftAlt:
                isLeftAltDown = isKeyDown;
                return ModifiersChangedEvent();
            case VirtualKeyLeftShift:
                isLeftShiftDown = isKeyDown;
                return ModifiersChangedEvent();
            case VirtualKeyRightShift:
                isRightShiftDown = isKeyDown;
                return ModifiersChangedEvent();
            case VirtualKeyRightControl:
            case VirtualKeyRightAlt:
                // Still a modifier change, like a macOS flagsChanged, but right-hand keys never count toward the shortcut.
                return ModifiersChangedEvent();
            default:
                NeutralKey neutralKey = keyboardEvent.VirtualKeyCode == VirtualKeySpace ? NeutralKey.Space : NeutralKey.Other;
                NeutralKeyEventType eventType = keyboardEvent.IsKeyUp ? NeutralKeyEventType.KeyUp : NeutralKeyEventType.KeyDown;
                return new NeutralKeyEvent(eventType, neutralKey, CurrentModifiers());
        }
    }

    /// <summary>Forget every held key, for when the hook was dead and key-up events may have been missed.</summary>
    public void Reset()
    {
        isLeftControlDown = false;
        isLeftAltDown = false;
        isLeftShiftDown = false;
        isRightShiftDown = false;
    }

    private NeutralKeyEvent ModifiersChangedEvent() =>
        new(NeutralKeyEventType.FlagsChanged, NeutralKey.Other, CurrentModifiers());

    private NeutralModifierKeys CurrentModifiers()
    {
        NeutralModifierKeys modifiers = NeutralModifierKeys.None;
        if (isLeftControlDown)
        {
            modifiers |= NeutralModifierKeys.Control;
        }
        if (isLeftAltDown)
        {
            modifiers |= NeutralModifierKeys.Alt;
        }
        // shortcut: either Shift key counts; parity spec PTT-04 leaves right Shift to 03-plan, revisit there.
        if (isLeftShiftDown || isRightShiftDown)
        {
            modifiers |= NeutralModifierKeys.Shift;
        }
        return modifiers;
    }
}
