using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// Receives the PCM16 mono 16 kHz little-endian byte frames the capture pipeline produces (plan
/// seam 1). <see cref="AssemblyAiStreamingTranscriptionProvider"/> is the implementation (T-023).
/// </summary>
internal interface IPcmFrameSink
{
    /// <summary>
    /// One chunk of converted audio; never empty (buffers with no fully determined frames are not
    /// delivered). Called from a capture thread while the pipeline's internal lock is held, so an
    /// implementation must not call back into <see cref="MicrophoneCapturePipeline"/>.
    /// </summary>
    void ReceivePcm16Frame(ReadOnlyMemory<byte> pcm16LittleEndianFrameBytes);
}

/// <summary>
/// The push-to-talk capture pipeline (task T-020 / WS-13; audit rows F1, F2 and F3): opens the
/// default microphone through <see cref="IMicrophoneCaptureSourceFactory"/>, converts every
/// captured buffer with Core's <see cref="Pcm16AudioConverter"/> (T-019) and hands the PCM16 mono
/// 16 kHz bytes to <see cref="IPcmFrameSink"/>. When the default capture endpoint changes
/// (IMMNotificationClient) it reopens capture mid-session, so switching headsets does not end a
/// recording. The waveform level (audit F3) is computed on the first channel's float buffer with
/// <see cref="AudioPowerLevelMeter"/>. Everything OS-specific is injected, which is what lets
/// MicCaptureReplayTests drive this identical pipeline with fakes on a runner that has no audio
/// device.
/// </summary>
internal sealed class MicrophoneCapturePipeline : IDisposable
{
    private readonly IMicrophoneCaptureSourceFactory captureSourceFactory;
    private readonly IMicrophoneDeviceChangeNotifier deviceChangeNotifier;
    private readonly IPcmFrameSink pcmFrameSink;
    private readonly Lock captureStateLock = new();
    private readonly AudioPowerLevelMeter audioPowerLevelMeter = new();

    /// <summary>Source and converter are swapped together under the lock, so these two are never inconsistently visible.</summary>
    private IMicrophoneCaptureSource? currentCaptureSource;

    private Pcm16AudioConverter? currentPcm16AudioConverter;
    private MicrophoneCaptureFormat? currentConverterInputFormat;

    /// <summary>
    /// The smoothed 0..1 waveform level for the overlay (audit F3), raised per captured buffer.
    /// Like the sink, it is raised while the pipeline's lock is held, so a subscriber must not call
    /// back into this pipeline.
    /// </summary>
    public event Action<float>? AudioPowerLevelChanged;

    public MicrophoneCapturePipeline(
        IMicrophoneCaptureSourceFactory captureSourceFactory,
        IMicrophoneDeviceChangeNotifier deviceChangeNotifier,
        IPcmFrameSink pcmFrameSink)
    {
        this.captureSourceFactory = captureSourceFactory;
        this.deviceChangeNotifier = deviceChangeNotifier;
        this.pcmFrameSink = pcmFrameSink;
        deviceChangeNotifier.DefaultCaptureDeviceChanged += HandleDefaultCaptureDeviceChanged;
        // Registered from construction so no default-device change can slip by before a capture starts.
        deviceChangeNotifier.Start();
    }

    /// <summary>
    /// Opens the default microphone and begins converting captured audio. Buffers may already have
    /// reached the sink when this returns (the fake source delivers synchronously; WASAPI delivers
    /// on its capture thread).
    /// </summary>
    public void StartCapture()
    {
        IMicrophoneCaptureSource? newCaptureSource = null;
        lock (captureStateLock)
        {
            if (currentCaptureSource is not null)
            {
                return; // already capturing; the macOS original guards the double-start the same way
            }
            newCaptureSource = captureSourceFactory.OpenDefaultMicrophoneCaptureSource();
            PrepareConverterForFormatLocked(newCaptureSource.CaptureFormat);
            newCaptureSource.InterleavedFloatSamplesCaptured += HandleInterleavedFloatSamplesCaptured;
            currentCaptureSource = newCaptureSource;
        }

        // Started OUTSIDE the lock on purpose: a source that raises buffers synchronously from its
        // own StartCapture (the replay fake) must be able to take the lock in the samples handler
        // without re-entering it here.
        newCaptureSource.StartCapture();
    }

    /// <summary>
    /// Stops capture and flushes the converter's trailing frames — the key-up transition: once this
    /// returns, the sink has received every PCM16 byte this session will produce.
    /// </summary>
    public void StopCapture()
    {
        IMicrophoneCaptureSource? captureSourceToStop = DetachCurrentCaptureSource();
        if (captureSourceToStop is null)
        {
            return; // not capturing
        }
        StopAndDisposeCaptureSource(captureSourceToStop);

        lock (captureStateLock)
        {
            Pcm16AudioConverter? converterToFlush = currentPcm16AudioConverter;
            currentPcm16AudioConverter = null;
            currentConverterInputFormat = null;
            if (converterToFlush is not null)
            {
                // Emits the trailing output frames whose sinc windows crossed the end of the capture
                // session (T-019's FlushRemainingAudio; the instance is spent afterwards).
                DeliverPcm16FrameBytesIfAny(converterToFlush.FlushRemainingAudio());
            }
        }
    }

