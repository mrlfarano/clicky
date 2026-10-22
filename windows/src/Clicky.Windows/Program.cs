using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
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
        ClickyUserSettingsStore userSettingsStore;
        Uri chatEndpoint;
        ReplayManifest? replayManifest;
        ReplayScript? replayScript;
        try
        {
            options = ClickyCommandLineOptions.Parse(commandLineArguments);
            settings = ClickySettings.LoadFromApplicationDirectory();
            // The user's settings load right after the ship-time file so the app has one merged view from
            // the first line of startup (ONB-04): defaults <- <data directory>\settings.json. A corrupt
            // file is not fatal — the merger returns every default and flags it, and the next settings
            // change rewrites the file valid (WS-12).
            userSettingsStore = new ClickyUserSettingsStore();
            CompanionSettingsMergeResult userSettingsMergeResult = userSettingsStore.Load();
            if (userSettingsMergeResult.UserSettingsFileWasCorrupt)
            {
                ClickyLog.Write("settings: user settings file is corrupt; using defaults, the next change rewrites it");
            }
            // The --settings-override replay-harness layer applies over whatever was loaded (defaults <-
            // settings.json) and lands in the report's effectiveSettings section; it is never persisted —
            // only --set-setting writes, through the store.
            effectiveUserSettings = CompanionSettingsOverrideApplier.Apply(userSettingsMergeResult.Settings, options.SettingsOverrides);
            // The model lives only in user settings (T-018 removed the appsettings copy): the loaded value
            // with its claude-sonnet-4-6 default, or the override when a parity scenario pins it (CLA-08).
            effectiveClaudeModel = effectiveUserSettings.SelectedClaudeModel;
            interactionSystemPrompt = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "prompts", "companion-voice-response.txt"));
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
            // endpoint instead of demanding WorkerBaseUrl like a real live session does. --set-setting
            // makes no request either; it only loads and writes the user's settings file.
            chatEndpoint = options.IsReplay || options.DebugCrashRequested || options.UserSettingsChange is not null
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
                    effectiveUserSettings, userSettingsStore, chatEndpoint, replayManifest, replayScript, TimeProvider.System);
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
        ClickyUserSettingsStore userSettingsStore,
        Uri chatEndpoint,
        ReplayManifest? replayManifest,
        ReplayScript? replayScript,
        TimeProvider timeProvider)
    {
        // --set-setting is the tracer stand-in for a panel settings change (the panel lands in WS-18/19):
        // apply exactly one change through the real persistence path — load-merged settings in, atomic
        // write out — and exit, so one process run equals "the app started and one setting changed".
        if (options.UserSettingsChange is ClickyUserSettingsChange requestedUserSettingsChange)
        {
            // --set-setting cannot be combined with --settings-override (parse rejects the mix), so the
            // effective settings here are exactly the loaded ones.
            CompanionSettings updatedUserSettings = requestedUserSettingsChange.ApplyToCompanionSettings(effectiveUserSettings);
            userSettingsStore.Save(updatedUserSettings);
            ClickyLog.Write($"settings: saved {requestedUserSettingsChange.SettingsFileKeyName} to {userSettingsStore.SettingsFilePath}");
            application.Shutdown(exitCode: 0);
            return;
        }

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
            effectiveClaudeModel,
            effectiveUserSettings,
            workerHttpClient,
            chatEndpoint,
            overlayWindow,
            new MonitorScreenCapturer(),
            analyticsSink,
            textToSpeechSink,
            fallbackSpeechSink);
        // The updater reads the coordinator's reducer-driven voice state through this (it only applies while idle).
        var companionVoiceStateReporter = new CompanionVoiceStateReporter();
        var companionCoordinator = new CompanionCoordinator(
            interactionRunner,
            overlayWindow,
            interactionUserPromptText,
            timeProvider,
            analyticsSink,
            textToSpeechSink,
            companionVoiceStateReporter);

        if (options.IsReplay)
        {
            ClickyLog.Write($"replay: {options.ReplayManifestPath ?? options.ReplayFixturePath} (event delay {options.ReplayEventDelay.TotalMilliseconds} ms)");
            if (options.DebugTriggerOnboardingDemo)
            {
                // ONB-02: the demo fires its own analytics event, so a report of a demo run proves the
                // analytics sink recorded it — the stub's counterpart of ClickyAnalytics's onboarding_demo_triggered.
                analyticsSink.TrackEvent("onboarding_demo_triggered");
            }
            TracerRunReport report = await companionCoordinator.RunReplayAsync(replayScript);
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

        var shortcutMonitor = new GlobalPushToTalkShortcutMonitor(
            effectiveUserSettings.PushToTalkShortcut, options.StallFirstHookCallback, timeProvider);
        // The monitor itself logs every transition ("ptt pressed" / "ptt released", T-007): the privacy rule
        // that log lines carry transitions and state but never vk or scan codes lives next to the key events.
        // It raises on a worker thread; Post marshals onto the dispatcher, where the reducer decides what a
        // press or release does (a press cancels the reply in flight, CompanionManager.swift:494-497).
        shortcutMonitor.ShortcutTransitioned += shortcutTransition => companionCoordinator.Post(
            shortcutTransition == PushToTalkTransition.Pressed
                ? new CompanionSessionEvent.ShortcutPressed()
                : new CompanionSessionEvent.ShortcutReleased());
        shortcutMonitor.Start();

        // Live only, like the hook: replay, --set-setting and --debug-crash have all returned above. With
        // UpdateFeedUrl empty (the shipped default) this logs once and makes no update request (WS-10).
        var updateService = new ClickyUpdateService(
            settings.TryGetUpdateFeed(),
            updateFeed => new VelopackUpdateFeedClient(updateFeed),
            companionVoiceStateReporter,
            timeProvider,
            ClickyLog.Write);
        // Task.Run keeps feed checks off the WPF dispatcher; the loop catches and logs its own failures.
        _ = Task.Run(() => updateService.RunUpdateChecksAsync(CancellationToken.None));

        // Live only (replay must stay deterministic): follow the user across virtual desktops (audit E2, T-005).
        // Without the COM class the overlay simply stays on one desktop, the ADR 001 fallback.
        if (WindowsVirtualDesktopService.TryCreate() is WindowsVirtualDesktopService virtualDesktopService)
        {
            var virtualDesktopFollower = new VirtualDesktopFollower(virtualDesktopService, new WindowInteropHelper(overlayWindow).Handle);
            var foregroundWindowChangeHook = new ForegroundWindowChangeHook(virtualDesktopFollower.HandleForegroundWindowChanged);
            // The Exit subscription also keeps the hook (and its native callback delegate) alive for the app's life.
            application.Exit += (_, _) => foregroundWindowChangeHook.Dispose();
        }
        ClickyLog.Write($"live: listening for {effectiveUserSettings.PushToTalkShortcut} (left Ctrl + left Alt by default), chat {chatEndpoint}. Ctrl+C quits.");
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
