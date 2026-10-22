using Clicky.Core;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-007: the hook-recovery behaviors, driven without any real Windows input plumbing. The three
/// pieces the acceptance criteria name are covered in order: resume/unlock reinstall the hook and
/// reset the translator and detector (fake hook installer, messages delivered to the message window),
/// a shortcut change posted to the translator channel applies to the next key event without touching
/// the hook, and a run of key events logs transitions and state but never a vk or scan code.
/// </summary>
[Collection(ClickyProcessReplayCollection.Name)] // captures Console.Out, which ClickyLog mirrors every line to
public sealed class HookRecoveryTests
{
    // Virtual-key and scan codes the tests inject, from WinUser.h set-key values. Scan 0x21D is never
    // used: that is AltGr's synthetic left Ctrl, which the translator drops before detection.
    private const uint VirtualKeyLeftControl = 0xA2;
    private const uint VirtualKeyLeftAlt = 0xA4;
    private const uint VirtualKeyLeftShift = 0xA0;
    private const uint ScanCodeLeftControl = 0x1D;
    private const uint ScanCodeLeftAlt = 0x38;
    private const uint ScanCodeLeftShift = 0x2A;

    // The timestamp prefix ClickyLog stamps on every line, "yyyy-MM-dd HH:mm:ss.fff " (23 characters
    // plus the separating space = 24), which must be stripped before scanning for key codes: a
    // timestamp alone contains plenty of digit pairs.
    private const int TimestampPrefixLength = 24;

    [Fact]
    public void ReinstallsAndResetsOnResumeAndUnlock()
    {
        using CapturedConsoleLog capturedLog = CapturedConsoleLog.Start();
        var hookInstallerFake = new CountingKeyboardHookInstallerFake();
        var monitor = new GlobalPushToTalkShortcutMonitor(
            PushToTalkShortcutOption.ControlOption, stallFirstHookCallback: false, hookInstallerFake);
        var observedTransitions = new List<PushToTalkTransition>();
        monitor.ShortcutTransitioned += observedTransitions.Add;

        // Left Ctrl is held when the system resumes from sleep...
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftControl, ScanCodeLeftControl, isKeyUp: false);
        monitor.HandleRawInputWindowMessage(
            GlobalPushToTalkShortcutMonitor.PowerBroadcastWindowMessage,
            GlobalPushToTalkShortcutMonitor.PowerBroadcastResumeAutomaticWParam,
            lParam: IntPtr.Zero);
        Assert.Equal(1, hookInstallerFake.InstallKeyboardHookCallCount);
        // ...the recovery reinstalled the hook and posted its reset to the translator channel.
        monitor.ProcessKeyboardHookSignal(ReadNextSignal(monitor));
        AssertChannelIsEmpty(monitor);

