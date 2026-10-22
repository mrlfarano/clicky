namespace Clicky.Windows;

/// <summary>
/// The replay microphone (T-023): replay drives the real <see cref="MicrophoneCapturePipeline"/> with this fake
/// source, because a CI runner has no audio device (audit row F1, T-020). Each capture delivers 100 ms of a quiet
/// 16 kHz mono sine per tick on the injected clock, so a press-to-release span of N ms yields about N / 100
/// binary websocket frames of 3200 PCM16 bytes.
/// </summary>
internal sealed class ReplayMicrophoneCaptureSourceFactory(TimeProvider timeProvider) : IMicrophoneCaptureSourceFactory
{
    public IMicrophoneCaptureSource OpenDefaultMicrophoneCaptureSource() => new ReplayMicrophoneCaptureSource(timeProvider);

    private sealed class ReplayMicrophoneCaptureSource(TimeProvider timeProvider) : IMicrophoneCaptureSource
    {
        private static readonly TimeSpan BufferDuration = TimeSpan.FromMilliseconds(100);
        private static readonly float[] QuietSineBuffer = Enumerable.Range(0, 1600)
            .Select(sampleIndex => 0.1f * MathF.Sin(2 * MathF.PI * 440 * sampleIndex / 16000))
            .ToArray();

        private ITimer? bufferTimer;

        public MicrophoneCaptureFormat CaptureFormat { get; } = new(SampleRateHz: 16000, ChannelCount: 1);

        public event Action<ReadOnlyMemory<float>>? InterleavedFloatSamplesCaptured;

        public void StartCapture() =>
            bufferTimer = timeProvider.CreateTimer(_ => InterleavedFloatSamplesCaptured?.Invoke(QuietSineBuffer), null, BufferDuration, BufferDuration);

        public void StopCapture() => bufferTimer?.Dispose();

        public void Dispose() => bufferTimer?.Dispose();
    }
}

/// <summary>Replay's default device never changes.</summary>
internal sealed class ReplayMicrophoneDeviceChangeNotifier : IMicrophoneDeviceChangeNotifier
{
    public event Action? DefaultCaptureDeviceChanged
    {
        add { }
        remove { }
    }

    public void Start()
    {
    }

    public void Dispose()
    {
    }
}
