namespace Clicky.Windows;

/// <summary>
/// The raw capture format a source delivers. On Windows this is the default endpoint's shared-mode
/// mix format (audit row F1), typically 44.1/48 kHz one-or-two-channel float; the pipeline resamples
/// it to PCM16 mono 16 kHz with Core's Pcm16AudioConverter.
/// </summary>
internal sealed record MicrophoneCaptureFormat(int SampleRateHz, int ChannelCount);

/// <summary>
/// One microphone capture session on the default capture endpoint, behind an interface so the
/// replay/CI path drives the identical pipeline with a fake source (the GitHub runner has no audio
/// device; task T-020, audit row F1). A real session is a shared-mode WASAPI capture
/// (IAudioClient/IAudioCaptureClient); implementations raise the samples event from their own
/// capture thread.
/// </summary>
internal interface IMicrophoneCaptureSource : IDisposable
{
    /// <summary>The mix format of the endpoint this source captures from; fixed for the source's lifetime.</summary>
    MicrophoneCaptureFormat CaptureFormat { get; }

    /// <summary>
    /// Interleaved float samples in [-1, 1] (one entry per channel per frame) at
    /// <see cref="CaptureFormat"/>'s rate, raised per captured buffer on the source's capture thread.
    /// </summary>
    event Action<ReadOnlyMemory<float>>? InterleavedFloatSamplesCaptured;

    /// <summary>Begins capturing. Buffers may reach the handler before this returns; it must not be called twice without a StopCapture.</summary>
    void StartCapture();

    /// <summary>Stops capturing. After this returns, no further buffers are raised (late buffers may be dropped silently).</summary>
    void StopCapture();
}

/// <summary>
/// Opens a <see cref="IMicrophoneCaptureSource"/> on the CURRENT default capture endpoint. The
/// pipeline re-asks the factory after every default-device change so capture reopens on the new
/// default (audit row F1's IMMNotificationClient handling).
/// </summary>
internal interface IMicrophoneCaptureSourceFactory
{
    IMicrophoneCaptureSource OpenDefaultMicrophoneCaptureSource();
}

/// <summary>
/// Default-capture-endpoint change notifications. The real implementation wraps
/// IMMNotificationClient (audit row F1); the fake in MicCaptureReplayTests lets a test raise the
/// notification on its own thread.
/// </summary>
internal interface IMicrophoneDeviceChangeNotifier : IDisposable
{
    /// <summary>Raised after the default capture endpoint changed, on the notification source's thread.</summary>
    event Action? DefaultCaptureDeviceChanged;

    /// <summary>Begins listening for notifications; idempotent.</summary>
    void Start();
}
