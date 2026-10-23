using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Clicky.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-039 (WS-22 part 2): parity spec ONB-01 and ONB-05 without the video the owner dropped on Windows. The panel
/// commands run against the real settings store on a temp directory, FormSpark is a fake HTTP handler, every timer
/// runs on FakeTimeProvider and the music plays through the silent output, so no test opens a window or makes a sound.
/// </summary>
public sealed class OnboardingFlowTests
{
    [Fact]
    public async Task EmailPostsFormSparkAndIdentifies()
    {
        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        try
        {
            var formSparkHandler = new RecordingFormSparkHandler { ResponseStatusCode = HttpStatusCode.InternalServerError };
            var analyticsSink = new RecordingAnalyticsSink();
            OnboardingFlow onboardingFlow = CreateOnboardingFlow(new FakeTimeProvider(), analyticsSink, formSparkHandler, _ => { });
            PanelViewModel panelViewModel = CreatePanelViewModel(dataDirectoryPath, CompanionSettings.Defaults, onboardingFlow);
            panelViewModel.Refresh(hasMicrophonePermission: true, isOverlayVisible: false, CompanionVoiceState.Idle);
            Assert.True(panelViewModel.IsEmailStepVisible);

            // ONB-01: a blank value is rejected before anything is sent.
            panelViewModel.EmailInput = " \t\n ";
            Assert.False(panelViewModel.SubmitEmailCommand.CanExecute(null));
            Assert.False(await onboardingFlow.SubmitEmailAsync(" \t\n "));
            Assert.Empty(formSparkHandler.RecordedRequests);

            // A failed POST persists nothing and identifies no one, so the email step stays up to retry.
            panelViewModel.EmailInput = "  user@example.com\n";
            Assert.True(panelViewModel.SubmitEmailCommand.CanExecute(null));
            await panelViewModel.SubmitEmailCommand.ExecuteAsync(null);
            Assert.Single(formSparkHandler.RecordedRequests);
            Assert.False(new ClickyUserSettingsStore(dataDirectoryPath).Load().Settings.HasSubmittedEmail);
            Assert.Empty(analyticsSink.RecordedIdentifies);
            Assert.True(panelViewModel.IsEmailStepVisible);

            formSparkHandler.ResponseStatusCode = HttpStatusCode.OK;
            await panelViewModel.SubmitEmailCommand.ExecuteAsync(null);

            // CompanionManager.swift:165-171: a JSON POST of the trimmed email to FormSpark.
            (HttpMethod method, Uri requestUri, string? contentType, string body) = formSparkHandler.RecordedRequests[^1];
            Assert.Equal(HttpMethod.Post, method);
            Assert.Equal(new Uri("https://submit-form.com/RWbGJxmIs"), requestUri);
            Assert.Equal("application/json", contentType);
            Assert.True(JsonNode.DeepEquals(new JsonObject { ["email"] = "user@example.com" }, JsonNode.Parse(body)), body);

            // CompanionManager.swift:161-163: identify(email, {email}).
            AnalyticsIdentify identify = Assert.Single(analyticsSink.RecordedIdentifies);
            Assert.Equal("user@example.com", identify.DistinctId);
            Assert.True(JsonNode.DeepEquals(new JsonObject { ["email"] = "user@example.com" }, identify.PersonProperties));

            Assert.True(new ClickyUserSettingsStore(dataDirectoryPath).Load().Settings.HasSubmittedEmail);
            Assert.False(panelViewModel.IsEmailStepVisible);
            Assert.True(panelViewModel.IsStartStepVisible);
        }
        finally
        {
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public void StartPersistsAndFiresEvents()
    {
        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        try
        {
            var fakeTimeProvider = new FakeTimeProvider();
            var analyticsSink = new RecordingAnalyticsSink();
            var musicAudioOutput = new SilentReplayAudioOutput(fakeTimeProvider);
            var onboardingFlow = new OnboardingFlow(
                new HttpClient(new RecordingFormSparkHandler()), analyticsSink, new OnboardingMusicPlayer(musicAudioOutput, fakeTimeProvider),
                _ => { }, fakeTimeProvider);
            CompanionSettings emailSubmittedSettings = CompanionSettings.Defaults with { HasSubmittedEmail = true };
            PanelViewModel panelViewModel = CreatePanelViewModel(dataDirectoryPath, emailSubmittedSettings, onboardingFlow);
            panelViewModel.Refresh(hasMicrophonePermission: true, isOverlayVisible: false, CompanionVoiceState.Idle);
            Assert.True(panelViewModel.IsStartStepVisible);

            // Wired as Program.cs wires it: the flow's dismiss request hides the open panel.
            var panelLogLines = new List<string>();
            var panelVisibility = new CompanionPanelVisibility(
                () => { }, () => { }, () => MicrophonePermissionState.Granted, panelLogLines.Add, fakeTimeProvider);
            onboardingFlow.PanelDismissRequested += () => panelVisibility.HidePanel(PanelHideReason.Onboarding);
            panelVisibility.ShowPanel();

            panelViewModel.StartOnboardingCommand.Execute(null);

            // CompanionManager.swift:201-218: panel dismissed, hasCompletedOnboarding persisted, onboarding_started, music.
            Assert.False(panelVisibility.IsPanelOpen);
            Assert.Equal(["panel hidden reason=onboarding"], panelLogLines);
            Assert.Equal(emailSubmittedSettings with { HasCompletedOnboarding = true }, new ClickyUserSettingsStore(dataDirectoryPath).Load().Settings);
            Assert.Equal(["onboarding_started"], analyticsSink.RecordedEvents.Select(analyticsEvent => analyticsEvent.Name));
            Assert.True(musicAudioOutput.IsPlaying, "the onboarding music did not start");
            Assert.Equal(OnboardingMusicPlayer.StartVolume, musicAudioOutput.Volume);
            Assert.True(onboardingFlow.IsOnboardingShowing);

            // The next time the panel shows, setup is done and Watch Onboarding Again is offered.
            panelViewModel.Refresh(hasMicrophonePermission: true, isOverlayVisible: true, CompanionVoiceState.Idle);
            Assert.False(panelViewModel.IsStartStepVisible);
            Assert.True(panelViewModel.IsSetupComplete);
            Assert.True(panelViewModel.IsWatchOnboardingAgainVisible);
        }
        finally
        {
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task PttIgnoredWhileOnboarding()
    {
        var fakeTimeProvider = new FakeTimeProvider();
        var analyticsSink = new RecordingAnalyticsSink();
        var setupReady = new TaskCompletionSource<(CompanionCoordinator Coordinator, OnboardingFlow Flow, Dispatcher UiDispatcher)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            try
            {
                CompanionCoordinator coordinator = CoordinatorReplayTests.CreateInProcessCoordinator(analyticsSink);
                OnboardingFlow onboardingFlow = CreateOnboardingFlow(fakeTimeProvider, analyticsSink, new RecordingFormSparkHandler(), _ => { });
                coordinator.IsOnboardingShowing = () => onboardingFlow.IsOnboardingShowing;
                setupReady.SetResult((coordinator, onboardingFlow, Dispatcher.CurrentDispatcher));
            }
            catch (Exception setupException)
            {
                setupReady.SetException(setupException);
                return;
            }
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "clicky-onboarding-ui-tests" };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();

        (CompanionCoordinator coordinator, OnboardingFlow onboardingFlow, Dispatcher uiDispatcher) = await setupReady.Task;
        try
        {
            await uiDispatcher.InvokeAsync(onboardingFlow.Start);
            Assert.True(onboardingFlow.IsOnboardingShowing);

            await coordinator.Post(new CompanionSessionEvent.ShortcutPressed());
            await coordinator.Post(new CompanionSessionEvent.ShortcutReleased());

            // AN-02's asymmetry: the ignored press tracks nothing, its release still tracks push_to_talk_released.
            Assert.Equal(["onboarding_started", "push_to_talk_released"], analyticsSink.RecordedEvents.Select(analyticsEvent => analyticsEvent.Name));
            Assert.Equal(CompanionVoiceState.Idle, coordinator.VoiceState);

            // Once the static onboarding has completed, a press is handled again.
            fakeTimeProvider.Advance(OnboardingFlow.OnboardingCompletesAfter);
            await WaitUntilAsync(() => !onboardingFlow.IsOnboardingShowing, "the onboarding to complete");
            await coordinator.Post(new CompanionSessionEvent.ShortcutPressed());
            Assert.Equal(
                ["onboarding_started", "push_to_talk_released", "push_to_talk_started"],
                analyticsSink.RecordedEvents.Select(analyticsEvent => analyticsEvent.Name));
        }
        finally
        {
            uiDispatcher.InvokeShutdown();
        }
    }

    [Fact]
    public async Task ClosingPromptAfterOnboarding()
    {
        var fakeTimeProvider = new FakeTimeProvider();
        var analyticsSink = new RecordingAnalyticsSink();
        var bubbleTexts = new ConcurrentQueue<string>();
        OnboardingFlow onboardingFlow = CreateOnboardingFlow(fakeTimeProvider, analyticsSink, new RecordingFormSparkHandler(), bubbleTexts.Enqueue);
        int panelDismissRequestCount = 0;
        onboardingFlow.PanelDismissRequested += () => panelDismissRequestCount++;

        onboardingFlow.Start();
        await RunStaticOnboardingUntilCompletedAsync(fakeTimeProvider, onboardingFlow, bubbleTexts);

        // CompanionManager.swift:871-879: the prompt waits 2.3 s after the onboarding completes.
        fakeTimeProvider.Advance(TimeSpan.FromSeconds(2.2));
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        Assert.False(onboardingFlow.IsClosingPromptShowing, "the closing prompt appeared before 2.3 s");
        fakeTimeProvider.Advance(TimeSpan.FromSeconds(0.1));
        await WaitUntilAsync(() => onboardingFlow.IsClosingPromptShowing, "the closing prompt at 2.3 s");
        Assert.Equal("press Ctrl + Alt and introduce yourself", bubbleTexts.Last());

        // :906-916: it dismisses itself after 10 s.
        fakeTimeProvider.Advance(TimeSpan.FromSeconds(9.9));
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        Assert.True(onboardingFlow.IsClosingPromptShowing, "the closing prompt dismissed before 10 s");
        fakeTimeProvider.Advance(TimeSpan.FromSeconds(0.1));
        await WaitUntilAsync(() => !onboardingFlow.IsClosingPromptShowing, "the closing prompt to dismiss after 10 s");
        Assert.Equal("", bubbleTexts.Last());

        // ONB-05: Watch Onboarding Again from the onboarded panel dismisses it, fires onboarding_replayed and re-runs
        // the static onboarding; there is no video to replay, so onboarding_video_completed never fires.
        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        try
        {
            PanelViewModel panelViewModel = CreatePanelViewModel(
                dataDirectoryPath, CompanionSettings.Defaults with { HasCompletedOnboarding = true, HasSubmittedEmail = true }, onboardingFlow);
            panelViewModel.Refresh(hasMicrophonePermission: true, isOverlayVisible: true, CompanionVoiceState.Idle);
            Assert.True(panelViewModel.IsWatchOnboardingAgainVisible);
            panelViewModel.WatchOnboardingAgainCommand.Execute(null);
        }
        finally
        {
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
        Assert.Equal(2, panelDismissRequestCount);
        Assert.Equal(["onboarding_started", "onboarding_replayed"], analyticsSink.RecordedEvents.Select(analyticsEvent => analyticsEvent.Name));
        Assert.True(onboardingFlow.IsOnboardingShowing);
        bubbleTexts.Clear();
        await RunStaticOnboardingUntilCompletedAsync(fakeTimeProvider, onboardingFlow, bubbleTexts);
        Assert.DoesNotContain(analyticsSink.RecordedEvents, analyticsEvent => analyticsEvent.Name == "onboarding_video_completed");
    }

    /// <summary>A flow for tests that never run onboarding: FormSpark answers 200 and the music plays silently.</summary>
    internal static OnboardingFlow CreateIdleOnboardingFlow() =>
        CreateOnboardingFlow(TimeProvider.System, new InMemoryAnalyticsSink(), new RecordingFormSparkHandler(), _ => { });

    private static OnboardingFlow CreateOnboardingFlow(
        TimeProvider timeProvider, IAnalyticsSink analyticsSink, HttpMessageHandler formSparkHandler, Action<string> showOnboardingBubbleText) =>
        new(new HttpClient(formSparkHandler), analyticsSink, new OnboardingMusicPlayer(new SilentReplayAudioOutput(timeProvider), timeProvider),
            showOnboardingBubbleText, timeProvider);

    private static PanelViewModel CreatePanelViewModel(string dataDirectoryPath, CompanionSettings userSettings, OnboardingFlow onboardingFlow) =>
        new(new ClickyUserSettingsStore(dataDirectoryPath), userSettings, launchUri: _ => { }, onSelectedClaudeModelChanged: _ => { },
            onPushToTalkShortcutChanged: _ => { }, quitApplication: () => { }, onboardingFlow);

    /// <summary>OverlayWindow.swift:355-360, 683-694: the welcome bubble from 2 s until the onboarding completes.</summary>
    private static async Task RunStaticOnboardingUntilCompletedAsync(
        FakeTimeProvider fakeTimeProvider, OnboardingFlow onboardingFlow, ConcurrentQueue<string> bubbleTexts)
    {
        fakeTimeProvider.Advance(OnboardingFlow.WelcomeAppearsAfter);
        await WaitUntilAsync(() => bubbleTexts.LastOrDefault() == "hey! i'm clicky", "the welcome bubble at 2 s");
        Assert.True(onboardingFlow.IsOnboardingShowing);
        fakeTimeProvider.Advance(OnboardingFlow.OnboardingCompletesAfter - OnboardingFlow.WelcomeAppearsAfter);
        await WaitUntilAsync(() => !onboardingFlow.IsOnboardingShowing, "the onboarding to complete");
        Assert.Equal("", bubbleTexts.Last());
        Assert.False(onboardingFlow.IsClosingPromptShowing);
    }

    /// <summary>
    /// FakeTimeProvider fires a delay during Advance, but the awaiting continuation may run on the thread pool or the
    /// dispatcher a moment later. Only the wait is wall clock; the times under assertion come from the virtual clock.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string conditionDescription)
    {
        var wallClockTimeout = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(wallClockTimeout.Elapsed < TimeSpan.FromSeconds(5), $"Timed out waiting for {conditionDescription}.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Stands in for submit-form.com: records each request and answers with the configured status.</summary>
    private sealed class RecordingFormSparkHandler : HttpMessageHandler
    {
        public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;

        public List<(HttpMethod Method, Uri RequestUri, string? ContentType, string Body)> RecordedRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            RecordedRequests.Add((request.Method, request.RequestUri!, request.Content?.Headers.ContentType?.MediaType, body));
            return new HttpResponseMessage(ResponseStatusCode);
        }
    }

    /// <summary>The in-memory sink drops identify calls; this one keeps them.</summary>
    private sealed class RecordingAnalyticsSink : IAnalyticsSink
    {
        private readonly ConcurrentQueue<AnalyticsEventReport> recordedEvents = new();

        public IReadOnlyList<AnalyticsEventReport> RecordedEvents => [.. recordedEvents];

        public List<AnalyticsIdentify> RecordedIdentifies { get; } = [];

        public void TrackEvent(AnalyticsEvent analyticsEvent) => recordedEvents.Enqueue(AnalyticsEventReport.FromAnalyticsEvent(analyticsEvent));

        public void Identify(AnalyticsIdentify analyticsIdentify) => RecordedIdentifies.Add(analyticsIdentify);
    }
}
