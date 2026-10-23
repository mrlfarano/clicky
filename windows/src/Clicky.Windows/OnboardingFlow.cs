using System.Net.Http.Json;
using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// The first-run onboarding flow (parity spec ONB-01, ONB-05; T-039) without the video the owner dropped on Windows
/// (ADR 001 addendum): the FormSpark email submit, Start and Watch Onboarding Again, and the static onboarding they
/// both run on the injected clock. That is the welcome bubble (OverlayWindow.swift:352-361, 677-702), push-to-talk
/// ignored until it has gone (CompanionManager.swift:477-478 keyed this to the video), then the closing prompt 2.3 s
/// later (:862-879) that dismisses itself after 10 s (:906-916). The panel's view-model persists the settings;
/// this class does the I/O, analytics and timing. Every member is called on the dispatcher, and the timeline resumes there.
/// </summary>
internal sealed class OnboardingFlow(
    HttpClient formSparkHttpClient,
    IAnalyticsSink analyticsSink,
    OnboardingMusicPlayer onboardingMusicPlayer,
    Action<string> showOnboardingBubbleText,
    TimeProvider timeProvider)
{
    /// <summary>CompanionManager.swift:167.</summary>
    public const string FormSparkSubmitUri = "https://submit-form.com/RWbGJxmIs";

    /// <summary>OverlayWindow.swift:173.</summary>
    public const string WelcomeText = "hey! i'm clicky";

    /// <summary>ONB-01's Windows copy of CompanionManager.swift:897.</summary>
    public const string ClosingPromptText = "press Ctrl + Alt and introduce yourself";

    /// <summary>OverlayWindow.swift:355-360: the cursor fades in for 2 s before the welcome types.</summary>
    public static readonly TimeSpan WelcomeAppearsAfter = TimeSpan.FromSeconds(2);

    /// <summary>OverlayWindow.swift:683-694: 15 characters at 0.03 s, then 2.5 s until the welcome is gone.</summary>
    public static readonly TimeSpan OnboardingCompletesAfter = WelcomeAppearsAfter + TimeSpan.FromSeconds(15 * 0.03 + 2.5);

    /// <summary>CompanionManager.swift:871-878: the 2 s video fade-out, then 0.3 s.</summary>
    public static readonly TimeSpan ClosingPromptDelay = TimeSpan.FromSeconds(2.3);

    /// <summary>CompanionManager.swift:906.</summary>
    public static readonly TimeSpan ClosingPromptDisplayDuration = TimeSpan.FromSeconds(10);

    // URLSession's 60 s default would leave Submit disabled that long on a dead network; the shared client has no timeout.
    private static readonly TimeSpan FormSparkTimeout = TimeSpan.FromSeconds(15);

    private readonly AnalyticsEvents analyticsEvents = new();
    private CancellationTokenSource? onboardingTimelineCancellation;

    /// <summary>The Mac's clickyDismissPanel notification, posted by Start and Watch Onboarding Again (CompanionManager.swift:203, 224).</summary>
    public event Action? PanelDismissRequested;

    /// <summary>True from Start or Watch Onboarding Again until the welcome has gone; the coordinator ignores presses meanwhile.</summary>
    public bool IsOnboardingShowing { get; private set; }

    public bool IsClosingPromptShowing { get; private set; }

    /// <summary>
    /// CompanionManager.swift:153-173: POSTs <c>{"email"}</c> to FormSpark and identifies the user in PostHog.
    /// Unlike the Mac's fire-and-forget POST, true only once FormSpark accepted it, so the caller persists
    /// hasSubmittedEmail only then (T-039) and a failed submit leaves the email step up to retry.
    /// </summary>
    public async Task<bool> SubmitEmailAsync(string email)
    {
        string trimmedEmail = email.Trim();
        if (trimmedEmail.Length == 0)
        {
            return false;
        }
        try
        {
            using var timeoutCancellation = new CancellationTokenSource(FormSparkTimeout, timeProvider);
            using HttpResponseMessage formSparkResponse = await formSparkHttpClient.PostAsJsonAsync(
                FormSparkSubmitUri, new { email = trimmedEmail }, timeoutCancellation.Token);
            if (!formSparkResponse.IsSuccessStatusCode)
            {
                // Never the address itself: the log holds no personal data.
                ClickyLog.Write($"onboarding: email submit failed: FormSpark answered {(int)formSparkResponse.StatusCode}");
                return false;
            }
        }
        catch (Exception submitException) when (submitException is HttpRequestException or OperationCanceledException)
        {
            ClickyLog.Write($"onboarding: email submit failed: {submitException.GetType().Name}");
            return false;
        }
        analyticsSink.Identify(analyticsEvents.Identify(trimmedEmail));
        return true;
    }

    /// <summary>CompanionManager.swift:201-218, after the caller persisted hasCompletedOnboarding.</summary>
    public void Start()
    {
        PanelDismissRequested?.Invoke();
        analyticsSink.TrackEvent(analyticsEvents.OnboardingStarted());
        onboardingMusicPlayer.Start();
        RestartStaticOnboarding();
    }

    /// <summary>CompanionManager.swift:223-231: the same static onboarding again; no video to replay on Windows.</summary>
    public void Replay()
    {
        PanelDismissRequested?.Invoke();
        analyticsSink.TrackEvent(analyticsEvents.OnboardingReplayed());
        onboardingMusicPlayer.Start();
        RestartStaticOnboarding();
    }

    /// <summary>CompanionManager.swift:500-509 and :908-915: a press, or the 10 s timer, hides the closing prompt.</summary>
    public void DismissClosingPrompt()
    {
        if (!IsClosingPromptShowing)
        {
            return;
        }
        IsClosingPromptShowing = false;
        showOnboardingBubbleText("");
    }

    private void RestartStaticOnboarding()
    {
        onboardingTimelineCancellation?.Cancel();
        onboardingTimelineCancellation = new CancellationTokenSource();
        IsClosingPromptShowing = false;
        showOnboardingBubbleText("");
        IsOnboardingShowing = true;
        _ = RunStaticOnboardingAsync(timeProvider.GetUtcNow(), onboardingTimelineCancellation.Token);
    }

    // shortcut: the welcome and the prompt appear whole instead of typing at 0.03 s per character and fading;
    // add the typing if the overlay grows a text animation.
    private async Task RunStaticOnboardingAsync(DateTimeOffset onboardingStartedAt, CancellationToken cancellationToken)
    {
        try
        {
            await DelayUntilAsync(onboardingStartedAt + WelcomeAppearsAfter, cancellationToken);
            showOnboardingBubbleText(WelcomeText);
            DateTimeOffset onboardingCompletedAt = onboardingStartedAt + OnboardingCompletesAfter;
            await DelayUntilAsync(onboardingCompletedAt, cancellationToken);
            showOnboardingBubbleText("");
            IsOnboardingShowing = false;
            await DelayUntilAsync(onboardingCompletedAt + ClosingPromptDelay, cancellationToken);
            IsClosingPromptShowing = true;
            showOnboardingBubbleText(ClosingPromptText);
            await DelayUntilAsync(onboardingCompletedAt + ClosingPromptDelay + ClosingPromptDisplayDuration, cancellationToken);
            DismissClosingPrompt();
        }
        catch (OperationCanceledException)
        {
            // A replay restarted the timeline.
        }
    }

    // Absolute due times, so one large clock advance still runs every later step at once.
    private async Task DelayUntilAsync(DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        TimeSpan delayUntilDue = dueAt - timeProvider.GetUtcNow();
        if (delayUntilDue > TimeSpan.Zero)
        {
            await Task.Delay(delayUntilDue, timeProvider, cancellationToken);
        }
        // A restart can land between the delay completing and this continuation running.
        cancellationToken.ThrowIfCancellationRequested();
    }
}
