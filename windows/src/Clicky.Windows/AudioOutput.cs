using NAudio.Wave;

namespace Clicky.Windows;

/// <summary>
/// Where a downloaded TTS MP3 is played (WS-17, audit row F5): WASAPI live, a silent fake in replay. One
/// playback at a time, like the single AVAudioPlayer of ElevenLabsTTSClient.swift:20. All members are called
/// on the dispatcher, and PlaybackStopped is raised there too.
/// </summary>
internal interface IAudioOutput
{
    /// <summary>Starts playing a complete MP3; the caller has already downloaded all of it (parity spec TTS-01).</summary>
    void Play(byte[] mp3Bytes);

    /// <summary>Stops playback at once (TTS-04, ElevenLabsTTSClient.swift:77-80); PlaybackStopped follows.</summary>
    void Stop();

    bool IsPlaying { get; }

    /// <summary>Raised once per Play: when the audio has played to its end, or after Stop.</summary>
    event EventHandler? PlaybackStopped;
}

/// <summary>
/// Live playback: Media Foundation decodes the MP3 straight from memory and WasapiOut plays it on the default
/// render device in shared mode (NAudio, MIT, audit row F5).
/// </summary>
internal sealed class WasapiAudioOutput : IAudioOutput
{
    private WasapiOut? wasapiOut;
    private StreamMediaFoundationReader? mp3Reader;

    public event EventHandler? PlaybackStopped;

    public bool IsPlaying => wasapiOut?.PlaybackState == PlaybackState.Playing;

    public void Play(byte[] mp3Bytes)
    {
        ReleasePlayback();
        mp3Reader = new StreamMediaFoundationReader(new MemoryStream(mp3Bytes));
        // Constructed on the dispatcher, so WasapiOut raises PlaybackStopped through the dispatcher's context.
        wasapiOut = new WasapiOut();
        wasapiOut.PlaybackStopped += HandleWasapiPlaybackStopped;
        wasapiOut.Init(mp3Reader);
        wasapiOut.Play();
    }

    public void Stop() => wasapiOut?.Stop();

    private void HandleWasapiPlaybackStopped(object? sender, StoppedEventArgs stoppedEventArgs)
    {
        if (stoppedEventArgs.Exception is not null)
        {
            ClickyLog.Write($"tts playback failed: {stoppedEventArgs.Exception.Message}");
        }
        ReleasePlayback();
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    private void ReleasePlayback()
    {
        if (wasapiOut is not null)
        {
            // Detached first, so disposing a playing device does not report a stop for a playback being replaced.
            wasapiOut.PlaybackStopped -= HandleWasapiPlaybackStopped;
            wasapiOut.Dispose();
            wasapiOut = null;
        }
        mp3Reader?.Dispose();
        mp3Reader = null;
    }
}

/// <summary>
/// The replay fake: "plays" for the MP3's real duration on the injected clock and opens no audio device, so CI
/// stays silent while voiceState still spends exactly the audio's length in responding.
/// </summary>
internal sealed class SilentReplayAudioOutput(TimeProvider timeProvider) : IAudioOutput
{
    private CancellationTokenSource? playbackCancellation;

    public event EventHandler? PlaybackStopped;

    public bool IsPlaying => playbackCancellation is not null;

    public void Play(byte[] mp3Bytes)
    {
        Stop();
        playbackCancellation = new CancellationTokenSource();
        _ = PlayUntilEndAsync(MeasureMp3Duration(mp3Bytes), playbackCancellation);
    }

    public void Stop()
    {
        if (playbackCancellation is null)
        {
            return;
        }
        playbackCancellation.Cancel();
        playbackCancellation = null;
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sums the samples of every MPEG frame, so the fake lasts exactly as long as the real playback would.</summary>
    internal static TimeSpan MeasureMp3Duration(byte[] mp3Bytes)
    {
        using var mp3Stream = new MemoryStream(mp3Bytes);
        long totalSampleCount = 0;
        int sampleRate = 0;
        while (Mp3Frame.LoadFromStream(mp3Stream) is Mp3Frame mp3Frame)
        {
            totalSampleCount += mp3Frame.SampleCount;
            sampleRate = mp3Frame.SampleRate;
        }
        return sampleRate == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)totalSampleCount / sampleRate);
    }

    private async Task PlayUntilEndAsync(TimeSpan playbackDuration, CancellationTokenSource thisPlaybackCancellation)
    {
        // Even an empty MP3 ends after Play has returned, as a real device would, never inside it.
        await Task.Yield();
        try
        {
            await Task.Delay(playbackDuration, timeProvider, thisPlaybackCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Stop already reported this playback.
            return;
        }
        playbackCancellation = null;
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }
}
