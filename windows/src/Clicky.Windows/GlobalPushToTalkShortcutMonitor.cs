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

/// <summary>What travels on the translator channel (T-007): one raw key event, a "the hook was
/// reinstalled, forget held keys" reset, or a shortcut hot-swap.</summary>
internal enum KeyboardHookSignalKind
{
    RawKeyboardEvent,
    HookWasReinstalled,
    ShortcutOptionChanged,
}

/// <summary>
/// One message the hook thread posts to the translation worker. A single channel (rather than one per
/// kind) keeps the worker single-reader and preserves ordering: a reset posted by a recovery path is
/// always seen before the key events of the freshly installed hook.
/// </summary>
internal readonly record struct KeyboardHookSignal(
    KeyboardHookSignalKind Kind,
    LowLevelKeyboardEvent KeyboardEvent,
    PushToTalkShortcutOption ShortcutOption)
{
    public static KeyboardHookSignal FromRawKeyboardEvent(LowLevelKeyboardEvent keyboardEvent) =>
        new(KeyboardHookSignalKind.RawKeyboardEvent, keyboardEvent, ShortcutOption: default);

    public static KeyboardHookSignal HookWasReinstalled() =>
        new(KeyboardHookSignalKind.HookWasReinstalled, KeyboardEvent: default, ShortcutOption: default);

    public static KeyboardHookSignal ShortcutOptionChangedTo(PushToTalkShortcutOption newShortcutOption) =>
        new(KeyboardHookSignalKind.ShortcutOptionChanged, KeyboardEvent: default, ShortcutOption: newShortcutOption);
}

/// <summary>
/// The WH_KEYBOARD_LL hook lifetime the recovery paths drive (T-007). Production wires the real
/// SetWindowsHookEx owner, which must run on the hook thread; the hook-recovery tests substitute a
/// counting fake, because a test must never install a system-wide keyboard hook.
/// </summary>
internal interface IKeyboardHookInstaller
{
    /// <summary>The live hook handle <c>CallNextHookEx</c> needs in the callback; null before the first install.</summary>
    UnhookWindowsHookExSafeHandle? CurrentKeyboardHookHandle { get; }

    /// <summary>Removes any current hook and installs a fresh one. Used for the first install and for every
    /// recovery reinstall alike: removing first is harmless when Windows already dropped the hook for
    /// exceeding LowLevelHooksTimeout, which is exactly the case recovery handles.</summary>
    void InstallKeyboardHook();
}

/// <summary>
/// The production installer: owns the SetWindowsHookEx handle plus the module handle and the delegate
/// reference that must stay alive for the process so the native hook never calls collected memory.
/// </summary>
internal sealed class WindowsKeyboardHookInstaller(HOOKPROC keyboardHookCallbackProcedure) : IKeyboardHookInstaller
{
    // The native hook calls this delegate; the field keeps the GC from collecting it for the process lifetime.
    private readonly HOOKPROC keyboardHookCallbackProcedure = keyboardHookCallbackProcedure;
    private FreeLibrarySafeHandle? currentModuleHandle;
    private UnhookWindowsHookExSafeHandle? keyboardHookHandle;

    public UnhookWindowsHookExSafeHandle? CurrentKeyboardHookHandle => keyboardHookHandle;

    public void InstallKeyboardHook()
    {
        // Disposing the previous handle calls UnhookWindowsHookEx, which fails harmlessly when Windows
        // already removed the hook — recovery reinstalls rely on that being safe.
        keyboardHookHandle?.Dispose();
        currentModuleHandle ??= PInvoke.GetModuleHandle((string?)null);
        keyboardHookHandle = PInvoke.SetWindowsHookEx(
            WINDOWS_HOOK_ID.WH_KEYBOARD_LL, keyboardHookCallbackProcedure, currentModuleHandle, 0);
        if (keyboardHookHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_KEYBOARD_LL) failed");
        }
    }
}

