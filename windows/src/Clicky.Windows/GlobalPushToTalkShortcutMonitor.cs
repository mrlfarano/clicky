using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows.Interop;
using System.Windows.Threading;
using Clicky.Core;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Clicky.Windows;

/// <summary>
/// System-wide push-to-talk, the Windows counterpart of GlobalPushToTalkShortcutMonitor.swift (audit C1, C2).
/// A WH_KEYBOARD_LL hook runs on its own thread and only enqueues; translation and detection happen on a
/// worker. Windows silently removes a low-level hook whose callback exceeds LowLevelHooksTimeout, so a Raw
/// Input watchdog on the same thread reinstalls the hook when it sees keys the hook did not.
/// </summary>
internal sealed class GlobalPushToTalkShortcutMonitor(PushToTalkShortcutOption shortcutOption, bool stallFirstHookCallback)
{
    private const int WindowMessageInput = 0x00FF; // WM_INPUT
    private const int HookLivenessWindowMilliseconds = 500; // ADR 001 tracer item 1

    private readonly Channel<KeyboardHookSignal> keyboardHookSignals = Channel.CreateUnbounded<KeyboardHookSignal>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    // Kept in fields for the life of the process: the native hook calls this delegate, so the GC must never collect it.
    private HOOKPROC? keyboardHookProcedure;
    private FreeLibrarySafeHandle? currentModuleHandle;
    private UnhookWindowsHookExSafeHandle? keyboardHookHandle;
    private HwndSource? rawInputWindow;
    private DispatcherTimer? hookLivenessCheckTimer;

    // Touched only on the hook thread.
    private long lastHookCallbackTickMilliseconds;
    private long? pendingRawKeyDownTickMilliseconds;
    private bool hasStalledHookCallback;

    /// <summary>Raised on a worker thread; subscribers marshal to their own thread.</summary>
    public event Action<PushToTalkTransition>? ShortcutTransitioned;

    public void Start()
    {
        var hookInstallation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookThread = new Thread(() => RunHookThread(hookInstallation))
        {
            IsBackground = true,
            Name = "Clicky keyboard hook",
        };
        // HwndSource (the raw input window) needs an STA thread.
        hookThread.SetApartmentState(ApartmentState.STA);
        hookThread.Start();
        // Surfaces an install failure to the caller instead of running with no shortcut.
        hookInstallation.Task.GetAwaiter().GetResult();

        _ = Task.Run(TranslateKeyboardHookSignalsAsync);
    }

    private void RunHookThread(TaskCompletionSource hookInstallation)
    {
        try
        {
            InstallKeyboardHook();
            RegisterRawInputWatchdog();
            hookInstallation.SetResult();
        }
        catch (Exception installationException)
        {
            hookInstallation.SetException(installationException);
            return;
        }
        // Dispatcher.Run is this thread's GetMessage loop. Hook callbacks are delivered through it, so nothing
        // slow may ever be queued on this dispatcher.
        Dispatcher.Run();
    }

