using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-007 / PTT-06, the automated half of the manual QA evidence: the real Clicky.exe runs live with
/// --debug-stall-hook-once, SendInput presses fire the shortcut, and the app log must show
/// "hook stalled (debug)", "watchdog: hook reinstalled" and "ptt pressed" in that order. Tagged
/// "Desktop" and skipped wherever no interactive input desktop exists (a session-0 agent cannot
/// inject keystrokes); the skip is the designed behavior, not a failure.
/// </summary>
[Trait("Category", "Desktop")]
[Collection(ClickyProcessReplayCollection.Name)] // launches Clicky.exe and injects real input: serialized with the other process suites
public sealed class HookSendInputSmokeTests
{
    private static readonly TimeSpan StartupWaitLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EvidenceDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TimeBetweenPressCycles = TimeSpan.FromMilliseconds(800);

    private const uint VirtualKeyLeftControl = 0xA2;
    private const uint VirtualKeyLeftAlt = 0xA4;
    private const uint DesktopReadObjectsAccess = 0x0001;
    private const uint InputTypeKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    [Fact]
    public async Task StalledHookIsWatchdogReinstalledAndPressesAgain()
    {
        if (!HasInteractiveInputDesktop())
        {
            // Runtime skip (not [Fact(Skip)]): the trait stays "Desktop" and the test still runs
            // wherever a real input desktop exists, e.g. the interactive CI runner.
            Assert.Skip("no interactive input desktop is available, so SendInput cannot fire shortcut presses");
        }

        string liveCopyDirectoryPath = StageLiveClickyCopyWithLoopbackWorker();
        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        try
        {
            using Process liveClickyProcess = StartLiveClicky(liveCopyDirectoryPath, dataDirectoryPath);
            Task<string> liveStandardOutputText = liveClickyProcess.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            Task<string> liveStandardErrorText = liveClickyProcess.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

            // Wait until the hook is really installed before injecting input, so the stall lands on a
            // known keystroke instead of on whatever noise precedes startup.
            var startupWaitLimit = DateTime.UtcNow + StartupWaitLimit;
            string logText = ReadEveryLogFile(dataDirectoryPath);
            while (DateTime.UtcNow < startupWaitLimit
                && !liveClickyProcess.HasExited
                && !logText.Contains("live: listening", StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
                logText = ReadEveryLogFile(dataDirectoryPath);
            }
            Assert.True(logText.Contains("live: listening", StringComparison.Ordinal),
                $"Clicky.exe never reached live mode. Log:\n{logText}\nOutput:\n{await liveStandardOutputText}");

            bool sawAllRecoveryEvidence = false;
            using var evidenceDeadlineCancellation = new CancellationTokenSource(EvidenceDeadline);
            try
            {
                while (!evidenceDeadlineCancellation.IsCancellationRequested && !liveClickyProcess.HasExited)
                {
                    await SendShortcutPressCycleAsync(TestContext.Current.CancellationToken);
                    logText = ReadEveryLogFile(dataDirectoryPath);
                    if (LogShowsRecoveryEvidenceInOrder(logText))
                    {
                        sawAllRecoveryEvidence = true;
                        break;
                    }
                    await Task.Delay(TimeBetweenPressCycles, evidenceDeadlineCancellation.Token);
                }
            }
            catch (OperationCanceledException) when (evidenceDeadlineCancellation.IsCancellationRequested)
            {
                // Deadline hit: fall through so the assertions report the log as evidence.
            }

            if (!liveClickyProcess.HasExited)
            {
                liveClickyProcess.Kill(entireProcessTree: true);
            }
            await liveClickyProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
            string liveProcessOutput = await liveStandardOutputText + await liveStandardErrorText;

            Assert.True(sawAllRecoveryEvidence,
                $"hook recovery evidence missing or out of order within {EvidenceDeadline.TotalSeconds} s. Log:\n{logText}\nOutput:\n{liveProcessOutput}");
        }
        finally
        {
            Directory.Delete(liveCopyDirectoryPath, recursive: true);
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
    }

    /// <summary>
    /// Live mode refuses to start without a WorkerBaseUrl, and the smoke test must never reach the real
    /// Worker. Port 9 on loopback refuses every connection instantly, so each press after recovery runs a
    /// real interaction that fails fast and is logged, while the shortcut path under test runs for real.
    /// The app cannot be pointed at a different appsettings.json in place, so the whole output directory
    /// is copied and only the copy's settings are rewritten.
    /// </summary>
    private static string StageLiveClickyCopyWithLoopbackWorker()
    {
        Assert.True(File.Exists(ClickyReplayProcess.ClickyExecutablePath),
            $"Build Clicky.Windows first; not found: {ClickyReplayProcess.ClickyExecutablePath}");
        string sourceExecutableDirectoryPath = Path.GetDirectoryName(ClickyReplayProcess.ClickyExecutablePath)!;
        string liveCopyDirectoryPath = Path.Combine(Path.GetTempPath(), $"clicky-live-{Guid.NewGuid():N}");
        CopyDirectory(sourceExecutableDirectoryPath, liveCopyDirectoryPath);

        string liveSettingsFilePath = Path.Combine(liveCopyDirectoryPath, "appsettings.json");
        JsonNode liveSettings = JsonNode.Parse(File.ReadAllText(liveSettingsFilePath))!;
        liveSettings["WorkerBaseUrl"] = "http://127.0.0.1:9/";
        File.WriteAllText(liveSettingsFilePath, liveSettings.ToJsonString());
        return liveCopyDirectoryPath;
    }

    private static Process StartLiveClicky(string liveCopyDirectoryPath, string dataDirectoryPath)
    {
        var liveClickyStartInfo = new ProcessStartInfo(Path.Combine(liveCopyDirectoryPath, "Clicky.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        liveClickyStartInfo.Environment["CLICKY_DATA_DIR"] = dataDirectoryPath;
        liveClickyStartInfo.ArgumentList.Add("--debug-stall-hook-once");
        return Process.Start(liveClickyStartInfo)!;
    }

    /// <summary>
    /// The order PBT-06 requires: the debug stall first, then the watchdog noticing the dead hook and
    /// reinstalling it, then a shortcut press that lands on the reinstalled hook.
    /// </summary>
    private static bool LogShowsRecoveryEvidenceInOrder(string logText)
    {
        int stallLineIndex = logText.IndexOf("hook stalled (debug)", StringComparison.Ordinal);
        int reinstallLineIndex = logText.IndexOf("watchdog: hook reinstalled", StringComparison.Ordinal);
        int pressedLineIndex = logText.IndexOf("ptt pressed", StringComparison.Ordinal);
        return stallLineIndex >= 0 && reinstallLineIndex > stallLineIndex && pressedLineIndex > reinstallLineIndex;
    }

    /// <summary>
    /// One shortcut tap: Ctrl down, Alt down (the press), then Ctrl up (the release) and Alt up. Ctrl is
    /// lifted first so the shortcut releases on it, and holding Ctrl through the Alt release keeps the
    /// tap from opening menus in whatever app happens to have focus.
    /// </summary>
    private static async Task SendShortcutPressCycleAsync(CancellationToken testCancellationToken)
    {
        SendKeyEvent(VirtualKeyLeftControl, isKeyUp: false);
        SendKeyEvent(VirtualKeyLeftAlt, isKeyUp: false);
        await Task.Delay(TimeSpan.FromMilliseconds(150), testCancellationToken);
        SendKeyEvent(VirtualKeyLeftControl, isKeyUp: true);
        SendKeyEvent(VirtualKeyLeftAlt, isKeyUp: true);
        await Task.Delay(TimeSpan.FromMilliseconds(150), testCancellationToken);
    }

    private static void SendKeyEvent(uint virtualKeyCode, bool isKeyUp)
    {
        var injectedKeyEvent = new SENDINPUT_KEYBOARD_EVENT
        {
            InputType = InputTypeKeyboard,
            KeyboardInput = new KEYBDINPUT
            {
                VirtualKey = (ushort)virtualKeyCode,
                Flags = isKeyUp ? KeyEventKeyUp : 0,
            },
        };
        uint injectedEventCount = SendInput(1, [injectedKeyEvent], Marshal.SizeOf<SENDINPUT_KEYBOARD_EVENT>());
        Assert.True(injectedEventCount == 1,
            $"SendInput rejected the key event for vk {virtualKeyCode} (Win32 error {Marshal.GetLastWin32Error()})");
    }

    /// <summary>
    /// True where keystrokes can actually be injected: an interactive session with an open input
    /// desktop. OpenInputDesktop fails on session-0 agents and disconnected sessions, which is exactly
    /// where this smoke test must skip instead of fail.
    /// </summary>
    private static bool HasInteractiveInputDesktop()
    {
        if (!Environment.UserInteractive)
        {
            return false;
        }
        IntPtr inputDesktopHandle = OpenInputDesktop(0, inheritHandle: false, desiredAccess: DesktopReadObjectsAccess);
        if (inputDesktopHandle == IntPtr.Zero)
        {
            return false;
        }
        CloseDesktop(inputDesktopHandle);
        return true;
    }

    private static string ReadEveryLogFile(string dataDirectoryPath)
    {
        string logsDirectoryPath = Path.Combine(dataDirectoryPath, "logs");
        if (!Directory.Exists(logsDirectoryPath))
        {
            return "";
        }
        return string.Join(Environment.NewLine,
            Directory.GetFiles(logsDirectoryPath, "clicky-*.log").Select(File.ReadAllText));
    }

    private static void CopyDirectory(string sourceDirectoryPath, string destinationDirectoryPath)
    {
        Directory.CreateDirectory(destinationDirectoryPath);
        foreach (string sourceFilePath in Directory.GetFiles(sourceDirectoryPath))
        {
            File.Copy(sourceFilePath, Path.Combine(destinationDirectoryPath, Path.GetFileName(sourceFilePath)));
        }
        foreach (string sourceSubdirectoryPath in Directory.GetDirectories(sourceDirectoryPath))
        {
            CopyDirectory(sourceSubdirectoryPath, Path.Combine(destinationDirectoryPath, Path.GetFileName(sourceSubdirectoryPath)));
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputEventCount, SENDINPUT_KEYBOARD_EVENT[] inputEvents, int inputEventStructureSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inheritHandle, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktopObjectHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SENDINPUT_KEYBOARD_EVENT
    {
        public uint InputType;
        public KEYBDINPUT KeyboardInput;

        // KEYBDINPUT is not the largest member of the native INPUT union (MOUSEINPUT is, by two
        // pointers): this padding makes the marshalled size match sizeof(INPUT), which SendInput checks.
        private readonly IntPtr unionPadding1;
        private readonly IntPtr unionPadding2;
    }
}
