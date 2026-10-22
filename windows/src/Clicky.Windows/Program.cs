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

    [STAThread]
    private static int Main(string[] commandLineArguments)
    {
        // Velopack requires this call before any other code in the process: it handles the
        // installer/update hook arguments (e.g. --veloapp-install) and may exit right here,
        // before the WPF Application exists. It must stay the first statement of Main;
        // PackagingWorkflowTests.VelopackIsFirstLineOfMain pins that.
        VelopackApp.Build().Run();

        ClickyCommandLineOptions options;
        ClickySettings settings;
        string systemPrompt;
        Uri chatEndpoint;
        try
        {
            options = ClickyCommandLineOptions.Parse(commandLineArguments);
            settings = ClickySettings.LoadFromApplicationDirectory();
            systemPrompt = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "prompts", "companion-voice-response.txt"));
            chatEndpoint = options.IsReplay
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
            application.Shutdown(report.ExitCode);
            return;
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
}