        // A lone Alt press must NOT trigger push-to-talk: the reset forgot the stale Ctrl, which
        // otherwise makes a plain Alt+Tab fire the shortcut.
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftAlt, ScanCodeLeftAlt, isKeyUp: false);
        Assert.Empty(observedTransitions);

        // A full Ctrl+Alt press after the resume recovery still fires...
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftControl, ScanCodeLeftControl, isKeyUp: false);
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftAlt, ScanCodeLeftAlt, isKeyUp: false);
        Assert.Equal([PushToTalkTransition.Pressed], observedTransitions);

        // ...and session unlock gets the same recovery...
        monitor.HandleRawInputWindowMessage(
            GlobalPushToTalkShortcutMonitor.SessionChangeWindowMessage,
            GlobalPushToTalkShortcutMonitor.SessionUnlockWParam,
            lParam: IntPtr.Zero);
        Assert.Equal(2, hookInstallerFake.InstallKeyboardHookCallCount);
        monitor.ProcessKeyboardHookSignal(ReadNextSignal(monitor));
        AssertChannelIsEmpty(monitor);

        // ...so the detector no longer remembers being held: the identical press fires again.
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftControl, ScanCodeLeftControl, isKeyUp: false);
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftAlt, ScanCodeLeftAlt, isKeyUp: false);
        Assert.Equal([PushToTalkTransition.Pressed, PushToTalkTransition.Pressed], observedTransitions);

        Assert.Contains("hook reinstalled after resume from sleep", capturedLog.CapturedText);
        Assert.Contains("hook reinstalled after session unlock", capturedLog.CapturedText);
    }

    [Fact]
    public void ShortcutHotSwaps()
    {
        using CapturedConsoleLog capturedLog = CapturedConsoleLog.Start();
        var hookInstallerFake = new CountingKeyboardHookInstallerFake();
        var monitor = new GlobalPushToTalkShortcutMonitor(
            PushToTalkShortcutOption.ControlOption, stallFirstHookCallback: false, hookInstallerFake);
        var observedTransitions = new List<PushToTalkTransition>();
        monitor.ShortcutTransitioned += observedTransitions.Add;

        // Under the starting option, Ctrl+Alt presses once and releases once.
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftControl, ScanCodeLeftControl, isKeyUp: false);
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftAlt, ScanCodeLeftAlt, isKeyUp: false);
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftControl, ScanCodeLeftControl, isKeyUp: true);
        Assert.Equal([PushToTalkTransition.Pressed, PushToTalkTransition.Released], observedTransitions);

        // The hot-swap travels on the translator channel and applies from the next key event...
        monitor.ChangeShortcut(PushToTalkShortcutOption.ShiftControl);
        monitor.ProcessKeyboardHookSignal(ReadNextSignal(monitor));
        AssertChannelIsEmpty(monitor);
        // ...without ever re-creating the hook.
        Assert.Equal(0, hookInstallerFake.InstallKeyboardHookCallCount);

        // Shift+Ctrl now presses...
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftShift, ScanCodeLeftShift, isKeyUp: false);
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftControl, ScanCodeLeftControl, isKeyUp: false);
        // ...and the old Ctrl+Alt combination is inert: lifting Ctrl releases, and a lone Alt adds nothing.
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftControl, ScanCodeLeftControl, isKeyUp: true);
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftAlt, ScanCodeLeftAlt, isKeyUp: false);
        Assert.Equal(
            [PushToTalkTransition.Pressed, PushToTalkTransition.Released, PushToTalkTransition.Pressed, PushToTalkTransition.Released],
            observedTransitions);
        Assert.Contains("shortcut changed to", capturedLog.CapturedText);
    }

    [Fact]
    public void LogHasTransitionsNotKeyCodes()
    {
        using CapturedConsoleLog capturedLog = CapturedConsoleLog.Start();
        var hookInstallerFake = new CountingKeyboardHookInstallerFake();
        var monitor = new GlobalPushToTalkShortcutMonitor(
            PushToTalkShortcutOption.ControlOption, stallFirstHookCallback: false, hookInstallerFake);

        // Every key the run touches; the assertions below recycle these pairs as the forbidden strings.
        var runKeyCodes = new List<(uint VirtualKeyCode, uint ScanCode)>
        {
            (VirtualKeyLeftControl, ScanCodeLeftControl),
            (VirtualKeyLeftAlt, ScanCodeLeftAlt),
            (VirtualKeyLeftShift, ScanCodeLeftShift),
            (0x41, 0x1E), // A
            (0x42, 0x30), // B
            (0x58, 0x2D), // X
            (0x5A, 0x2C), // Z
        };

        // A shortcut press and release under the starting option...
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftControl, ScanCodeLeftControl, isKeyUp: false);
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftAlt, ScanCodeLeftAlt, isKeyUp: false);
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftControl, ScanCodeLeftControl, isKeyUp: true);
        // ...unrelated typing that must leave no trace at all...
        foreach ((uint typedVirtualKeyCode, uint typedScanCode) in runKeyCodes.Skip(3))
        {
            ProcessRawKeyboardEvent(monitor, typedVirtualKeyCode, typedScanCode, isKeyUp: false);
            ProcessRawKeyboardEvent(monitor, typedVirtualKeyCode, typedScanCode, isKeyUp: true);
        }
        // ...a recovery state line, a shortcut change, and a press under the new option.
        monitor.HandleRawInputWindowMessage(
            GlobalPushToTalkShortcutMonitor.PowerBroadcastWindowMessage,
            GlobalPushToTalkShortcutMonitor.PowerBroadcastResumeAutomaticWParam,
            lParam: IntPtr.Zero);
        monitor.ProcessKeyboardHookSignal(ReadNextSignal(monitor));
        monitor.ChangeShortcut(PushToTalkShortcutOption.ShiftControl);
        monitor.ProcessKeyboardHookSignal(ReadNextSignal(monitor));
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftShift, ScanCodeLeftShift, isKeyUp: false);
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftControl, ScanCodeLeftControl, isKeyUp: false);
        ProcessRawKeyboardEvent(monitor, VirtualKeyLeftShift, ScanCodeLeftShift, isKeyUp: true);

        string capturedLogText = capturedLog.CapturedText;
        // Transitions and state are present: pressed, released, the reinstall reason and the change.
        Assert.Contains("ptt pressed", capturedLogText);
        Assert.Contains("ptt released", capturedLogText);
        Assert.Contains("hook reinstalled after resume from sleep", capturedLogText);
        Assert.Contains("shortcut changed to", capturedLogText);

        string[] capturedLogLines = capturedLogText.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);
        // The run must have logged enough lines for the scan below to mean anything (the typed keys log
        // nothing, by the same privacy rule).
        Assert.True(capturedLogLines.Length >= 6, $"expected at least the 6 transition/state lines, got:\n{capturedLogText}");

        foreach (string capturedLogLine in capturedLogLines)
        {
            // Every line is timestamp + space + message, so the message starts after the fixed prefix.
            Assert.True(capturedLogLine.Length > TimestampPrefixLength, $"log line has no room for a message: '{capturedLogLine}'");
            string logMessageWithoutTimestamp = capturedLogLine[TimestampPrefixLength..];
            foreach ((uint runVirtualKeyCode, uint runScanCode) in runKeyCodes)
            {
                // Decimal, bare hexadecimal and 0x-prefixed hexadecimal cover every way a vk or scan code
                // could be formatted into a log line by interpolation or exception text.
                Assert.DoesNotContain(runVirtualKeyCode.ToString(), logMessageWithoutTimestamp);
                Assert.DoesNotContain(runScanCode.ToString(), logMessageWithoutTimestamp);
                Assert.DoesNotContain(runVirtualKeyCode.ToString("X"), logMessageWithoutTimestamp);
                Assert.DoesNotContain(runScanCode.ToString("X"), logMessageWithoutTimestamp);
                Assert.DoesNotContain("0x" + runVirtualKeyCode.ToString("X"), logMessageWithoutTimestamp);
                Assert.DoesNotContain("0x" + runScanCode.ToString("X"), logMessageWithoutTimestamp);
            }
        }
    }

    /// <summary>Posts one raw key event and runs the exact signal the hook callback would have enqueued.</summary>
    private static void ProcessRawKeyboardEvent(
        GlobalPushToTalkShortcutMonitor monitor, uint virtualKeyCode, uint scanCode, bool isKeyUp)
    {
        monitor.PostRawKeyboardEvent(new LowLevelKeyboardEvent(virtualKeyCode, scanCode, isKeyUp));
        monitor.ProcessKeyboardHookSignal(ReadNextSignal(monitor));
    }

    private static KeyboardHookSignal ReadNextSignal(GlobalPushToTalkShortcutMonitor monitor)
    {
        Assert.True(
            monitor.KeyboardHookSignals.Reader.TryRead(out KeyboardHookSignal keyboardHookSignal),
            "the monitor posted no signal to the translator channel");
        return keyboardHookSignal;
    }

    private static void AssertChannelIsEmpty(GlobalPushToTalkShortcutMonitor monitor) =>
        Assert.False(
            monitor.KeyboardHookSignals.Reader.TryRead(out _),
            "the monitor posted more signals to the translator channel than the test expected");
}