    public void Dispose()
    {
        // Unsubscribe and release the notification first so no reopen can start while disposing.
        deviceChangeNotifier.DefaultCaptureDeviceChanged -= HandleDefaultCaptureDeviceChanged;
        deviceChangeNotifier.Dispose();
        StopCapture();
    }

    /// <summary>
    /// The default capture endpoint changed (IMMNotificationClient.OnDefaultEndpointChanged):
    /// capture reopens on the new default without ending the recording (audit row F1).
    /// </summary>
    private void HandleDefaultCaptureDeviceChanged()
    {
        IMicrophoneCaptureSource? captureSourceToReplace = DetachCurrentCaptureSource();
        if (captureSourceToReplace is null)
        {
            return; // idle: the next StartCapture asks the factory for the (already changed) default
        }
        StopAndDisposeCaptureSource(captureSourceToReplace);

        IMicrophoneCaptureSource? newCaptureSource = null;
        lock (captureStateLock)
        {
            if (currentCaptureSource is not null)
            {
                return; // a concurrent reopen or fresh StartCapture already won the race
            }
            newCaptureSource = captureSourceFactory.OpenDefaultMicrophoneCaptureSource();
            PrepareConverterForFormatLocked(newCaptureSource.CaptureFormat);
            newCaptureSource.InterleavedFloatSamplesCaptured += HandleInterleavedFloatSamplesCaptured;
            currentCaptureSource = newCaptureSource;
        }
        newCaptureSource.StartCapture();
    }

    private void HandleInterleavedFloatSamplesCaptured(ReadOnlyMemory<float> interleavedFloatSamples)
    {
        lock (captureStateLock)
        {
            Pcm16AudioConverter? converterForThisBuffer = currentPcm16AudioConverter;
            if (converterForThisBuffer is null)
            {
                return; // this buffer raced a StopCapture/flush or a reopen's unsubscribe; drop it
            }

            // Audit F3: the waveform level is computed on the captured float buffer (the Mac reads
            // floatChannelData[0], BuddyDictationManager.swift:694) before conversion.
            float currentAudioPowerLevel = audioPowerLevelMeter.ProcessMonoFloatSamples(
                FirstChannelSamples(interleavedFloatSamples.Span, currentConverterInputFormat!.ChannelCount));
            AudioPowerLevelChanged?.Invoke(currentAudioPowerLevel);

            // Audit F2: interleaved float at the device rate goes in, PCM16 mono 16 kHz bytes come out.
            byte[] pcm16LittleEndianFrameBytes =
                converterForThisBuffer.ConvertInterleavedFloatSamples(interleavedFloatSamples.Span);
            DeliverPcm16FrameBytesIfAny(pcm16LittleEndianFrameBytes);
        }
    }

    private void PrepareConverterForFormatLocked(MicrophoneCaptureFormat newCaptureFormat)
    {
        if (currentConverterInputFormat == newCaptureFormat)
        {
            return; // a reopen onto an endpoint with the same mix format: the converter (and the
                    // stream) stays continuous, so not a single frame is lost to the device swap
        }

        // First open of the session, or the new default mixes at a different rate/channel count. A
        // Pcm16AudioConverter is fixed to one input format (T-019), so the stream restarts on a
        // fresh converter; the old one's pending tail (under a millisecond of resampler latency) is
        // dropped, which a mid-press device swap loses either way.
        currentPcm16AudioConverter = new Pcm16AudioConverter(newCaptureFormat.SampleRateHz, newCaptureFormat.ChannelCount);
        currentConverterInputFormat = newCaptureFormat;
    }

    private IMicrophoneCaptureSource? DetachCurrentCaptureSource()
    {
        lock (captureStateLock)
        {
            IMicrophoneCaptureSource? captureSourceToDetach = currentCaptureSource;
            currentCaptureSource = null;
            return captureSourceToDetach;
        }
    }

    private void StopAndDisposeCaptureSource(IMicrophoneCaptureSource captureSourceToStop)
    {
        // Unsubscribe BEFORE stopping so a buffer the source still raises while StopCapture drains
        // is ignored instead of converted against a converter it no longer belongs to.
        captureSourceToStop.InterleavedFloatSamplesCaptured -= HandleInterleavedFloatSamplesCaptured;
        captureSourceToStop.StopCapture();
        captureSourceToStop.Dispose();
    }

    private void DeliverPcm16FrameBytesIfAny(byte[] pcm16LittleEndianFrameBytes)
    {
        if (pcm16LittleEndianFrameBytes.Length == 0)
        {
            return; // this buffer did not fully determine any output frame; nothing to send
        }
        pcmFrameSink.ReceivePcm16Frame(pcm16LittleEndianFrameBytes);
    }

    /// <summary>
    /// The first channel of interleaved samples (mono input is returned as-is). The macOS original
    /// meters floatChannelData[0]; for interleaved WASAPI buffers that is every ChannelCount-th sample.
    /// </summary>
    private static ReadOnlySpan<float> FirstChannelSamples(ReadOnlySpan<float> interleavedFloatSamples, int channelCount)
    {
        if (channelCount == 1)
        {
            return interleavedFloatSamples;
        }

        var firstChannelSamples = new float[interleavedFloatSamples.Length / channelCount];
        for (int frameIndex = 0; frameIndex < firstChannelSamples.Length; frameIndex++)
        {
            firstChannelSamples[frameIndex] = interleavedFloatSamples[frameIndex * channelCount];
        }
        return firstChannelSamples;
    }
}