/// <summary>
/// System-wide push-to-talk, the Windows counterpart of GlobalPushToTalkShortcutMonitor.swift (audit C1, C2).
/// A WH_KEYBOARD_LL hook runs on its own thread and only enqueues; translation and detection happen on a
/// worker. Windows silently removes a low-level hook whose callback exceeds LowLevelHooksTimeout, so a Raw
/// Input watchdog on the same thread reinstalls the hook when it sees keys the hook did not (T-007 also
/// reinstalls on resume-from-sleep and session-unlock messages, and hot-swaps the shortcut option over the
/// translator channel without ever touching the installed hook).
/// </summary>
internal sealed class GlobalPushToTalkShortcutMonitor
{
    private const int WindowMessageInput = 0x00FF; // WM_INPUT
    private const int HookLivenessWindowMilliseconds = 500; // ADR 001 tracer item 1

    // WTSRegisterSessionNotification's flags parameter: WTS's NOTIFY_FOR_THIS_SESSION (this session
    // only; the generated interop takes it as a plain uint).
    private const uint NotifyForThisSession = 0;

    // Recovery messages delivered to the raw-input window (T-007). Internal so HookRecoveryTests delivers
    // them exactly as the window procedure receives them, without a real power or session event.
    internal const int PowerBroadcastWindowMessage = 0x0218; // WM_POWERBROADCAST
    internal const int SessionChangeWindowMessage = 0x02B1; // WM_WTSSESSION_CHANGE
    internal static readonly IntPtr PowerBroadcastResumeAutomaticWParam = new(0x0012); // PBT_APMRESUMEAUTOMATIC
    internal static readonly IntPtr SessionUnlockWParam = new(0x8); // WTS_SESSION_UNLOCK

    private readonly bool stallFirstHookCallback;
    private readonly IKeyboardHookInstaller keyboardHookInstaller;
    private readonly Channel<KeyboardHookSignal> keyboardHookSignals = Channel.CreateUnbounded<KeyboardHookSignal>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly TimeProvider timeProvider;
    private HwndSource? rawInputWindow;

    // A one-shot TimeProvider timer (T-001 left this conversion to T-007) whose tick is marshalled back
    // onto the hook thread's dispatcher: reinstalling the hook from any other thread would leave hook
    // callbacks delivered to a message loop that is not running.
    private ITimer? hookLivenessCheckTimer;

    // Touched only on the hook thread.
    private long lastHookCallbackTickMilliseconds;
    private long? pendingRawKeyDownTickMilliseconds;
    private bool hasStalledHookCallback;

    // Touched only by the translation worker in production; the recovery tests drive the same methods
    // synchronously instead of starting the worker.
    private LowLevelKeyboardEventTranslator keyboardEventTranslator = new();
    private PushToTalkDetector pushToTalkDetector;

    /// <summary>Raised on a worker thread; subscribers marshal to their own thread.</summary>
    public event Action<PushToTalkTransition>? ShortcutTransitioned;

    public GlobalPushToTalkShortcutMonitor(PushToTalkShortcutOption shortcutOption, bool stallFirstHookCallback, TimeProvider timeProvider)
        : this(shortcutOption, stallFirstHookCallback, timeProvider, keyboardHookInstallerForTests: null)
    {
    }

    /// <summary>
    /// Recovery-test constructor (T-007): a non-null installer replaces the real SetWindowsHookEx owner so
    /// tests can deliver recovery messages and count reinstalls without installing a system-wide hook.
    /// </summary>
    internal GlobalPushToTalkShortcutMonitor(
        PushToTalkShortcutOption shortcutOption,
        bool stallFirstHookCallback,
        TimeProvider timeProvider,
        IKeyboardHookInstaller? keyboardHookInstallerForTests)
    {
        this.stallFirstHookCallback = stallFirstHookCallback;
        this.timeProvider = timeProvider;
        keyboardHookInstaller = keyboardHookInstallerForTests ?? new WindowsKeyboardHookInstaller(KeyboardHookCallback);
        pushToTalkDetector = new PushToTalkDetector(shortcutOption);
    }

    /// <summary>The channel from the hook thread to the translation worker. Internal so the recovery tests
    /// post and drain signals instead of installing a real hook (T-007).</summary>
    internal Channel<KeyboardHookSignal> KeyboardHookSignals => keyboardHookSignals;

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