    private void InstallKeyboardHook()
    {
        keyboardHookProcedure ??= KeyboardHookCallback;
        currentModuleHandle ??= PInvoke.GetModuleHandle((string?)null);
        keyboardHookHandle = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, keyboardHookProcedure, currentModuleHandle, 0);
        if (keyboardHookHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_KEYBOARD_LL) failed");
        }
        lastHookCallbackTickMilliseconds = Environment.TickCount64;
    }

    private unsafe LRESULT KeyboardHookCallback(int hookCode, WPARAM keyboardMessage, LPARAM keyboardHookData)
    {
        if (hookCode >= 0)
        {
            lastHookCallbackTickMilliseconds = Environment.TickCount64;
            var keyboardEvent = (KBDLLHOOKSTRUCT*)keyboardHookData.Value;
            bool isKeyUp = (keyboardEvent->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_UP) != 0;
            // O(1): enqueue and return. Anything slower here risks Windows removing the hook.
            keyboardHookSignals.Writer.TryWrite(new KeyboardHookSignal(
                new LowLevelKeyboardEvent(keyboardEvent->vkCode, keyboardEvent->scanCode, isKeyUp),
                IsHookReinstalled: false));

            if (stallFirstHookCallback && !hasStalledHookCallback)
            {
                hasStalledHookCallback = true;
                StallLongerThanLowLevelHooksTimeout();
            }
        }
        // Listen-only: every key always continues down the hook chain, so typing is never swallowed.
        return PInvoke.CallNextHookEx(keyboardHookHandle!, hookCode, keyboardMessage, keyboardHookData);
    }

    private unsafe void RegisterRawInputWatchdog()
    {
        // A message-only window (parent HWND_MESSAGE) on this thread, purely to receive WM_INPUT.
        rawInputWindow = new HwndSource(new HwndSourceParameters("Clicky raw input watchdog") { ParentWindow = new IntPtr(-3) });
        rawInputWindow.AddHook(RawInputWindowProcedure);

        var keyboardRawInputDevice = new RAWINPUTDEVICE
        {
            usUsagePage = 0x01, // HID_USAGE_PAGE_GENERIC
            usUsage = 0x06, // HID_USAGE_GENERIC_KEYBOARD
            dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK, // keep receiving input while other apps have focus
            hwndTarget = (HWND)rawInputWindow.Handle,
        };
        if (!PInvoke.RegisterRawInputDevices(new ReadOnlySpan<RAWINPUTDEVICE>(ref keyboardRawInputDevice), (uint)sizeof(RAWINPUTDEVICE)))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterRawInputDevices failed");
        }

        hookLivenessCheckTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(HookLivenessWindowMilliseconds),
        };
        hookLivenessCheckTimer.Tick += (_, _) => CheckHookLiveness();
    }

    private IntPtr RawInputWindowProcedure(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WindowMessageInput && pendingRawKeyDownTickMilliseconds is null && IsRawKeyDown(lParam))
        {
            pendingRawKeyDownTickMilliseconds = Environment.TickCount64;
            hookLivenessCheckTimer!.Start();
        }
        // Not marked handled: WM_INPUT must reach DefWindowProc so Windows can free the input data.
        return IntPtr.Zero;
    }

    private static unsafe bool IsRawKeyDown(IntPtr rawInputHandle)
    {
        const uint RawInputTypeKeyboard = 1; // RIM_TYPEKEYBOARD
        const ushort RawKeyBreakFlag = 0x01; // RI_KEY_BREAK: set when the key went up

        RAWINPUT rawInput;
        uint rawInputSize = (uint)sizeof(RAWINPUT);
        uint copiedByteCount = PInvoke.GetRawInputData(
            (HRAWINPUT)rawInputHandle, RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, &rawInput, ref rawInputSize, (uint)sizeof(RAWINPUTHEADER));
        return copiedByteCount != uint.MaxValue
            && rawInput.header.dwType == RawInputTypeKeyboard
            && (rawInput.data.keyboard.Flags & RawKeyBreakFlag) == 0;
    }

    private void CheckHookLiveness()
    {
        hookLivenessCheckTimer!.Stop();
        if (pendingRawKeyDownTickMilliseconds is not long rawKeyDownTickMilliseconds)
        {
            return;
        }
        pendingRawKeyDownTickMilliseconds = null;

        // The hook sees the same keystroke within milliseconds of Raw Input, before or after it. Silence over
        // the whole window means Windows removed the hook without telling us.
        if (lastHookCallbackTickMilliseconds >= rawKeyDownTickMilliseconds - HookLivenessWindowMilliseconds)
        {
            return;
        }

        // Disposing calls UnhookWindowsHookEx, which fails harmlessly when Windows already removed the hook.
        keyboardHookHandle?.Dispose();
        InstallKeyboardHook();
        keyboardHookSignals.Writer.TryWrite(new KeyboardHookSignal(default, IsHookReinstalled: true));
        ClickyLog.Write("watchdog: hook reinstalled");
    }

    private async Task TranslateKeyboardHookSignalsAsync()
    {
        var keyboardEventTranslator = new LowLevelKeyboardEventTranslator();
        var pushToTalkDetector = new PushToTalkDetector(shortcutOption);

        await foreach (KeyboardHookSignal keyboardHookSignal in keyboardHookSignals.Reader.ReadAllAsync())
        {
            if (keyboardHookSignal.IsHookReinstalled)
            {
                // Key-ups may have been lost while the hook was dead. A left Ctrl stuck "down" would make a
                // lone Alt press (Alt+Tab) trigger push-to-talk, so start from all keys up.
                keyboardEventTranslator.Reset();
                pushToTalkDetector.Reset();
                continue;
            }
            if (keyboardEventTranslator.Translate(keyboardHookSignal.KeyboardEvent) is not NeutralKeyEvent neutralKeyEvent)
            {
                continue;
            }
            PushToTalkTransition shortcutTransition = pushToTalkDetector.Process(neutralKeyEvent);
            if (shortcutTransition != PushToTalkTransition.None)
            {
                ShortcutTransitioned?.Invoke(shortcutTransition);
            }
        }
    }

    /// <summary>For ADR 001 acceptance 6: block one callback for twice LowLevelHooksTimeout so Windows drops the hook.</summary>
    private static void StallLongerThanLowLevelHooksTimeout()
    {
        int stallMilliseconds = 2 * ReadLowLevelHooksTimeoutMilliseconds();
        ClickyLog.Write($"hook stalled (debug) for {stallMilliseconds} ms");
        Thread.Sleep(stallMilliseconds);
    }

    private static int ReadLowLevelHooksTimeoutMilliseconds()
    {
        // Used when the value is absent. Windows 10 1709+ caps the timeout at 1000 ms, so 2x this always exceeds it.
        const int AssumedLowLevelHooksTimeoutMilliseconds = 1000;
        using RegistryKey? desktopSettingsKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        return desktopSettingsKey?.GetValue("LowLevelHooksTimeout") switch
        {
            int timeoutMilliseconds => timeoutMilliseconds,
            string timeoutText when int.TryParse(timeoutText, out int parsedTimeoutMilliseconds) => parsedTimeoutMilliseconds,
            _ => AssumedLowLevelHooksTimeoutMilliseconds,
        };
    }

    private readonly record struct KeyboardHookSignal(LowLevelKeyboardEvent KeyboardEvent, bool IsHookReinstalled);
}
