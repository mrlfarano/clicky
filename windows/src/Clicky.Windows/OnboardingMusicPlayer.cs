namespace Clicky.Windows;

/// <summary>
/// The onboarding music (parity spec ONB-01, ONB-05): ff.mp3 plays at volume 0.3, holds for 90 s, then fades
/// linearly to 0 over 3 s and stops (CompanionManager.swift:240-282). Nothing starts it at launch: only the
/// onboarding flow's Start and Watch Onboarding Again paths call <see cref="Start"/> (T-039 wires them).
/// It needs its own <see cref="IAudioOutput"/>, since the TTS output plays one clip at a time.
/// All members are called on the dispatcher; the fade resumes there through its awaits.
/// </summary>
internal sealed class OnboardingMusicPlayer(IAudioOutput musicAudioOutput, TimeProvider timeProvider)
{
    internal const float StartVolume = 0.3f;
    internal static readonly TimeSpan HoldBeforeFadeDuration = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan FadeOutDuration = TimeSpan.FromSeconds(3);
    // Swift steps the fade 30 times over 3 s (CompanionManager.swift:265-267).
    private static readonly TimeSpan FadeStepInterval = TimeSpan.FromSeconds(0.1);

    internal static string OnboardingMusicFilePath { get; } = Path.Combine(AppContext.BaseDirectory, "ff.mp3");

    private CancellationTokenSource? fadeCancellation;

    /// <summary>Restarts the music from the beginning at volume 0.3 (CompanionManager.swift:240-260).</summary>
    public void Start()
    {
        Stop();
        if (!File.Exists(OnboardingMusicFilePath))
        {
            ClickyLog.Write("onboarding music not found: ff.mp3");
            return;
        }

        try
        {
            musicAudioOutput.Volume = StartVolume;
            musicAudioOutput.Play(File.ReadAllBytes(OnboardingMusicFilePath));
        }
        catch (Exception playbackException)
        {
            // As on the Mac (:257-259), failed music never stops onboarding itself.
            ClickyLog.Write($"onboarding music failed to play: {playbackException.Message}");
            return;
        }
        fadeCancellation = new CancellationTokenSource();
        _ = HoldThenFadeOutAsync(timeProvider.GetUtcNow(), fadeCancellation.Token);
    }

    /// <summary>Stops the music and any pending fade at once (CompanionManager.swift:233-238).</summary>
    public void Stop()
    {
        fadeCancellation?.Cancel();
        fadeCancellation = null;
        musicAudioOutput.Stop();
    }

    /// <summary>The volume the music has at a given time after it started: 0.3 until 90 s, then linear to 0 at 93 s.</summary>
    internal static float VolumeAtElapsedTime(TimeSpan elapsedSinceMusicStarted)
    {
        TimeSpan elapsedIntoFade = elapsedSinceMusicStarted - HoldBeforeFadeDuration;
        if (elapsedIntoFade <= TimeSpan.Zero)
        {
            return StartVolume;
        }
        if (elapsedIntoFade >= FadeOutDuration)
        {
            return 0f;
        }
        return StartVolume * (float)(1 - elapsedIntoFade / FadeOutDuration);
    }

    private async Task HoldThenFadeOutAsync(DateTimeOffset musicStartedAt, CancellationToken cancellationToken)
    {
        DateTimeOffset fadeEndsAt = musicStartedAt + HoldBeforeFadeDuration + FadeOutDuration;
        DateTimeOffset nextVolumeUpdateAt = musicStartedAt + HoldBeforeFadeDuration;
        try
        {
            while (true)
            {
                // Waits for an absolute due time, so a late-running step never pushes the rest of the fade later.
                TimeSpan delayUntilNextVolumeUpdate = nextVolumeUpdateAt - timeProvider.GetUtcNow();
                if (delayUntilNextVolumeUpdate > TimeSpan.Zero)
                {
                    await Task.Delay(delayUntilNextVolumeUpdate, timeProvider, cancellationToken);
                }
                // A restart can land between the delay completing and this continuation running.
                cancellationToken.ThrowIfCancellationRequested();
                DateTimeOffset now = timeProvider.GetUtcNow();
                musicAudioOutput.Volume = VolumeAtElapsedTime(now - musicStartedAt);
                if (now >= fadeEndsAt)
                {
                    break;
                }
                nextVolumeUpdateAt += FadeStepInterval;
            }
        }
        catch (OperationCanceledException)
        {
            // Stop or a restart already ended this playback.
            return;
        }
        fadeCancellation = null;
        musicAudioOutput.Stop();
    }
}