    /// <summary>
    /// Hot-swaps the shortcut option without re-creating the hook (T-007): the new option travels on the
    /// translator channel and applies from the next key event. The settings layer calls this when the user
    /// changes the shortcut; nothing here touches the installed hook or its thread.
    /// </summary>
    public void ChangeShortcut(PushToTalkShortcutOption newShortcutOption) =>
        keyboardHookSignals.Writer.TryWrite(KeyboardHookSignal.ShortcutOptionChangedTo(newShortcutOption));

    /// <summary>Posts one raw key event onto the translator channel. Internal so the recovery tests inject
    /// key events exactly where the hook callback would (T-007).</summary>
    internal void PostRawKeyboardEvent(LowLevelKeyboardEvent keyboardEvent) =>
        keyboardHookSignals.Writer.TryWrite(KeyboardHookSignal.FromRawKeyboardEvent(keyboardEvent));

    /// <summary>
    /// Handles one signal from the translator channel: one key event, a post-reinstall reset, or a shortcut
    /// hot-swap. Internal so the recovery tests drive the same per-signal code the worker runs (T-007).
    /// </summary>
    internal void ProcessKeyboardHookSignal(KeyboardHookSignal keyboardHookSignal)
    {
        switch (keyboardHookSignal.Kind)
        {
            case KeyboardHookSignalKind.HookWasReinstalled:
                // Key-ups may have been lost while the hook was dead. A left Ctrl stuck "down" would make a
                // lone Alt press (Alt+Tab) trigger push-to-talk, so start from all keys up.
                keyboardEventTranslator.Reset();
                pushToTalkDetector.Reset();
                return;
            case KeyboardHookSignalKind.ShortcutOptionChanged:
                // The next key event is judged by the new option; the old option's held-press state is
                // dropped, so changing the shortcut mid-hold cannot leave a phantom press behind.
                pushToTalkDetector = new PushToTalkDetector(keyboardHookSignal.ShortcutOption);
                ClickyLog.Write($"shortcut changed to {keyboardHookSignal.ShortcutOption}");
                return;
            case KeyboardHookSignalKind.RawKeyboardEvent:
                if (keyboardEventTranslator.Translate(keyboardHookSignal.KeyboardEvent) is not NeutralKeyEvent neutralKeyEvent)
                {
                    return;
                }
                PushToTalkTransition shortcutTransition = pushToTalkDetector.Process(neutralKeyEvent);
                if (shortcutTransition != PushToTalkTransition.None)
                {
                    // Privacy rule (T-007): the log records the transition only. Virtual-key and scan codes
                    // must never reach a log line, because the log ships inside crash reports.
                    ClickyLog.Write(shortcutTransition == PushToTalkTransition.Pressed ? "ptt pressed" : "ptt released");
                    ShortcutTransitioned?.Invoke(shortcutTransition);
                }
                return;
        }
    }

