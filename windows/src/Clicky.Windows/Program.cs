using System.Text.Json;
using System.Windows;
using Clicky.Core;

namespace Clicky.Windows;

internal static class Program
{
    private const int UsageOrConfigurationErrorExitCode = 2;
    private const int RuntimeFailureExitCode = 1;

    // Replay never touches the network, so it needs no Worker URL; this is what lets CI run with none configured.
    private static readonly Uri ReplayChatEndpoint = new("http://replay.clicky.invalid/chat");

    [STAThread]
    private static int Main(string[] commandLineArguments)
    {
        // Crash reporting goes in before anything else can throw (WS-11): an unhandled exception on
        // any thread must land in crashes\ before the process dies.
        ClickyCrashReporter.InstallOnCurrentAppDomain();

        ClickyCommandLineOptions options;
        ClickySettings settings;
        string systemPrompt;
        Uri chatEndpoint;
        try
        {
            options = ClickyCommandLineOptions.Parse(commandLineArguments);
            settings = ClickySettings.LoadFromApplicationDirectory();
            systemPrompt = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "prompts", "companion-voice-response.txt"));
            // --debug-crash must run with zero configuration on any machine (it is a crash-reporting test
            // hook, T-016), and it crashes before any request is made, so it takes the inert replay
            // endpoint instead of demanding WorkerBaseUrl like a real live session does.
            chatEndpoint = options.IsReplay || options.DebugCrashRequested
                ? ReplayChatEndpoint
                : settings.TryGetChatEndpoint()
                    ?? throw new CommandLineUsageException("Live mode needs WorkerBaseUrl in appsettings.json set to the deployed Worker's https URL.");
        }
        catch (Exception startupException) when (startupException is CommandLineUsageException or InvalidDataException or IOException or JsonException)
        {
            ClickyLog.Write($"startup error: {startupException.Message}");
            Console.Error.WriteLine(ClickyCommandLineOptions.UsageText);
            return UsageOrConfigurationErrorExitCode;
        }

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Dispatcher-thread exceptions surface through this handler while the message loop runs (WS-11
        // names both hooks); the reporter's write-once guard stops the follow-up AppDomain handler
        // from writing a second report when the exception propagates on.
        ClickyCrashReporter.InstallOnApplication(application);
        application.Startup += async (_, _) =>
        {
            try
            {
                await RunAsync(application, options, settings, systemPrompt, chatEndpoint);
            }
            catch (Exception unexpectedException)
            {
                ClickyLog.Write($"fatal: {unexpectedException}");
                application.Shutdown(RuntimeFailureExitCode);
            }
        };
        return application.Run();
    }

    private static async Task RunAsync(
        Application application,
        ClickyCommandLineOptions options,
        ClickySettings settings,
        string systemPrompt,
        Uri chatEndpoint)
    {
        var overlayWindow = new CursorOverlayWindow();
        overlayWindow.CreateHiddenWindowHandle();

        HttpMessageHandler workerMessageHandler = options.ReplayFixturePath is string replayFixturePath
            ? new ReplayHttpMessageHandler(replayFixturePath, options.ReplayEventDelay)
            // Cookies off, as in the Swift URLSession configuration (audit G2).
            : new SocketsHttpHandler { UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(15) };
        // One HttpClient for the life of the app, so connections and TLS sessions are reused across presses.
        var workerHttpClient = new HttpClient(workerMessageHandler) { Timeout = TimeSpan.FromSeconds(120) };

        var interactionRunner = new TracerInteractionRunner(
            settings, options, systemPrompt, workerHttpClient, chatEndpoint, overlayWindow, new MonitorScreenCapturer());

        if (options.IsReplay)
        {
            ClickyLog.Write($"replay: {options.ReplayFixturePath} (event delay {options.ReplayEventDelay.TotalMilliseconds} ms)");
            TracerRunReport report = await interactionRunner.RunAsync(CancellationToken.None);
            // --linger-ms keeps the cursor on screen so QA can photograph where it landed (parity spec PNT-04).
            await Task.Delay(options.LingerBeforeExit);
            if (options.DebugCrashRequested)
            {
                // Crash only after the report is on disk, so one run can both replay an interaction and
                // exercise crash reporting (T-016's no-response-text-in-crash-files test does exactly that).
                RaiseDeliberateDebugCrash();
            }
            application.Shutdown(report.ExitCode);
            return;
        }

        if (options.DebugCrashRequested)
        {
            // Crash before the global keyboard hook is installed: --debug-crash exists to exercise crash
            // reporting (T-016), not the hook, and touching no real input plumbing keeps the run
            // deterministic on headless CI runners.
            RaiseDeliberateDebugCrash();
        }

        var shortcutMonitor = new GlobalPushToTalkShortcutMonitor(settings.PushToTalkShortcut, options.StallFirstHookCallback);
        CancellationTokenSource? currentInteractionCancellation = null;
        shortcutMonitor.ShortcutTransitioned += shortcutTransition => application.Dispatcher.InvokeAsync(async () =>
        {
            if (shortcutTransition == PushToTalkTransition.Released)
            {
                // The tracer bullet has no recording to finalize; the release is logged for ADR 001 acceptance 4.
                ClickyLog.Write("ptt released");
                return;
            }
            ClickyLog.Write("ptt pressed");
            // A new press cancels the interaction still in flight, as on macOS (CompanionManager.swift:494-497).
            currentInteractionCancellation?.Cancel();
            var interactionCancellation = new CancellationTokenSource();
            currentInteractionCancellation = interactionCancellation;
            try
            {
                await interactionRunner.RunAsync(interactionCancellation.Token);
            }
            catch (OperationCanceledException) when (interactionCancellation.IsCancellationRequested)
            {
                ClickyLog.Write("interaction cancelled by a newer press");
            }
        });
        shortcutMonitor.Start();
        ClickyLog.Write($"live: listening for {settings.PushToTalkShortcut} (left Ctrl + left Alt by default), chat {chatEndpoint}. Ctrl+C quits.");
    }

    /// <summary>
    /// The --debug-crash hook (WS-11/T-016): raises a real unhandled exception on a dedicated thread so
    /// the crash reporter runs exactly as it would for an unexpected failure. A dedicated thread keeps
    /// the propagation out of WPF's async-void Startup plumbing, whose path through
    /// DispatcherUnhandledException is hard to reason about; the main thread joins it so the process
    /// cannot exit cleanly before the crash report has been written.
    /// </summary>
    private static void RaiseDeliberateDebugCrash()
    {
        ClickyLog.Write("debug crash: raising the deliberate unhandled exception requested by --debug-crash");
        var debugCrashThread = new Thread(() => throw new DeliberateDebugCrashException(
            "Deliberate unhandled exception raised by --debug-crash so crash reporting can be exercised on demand."))
        {
            Name = "clicky-debug-crash",
        };
        debugCrashThread.Start();
        // Never returns: the unhandled exception terminates the process while the main thread waits here.
        debugCrashThread.Join();
    }
}
