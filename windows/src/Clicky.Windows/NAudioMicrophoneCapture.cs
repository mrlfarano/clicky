using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Clicky.Windows;

// WASAPI access choice: NAudio.CoreAudioApi (MIT) rather than hand-rolled CsWin32 COM bindings.
// Audit row F1 explicitly names "NAudio WasapiCapture" as the approved Windows equivalent of
// AVAudioEngine's input tap, and the shared-mode IAudioClient/IAudioCaptureClient event loop plus
// the MMDeviceEnumerator / IMMNotificationClient registration is exactly NAudio.CoreAudioApi's
// existing, battle-tested surface; re-implementing it as raw COM interop would add a large block of
// code that CI can never execute (the runner has no audio device) with no parity benefit. No audio
// MATH is delegated to the library: mixing, resampling and PCM16 encoding stay in Core's
// Pcm16AudioConverter (T-019) and RMS in Core's AudioPowerLevelMeter, so NAudio only moves bytes.

/// <summary>
/// The live capture source factory (task T-020 / WS-13, audit row F1): each open resolves the
/// current default capture endpoint and starts a shared-mode WASAPI capture on it.
/// </summary>
internal sealed class NAudioMicrophoneCaptureSourceFactory : IMicrophoneCaptureSourceFactory
{
    public IMicrophoneCaptureSource OpenDefaultMicrophoneCaptureSource() => new NAudioMicrophoneCaptureSource();
}

/// <summary>
/// The live microphone capture session: shared-mode WASAPI capture (IAudioClient/IAudioCaptureClient
/// via NAudio's <c>WasapiCapture</c>) on the default capture endpoint. Shared mode is required —
/// exclusive mode would mute every other app's audio whenever Clicky listens. Buffers are raised on
/// NAudio's capture thread as interleaved float32 in the endpoint's mix format.
/// </summary>
internal sealed class NAudioMicrophoneCaptureSource : IMicrophoneCaptureSource
{
    private readonly MMDeviceEnumerator deviceEnumerator = new();
    private readonly MMDevice captureDevice;
    private readonly WasapiCapture wasapiCapture;

    public NAudioMicrophoneCaptureSource()
    {
        // Role.Console is Windows' plain "Default Device" (the analog of the Mac's default input);
        // Role.Communications is the separately-configurable headset variant.
        captureDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        // WasapiCapture's default share mode is AUDCLNT_SHAREMODE_SHARED, which is what audit row F1 pins.
        wasapiCapture = new WasapiCapture(captureDevice);

        WaveFormat captureWaveFormat = wasapiCapture.WaveFormat;
        if (captureWaveFormat.Encoding != WaveFormatEncoding.IeeeFloat || captureWaveFormat.BitsPerSample != 32)
        {
            // Shared-mode capture always negotiates the audio engine's mix format, which is 32-bit
            // float on every shipping Windows; anything else would corrupt the byte-to-float cast below.
            throw new NotSupportedException(
                $"Shared-mode WASAPI capture is expected to deliver a 32-bit IeeeFloat mix format, but '{captureDevice.FriendlyName}' reported {captureWaveFormat}.");
        }
        CaptureFormat = new MicrophoneCaptureFormat(captureWaveFormat.SampleRate, captureWaveFormat.Channels);

        wasapiCapture.DataAvailable += HandleWasapiCaptureDataAvailable;
    }

    public MicrophoneCaptureFormat CaptureFormat { get; }

    public event Action<ReadOnlyMemory<float>>? InterleavedFloatSamplesCaptured;

    public void StartCapture() => wasapiCapture.StartRecording();

    public void StopCapture() => wasapiCapture.StopRecording();

    public void Dispose()
    {
        wasapiCapture.DataAvailable -= HandleWasapiCaptureDataAvailable;
        wasapiCapture.Dispose();
        captureDevice.Dispose();
        deviceEnumerator.Dispose();
    }

    private void HandleWasapiCaptureDataAvailable(object? sender, WaveInEventArgs wasapiCaptureEventArgs)
    {
        // Shared-mode WASAPI hands back the mix format: interleaved 32-bit float. BytesRecorded can
        // be shorter than the buffer, and NAudio reuses buffers, so copy the exact recorded span
        // into memory this event owns before handing it on.
        ReadOnlySpan<float> interleavedFloatSamples = MemoryMarshal.Cast<byte, float>(
            wasapiCaptureEventArgs.Buffer.AsSpan(0, wasapiCaptureEventArgs.BytesRecorded));
        InterleavedFloatSamplesCaptured?.Invoke(interleavedFloatSamples.ToArray());
    }
}

/// <summary>
/// The live default-device-change notifier (audit row F1): registers an IMMNotificationClient with
/// the MMDeviceEnumerator and surfaces exactly the event the pipeline cares about — the default
/// CAPTURE endpoint (Console role) changing, which is what makes "reopen capture on the new default"
/// necessary when the user plugs in or switches headsets mid-recording.
/// </summary>
internal sealed class NAudioMicrophoneDeviceChangeNotifier : IMicrophoneDeviceChangeNotifier
{
    private readonly MMDeviceEnumerator deviceEnumerator = new();
    private readonly DefaultCaptureDeviceChangeNotificationClient notificationClient = new();
    private bool isListeningForDeviceChanges;

    public event Action? DefaultCaptureDeviceChanged
    {
        // Forwarded to the notification client so subscribers and the COM callback live on one object.
        add { notificationClient.DefaultCaptureDeviceChanged += value; }
        remove { notificationClient.DefaultCaptureDeviceChanged -= value; }
    }

    public void Start()
    {
        if (isListeningForDeviceChanges)
        {
            return;
        }
        deviceEnumerator.RegisterEndpointNotificationCallback(notificationClient);
        isListeningForDeviceChanges = true;
    }

    public void Dispose()
    {
        if (isListeningForDeviceChanges)
        {
            deviceEnumerator.UnregisterEndpointNotificationCallback(notificationClient);
            isListeningForDeviceChanges = false;
        }
        deviceEnumerator.Dispose();
    }

    /// <summary>
    /// NAudio's IMMNotificationClient projection: the enumerator invokes these from a COM callback
    /// thread. Only the default capture endpoint's Console role matters here; every other
    /// notification (state changes, property changes, other roles) is irrelevant to reopening capture.
    /// </summary>
    private sealed class DefaultCaptureDeviceChangeNotificationClient : IMMNotificationClient
    {
        public event Action? DefaultCaptureDeviceChanged;

        // NAudio names this OnDefaultDeviceChanged (the COM method is OnDefaultEndpointChanged);
        // defaultDeviceId identifies the new default endpoint, which the factory re-resolves anyway.
        public void OnDefaultDeviceChanged(DataFlow dataFlow, Role role, string defaultDeviceId)
        {
            if (dataFlow == DataFlow.Capture && role == Role.Console)
            {
                DefaultCaptureDeviceChanged?.Invoke();
            }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
        }

        public void OnDeviceAdded(string deviceId)
        {
        }

        public void OnDeviceRemoved(string deviceId)
        {
        }

        public void OnPropertyValueChanged(string deviceId, PropertyKey propertyKey)
        {
        }
    }
}
