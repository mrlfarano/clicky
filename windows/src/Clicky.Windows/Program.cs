using System.Text.Json;
using System.Windows;
using Clicky.Core;
using Velopack;

namespace Clicky.Windows;

internal static class Program
{
    private const int UsageOrConfigurationErrorExitCode = 2;
    private const int RuntimeFailureExitCode = 1;

    // Replay never touches the network, so it needs no Worker URL; this is what lets CI run with none configured.
    private static readonly Uri ReplayChatEndpoint = new("http://replay.clicky.invalid/chat");

    /// <summary>
    /// The onboarding demo's fixed user text (ONB-02, CompanionManager.swift:990): the demo asks Claude to
    /// look around and pick something to point at, unprompted, while the intro video plays.
    /// </summary>
    private const string OnboardingDemoUserPromptText = "look around my screen and find something interesting to point at";

    [STAThread]
    private static int Main(string[] commandLineArguments)
    {
        // Velopack requires this call before any other code in the process: it handles the
        // installer/update hook arguments (e.g. --veloapp-install) and may exit right here,
        // before the WPF Application exists. It must stay the first statement of Main;
        // PackagingWorkflowTests.VelopackIsFirstLineOfMain pins that.
        VelopackApp.Build().Run();

        // Crash reporting goes in as the very next step, before anything else can throw
        // (WS-11): an unhandled exception on any thread must land in crashes\ before the
        // process dies. It sits after Velopack only because Velopack legitimately exits the
        // process here for installer-hook arguments, before any of our code runs.
        ClickyCrashReporter.InstallOnCurrentAppDomain();

        ClickyCommandLineOptions options;
        ClickySettings settings;
        string interactionSystemPrompt;
        string interactionUserPromptText;
        string effectiveClaudeModel;
        CompanionSettings effectiveUserSettings;
        Uri chatEndpoint;
        ReplayManifest? replayManifest;
        ReplayScript? replayScript;
        try
        {
            options = ClickyCommandLineOptions.Parse(commandLineArguments);
            settings = ClickySettings.LoadFromApplicationDirectory();
            interactionSystemPrompt = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "prompts", "companion-voice-response.txt"));
            // The user settings the app loaded: %LOCALAPPDATA%\Clicky\settings.json merged over the ONB-04
            // defaults when that file exists (Core's merger, T-017). T-018 (WS-12) adds writing it; reading
            // it here already keeps --settings-override semantics complete — the override layer applies over
            // whatever was loaded, and both land in the report's effectiveSettings section.
            string userSettingsFilePath = Path.Combine(ClickyDataDirectory.RootPath, "settings.json");
            string? userSettingsFileContents = File.Exists(userSettingsFilePath) ? File.ReadAllText(userSettingsFilePath) : null;
            CompanionSettings loadedUserSettings =
                CompanionSettingsMerger.MergeUserSettingsFileContentsOverDefaults(userSettingsFileContents).Settings;
            effectiveUserSettings = CompanionSettingsOverrideApplier.Apply(loadedUserSettings, options.SettingsOverrides);
            // The tracer's model comes from appsettings.json; a selectedClaudeModel override (parity spec
            // CLA-08) wins over it, because the override exists to pin the model a scenario asserts on.
            effectiveClaudeModel = options.SettingsOverrides.Any(
                overridePair => overridePair.SettingsFileKeyName == CompanionSettingsMerger.SelectedClaudeModelSettingsFileKeyName)
                ? effectiveUserSettings.SelectedClaudeModel
                : settings.ClaudeModel;
            // --debug-trigger-onboarding-demo runs the ONB-02 demo interaction instead of the tracer prompt:
            // the demo system prompt (shared/prompts/onboarding-demo.txt) and the demo's fixed user text.
            if (options.DebugTriggerOnboardingDemo)
            {
                interactionSystemPrompt = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "prompts", "onboarding-demo.txt"));
                interactionUserPromptText = OnboardingDemoUserPromptText;
            }
            else
            {
                interactionUserPromptText = settings.TracerPromptText;
            }
            // --debug-crash must run with zero configuration on any machine (it is a crash-reporting test
            // hook, T-016), and it crashes before any request is made, so it takes the inert replay
            // endpoint instead of demanding WorkerBaseUrl like a real live session does.
            chatEndpoint = options.IsReplay || options.DebugCrashRequested
                ? ReplayChatEndpoint
                : settings.TryGetChatEndpoint()
                    ?? throw new CommandLineUsageException("Live mode needs WorkerBaseUrl in appsettings.json set to the deployed Worker's https URL.");
            // Both replay modes funnel into one manifest, so --replay and --replay-manifest share the same
            // fail-closed routing: a route the manifest does not map can never reach the live Worker.
            replayManifest = options.ReplayManifestPath is string replayManifestFilePath
                ? ReplayManifest.LoadFrom(replayManifestFilePath)
                : options.ReplayFixturePath is string replayFixturePath
                    ? ReplayManifest.CreateForSingleChatFixture(replayFixturePath)
                    : null;
            replayScript = options.ScriptPath is string scriptFilePath ? ReplayScript.LoadFrom(scriptFilePath) : null;
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
                await RunAsync(
                    application, options, settings, interactionSystemPrompt, interactionUserPromptText, effectiveClaudeModel,
                    effectiveUserSettings, chatEndpoint, replayManifest, replayScript, TimeProvider.System);
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
        string interactionSystemPrompt,
        string interactionUserPromptText,
        string effectiveClaudeModel,
        CompanionSettings effectiveUserSettings,
        Uri chatEndpoint,
        ReplayManifest? replayManifest,
        ReplayScript? replayScript,
        TimeProvider timeProvider)
    {
        var overlayWindow = new CursorOverlayWindow();
        overlayWindow.CreateHiddenWindowHandle();

        HttpMessageHandler workerMessageHandler = replayManifest is not null
            ? new ReplayHttpMessageHandler(replayManifest, options.ReplayEventDelay, timeProvider)
            // Cookies off, as in the Swift URLSession configuration (audit G2).
            : new SocketsHttpHandler { UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(15) };
        // One HttpClient for the life of the app, so connections and TLS sessions are reused across presses.
        var workerHttpClient = new HttpClient(workerMessageHandler) { Timeout = TimeSpan.FromSeconds(120) };

        // The T-002 in-memory sinks record what the interaction outputs; replay (and live, until the real
        // sinks land) thereby answers nothing to PostHog and opens no audio device.
        var analyticsSink = new InMemoryAnalyticsSink();
        var textToSpeechSink = new InMemoryTextToSpeechSink();
        var fallbackSpeechSink = new InMemoryFallbackSpeechSink();

        var interactionRunner = new TracerInteractionRunner(
            options,
            interactionSystemPrompt,
            interactionUserPromptText,
            effectiveClaudeModel,
            effectiveUserSettings,
            replayScript,
            workerHttpClient,
            chatEndpoint,
            overlayWindow,
            new MonitorScreenCapturer(),
            timeProvider,
            analyticsSink,
            textToSpeechSink,
            fallbackSpeechSink);

        if (options.IsReplay)
        {
            ClickyLog.Write($"replay: {options.ReplayManifestPath ?? options.ReplayFixturePath} (event delay {options.ReplayEventDelay.TotalMilliseconds} ms)");
            if (options.DebugTriggerOnboardingDemo)
            {
                // ONB-02: the demo fires its own analytics event, so a report of a demo run proves the
                // analytics sink recorded it — the stub's counterpart of ClickyAnalytics's onboarding_demo_triggered.
                analyticsSink.TrackEvent("onboarding_demo_triggered");
            }
            TracerRunReport report = await interactionRunner.RunAsync(CancellationToken.None);
            // --linger-ms keeps the cursor on screen so QA can photograph where it landed (parity spec PNT-04).
            // The TimeProvider overload (not the bare Task.Delay) is what lets a fake clock fire this.
            await Task.Delay(options.LingerBeforeExit, timeProvider);
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