/// <summary>
/// The T-007 hook-recovery test double: counts installs without ever calling SetWindowsHookEx, so a
/// test never installs a real system-wide keyboard hook.
/// </summary>
internal sealed class CountingKeyboardHookInstallerFake : IKeyboardHookInstaller
{
    public int InstallKeyboardHookCallCount { get; private set; }

    public UnhookWindowsHookExSafeHandle? CurrentKeyboardHookHandle => null;

    public void InstallKeyboardHook() => InstallKeyboardHookCallCount++;
}

/// <summary>
/// Captures ClickyLog's console mirror for one test. ClickyLog.Write appends to the file and writes
/// the same line to the console under one lock, so the captured text is the log verbatim without the
/// tests depending on which data directory the process-wide log file resolved to.
/// </summary>
internal sealed class CapturedConsoleLog : IDisposable
{
    private readonly TextWriter originalConsoleOut;
    private readonly StringWriter capturedTextWriter = new();

    private CapturedConsoleLog()
    {
        originalConsoleOut = Console.Out;
        Console.SetOut(capturedTextWriter);
    }

    public static CapturedConsoleLog Start() => new();

    public string CapturedText
    {
        get
        {
            lock (capturedTextWriter)
            {
                return capturedTextWriter.ToString();
            }
        }
    }

    public void Dispose()
    {
        Console.SetOut(originalConsoleOut);
        capturedTextWriter.Dispose();
    }
}