    private void RunHookThread(TaskCompletionSource hookInstallation)
    {
        try
        {
            keyboardHookInstaller.InstallKeyboardHook();
            lastHookCallbackTickMilliseconds = Environment.TickCount64;
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

    private unsafe LRESULT KeyboardHookCallback(int hookCode, WPARAM keyboardMessage, LPARAM keyboardHookData)
    {
        if (hookCode >= 0)
        {
            lastHookCallbackTickMilliseconds = Environment.TickCount64;
            var keyboardEvent = (KBDLLHOOKSTRUCT*)keyboardHookData.Value;
            bool isKeyUp = (keyboardEvent->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_UP) != 0;
            // O(1): enqueue and return. Anything slower here risks Windows removing the hook.
            PostRawKeyboardEvent(new LowLevelKeyboardEvent(keyboardEvent->vkCode, keyboardEvent->scanCode, isKeyUp));

            if (stallFirstHookCallback && !hasStalledHookCallback)
            {
                hasStalledHookCallback = true;
                StallLongerThanLowLevelHooksTimeout();
            }
        }
        // Listen-only: every key always continues down the hook chain, so typing is never swallowed.
        return PInvoke.CallNextHookEx(keyboardHookInstaller.CurrentKeyboardHookHandle, hookCode, keyboardMessage, keyboardHookData);
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

        // Resume and unlock notifications are broadcast to top-level windows, which a message-only window
        // never is, so the watchdog window registers for both directly (T-007): Windows then targets it
        // with WM_POWERBROADCAST / WM_WTSSESSION_CHANGE like any other registered recipient.
        PInvoke.RegisterSuspendResumeNotification(
            (HWND)rawInputWindow.Handle, REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_WINDOW_HANDLE);
        if (!PInvoke.WTSRegisterSessionNotification((HWND)rawInputWindow.Handle, NotifyForThisSession))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSRegisterSessionNotification failed");
        }

        // Created disarmed; each first raw key-down arms it once (see HandleRawInputWindowMessage).
        Dispatcher hookThreadDispatcher = Dispatcher.CurrentDispatcher;
        hookLivenessCheckTimer = timeProvider.CreateTimer(
            _ => hookThreadDispatcher.InvokeAsync(CheckHookLiveness),
            state: null,
            dueTime: Timeout.InfiniteTimeSpan,
            period: Timeout.InfiniteTimeSpan);
    }

    private IntPtr RawInputWindowProcedure(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        HandleRawInputWindowMessage(message, wParam, lParam);
        // Nothing is marked handled: WM_INPUT must reach DefWindowProc so Windows can free the input data,
        // and the power and session notifications need no special return value.
        return IntPtr.Zero;
    }

    /// <summary>
    /// One message delivered to the raw-input window: WM_INPUT liveness tracking, plus the two recovery
    /// messages (T-007). Internal so HookRecoveryTests delivers WM_POWERBROADCAST and WM_WTSSESSION_CHANGE
    /// through the same entry point the OS reaches.
    /// </summary>
    internal void HandleRawInputWindowMessage(int message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WindowMessageInput && pendingRawKeyDownTickMilliseconds is null && IsRawKeyDown(lParam))
        {
            pendingRawKeyDownTickMilliseconds = Environment.TickCount64;
            hookLivenessCheckTimer!.Change(TimeSpan.FromMilliseconds(HookLivenessWindowMilliseconds), Timeout.InfiniteTimeSpan);
            return;
        }
        if (message == PowerBroadcastWindowMessage && wParam == PowerBroadcastResumeAutomaticWParam)
        {
            // Across sleep the hook can be silently dropped, so the first press after wake may never arrive
            // (PTT-07: the shortcut must survive idle time and resume). Reinstall from a clean key state.
            ReinstallHookAndResetTranslation("hook reinstalled after resume from sleep");
            return;
        }
        if (message == SessionChangeWindowMessage && wParam == SessionUnlockWParam)
        {
            // Lock/unlock switches desktops, which re-homes low-level hooks unreliably; same recovery.
            ReinstallHookAndResetTranslation("hook reinstalled after session unlock");
        }
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

        // The exact wording is PTT-06's evidence contract with QA, matched by HookSendInputSmokeTests.
        ReinstallHookAndResetTranslation("watchdog: hook reinstalled");
    }

    /// <summary>Reinstalls the hook and posts the translator-channel reset that forgets held keys.</summary>
    private void ReinstallHookAndResetTranslation(string reinstallLogMessage)
    {
        keyboardHookInstaller.InstallKeyboardHook();
        // A freshly installed hook is alive by definition, so the liveness clock restarts here; otherwise
        // the very next keystroke would look like the hook missed it and trigger a second reinstall.
        lastHookCallbackTickMilliseconds = Environment.TickCount64;
        keyboardHookSignals.Writer.TryWrite(KeyboardHookSignal.HookWasReinstalled());
        ClickyLog.Write(reinstallLogMessage);
    }

    private async Task TranslateKeyboardHookSignalsAsync()
    {
        await foreach (KeyboardHookSignal keyboardHookSignal in keyboardHookSignals.Reader.ReadAllAsync())
        {
            ProcessKeyboardHookSignal(keyboardHookSignal);
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
}
