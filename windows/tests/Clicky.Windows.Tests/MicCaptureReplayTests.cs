using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// WS-13 part 2 (task T-020, parity scenario STT-02 audio side): the WASAPI capture pipeline over
/// an injected FAKE audio source, because the CI runner has no audio device (audit row F1). The
/// fakes implement the same source/factory/notifier interfaces the live NAudio session implements,
/// so these tests exercise the identical open → convert (T-019 Pcm16AudioConverter) →
/// PCM16-frame-sink path, including the IMMNotificationClient-driven reopen on a default-device
/// change.
/// </summary>
public sealed class MicCaptureReplayTests
{
    [Fact]
    public void FakeSourceYields160000Bytes()
    {
        // 5 s at the WASAPI-typical 48 kHz stereo mix format. AssemblyAI's sink is PCM16 mono 16 kHz,
        // so 5 s * 16000 frames/s * 2 bytes = 160000 B; the task tolerance is +/-1%.
        var fiveSecondFakeSource = FakeMicrophoneCaptureSource.CapturingSineWave(
            sampleRateHz: 48000, channelCount: 2, captureDuration: TimeSpan.FromSeconds(5));
        var fakeSourceFactory = new FakeMicrophoneCaptureSourceFactory(fiveSecondFakeSource);
        var collectingPcmFrameSink = new CollectingPcmFrameSink();
        float maximumObservedAudioPowerLevel = 0;
        using var fakeDeviceChangeNotifier = new FakeMicrophoneDeviceChangeNotifier();

        using var microphoneCapturePipeline = new MicrophoneCapturePipeline(
            fakeSourceFactory, fakeDeviceChangeNotifier, collectingPcmFrameSink);
        microphoneCapturePipeline.AudioPowerLevelChanged += audioPowerLevel =>
            maximumObservedAudioPowerLevel = Math.Max(maximumObservedAudioPowerLevel, audioPowerLevel);

        microphoneCapturePipeline.StartCapture();
        // StopCapture flushes the converter's trailing frames, so when it returns the sink has every
        // byte of the session — which is what makes the exact 160000 B comparable.
        microphoneCapturePipeline.StopCapture();

        Assert.InRange(collectingPcmFrameSink.TotalReceivedByteCount, 158400, 161600);
        // Audit F3 wiring: a 0.25-amplitude sine drives the boosted RMS level to the top of [0, 1].
        Assert.True(maximumObservedAudioPowerLevel > 0.9f,
            $"the captured float buffers should drive the waveform level near 1.0, but it peaked at {maximumObservedAudioPowerLevel}");
    }

    [Fact]
    public void ReopensOnDeviceChange()
    {
        var firstFakeSource = FakeMicrophoneCaptureSource.CapturingSineWave(
            sampleRateHz: 48000, channelCount: 2, captureDuration: TimeSpan.FromSeconds(1));
        var secondFakeSource = FakeMicrophoneCaptureSource.CapturingSineWave(
            sampleRateHz: 48000, channelCount: 2, captureDuration: TimeSpan.FromSeconds(1));
        var fakeSourceFactory = new FakeMicrophoneCaptureSourceFactory(firstFakeSource, secondFakeSource);
        var collectingPcmFrameSink = new CollectingPcmFrameSink();
        using var fakeDeviceChangeNotifier = new FakeMicrophoneDeviceChangeNotifier();

        using var microphoneCapturePipeline = new MicrophoneCapturePipeline(
            fakeSourceFactory, fakeDeviceChangeNotifier, collectingPcmFrameSink);
        microphoneCapturePipeline.StartCapture();
        Assert.Equal(1, fakeDeviceChangeNotifier.StartCallCount); // the pipeline owns notifier registration
        long byteCountAfterFirstOpen = collectingPcmFrameSink.TotalReceivedByteCount;
        Assert.True(byteCountAfterFirstOpen > 0, "the first fake source delivered no PCM16 bytes");

        // The default endpoint changed: IMMNotificationClient would raise this on a COM callback
        // thread; the fake raises it on the test thread.
        fakeDeviceChangeNotifier.RaiseDefaultCaptureDeviceChanged();

        // Reopened: the factory was asked again (it resolves the NEW default endpoint)...
        Assert.Equal(2, fakeSourceFactory.OpenCallCount);
        // ...the old session was stopped and disposed...
        Assert.Equal(1, firstFakeSource.StopCaptureCallCount);
        Assert.True(firstFakeSource.IsDisposed);
        // ...the replacement session was started...
        Assert.Equal(1, secondFakeSource.StartCaptureCallCount);
        // ...and its audio kept flowing through the SAME conversion path (the mix format matches, so
        // the converter runs on continuously: 2 s of 48 kHz input = exactly 64000 PCM16 bytes).
        Assert.True(collectingPcmFrameSink.TotalReceivedByteCount > byteCountAfterFirstOpen,
            "the reopened capture delivered no further PCM16 bytes");

        microphoneCapturePipeline.StopCapture();
        Assert.Equal(64000, collectingPcmFrameSink.TotalReceivedByteCount);
    }

    /// <summary>
    /// The fake audio source for CI: generates a deterministic 440 Hz 0.25-amplitude sine at the
    /// requested mix format and raises it in WASAPI-like ~10 ms buffers, synchronously from
    /// StartCapture. Synchronous delivery is what makes the byte counts below deterministic; the
    /// pipeline is built so this does not re-enter its lock (StartCapture is invoked outside it).
    /// </summary>
    private sealed class FakeMicrophoneCaptureSource : IMicrophoneCaptureSource
    {
        private const int CaptureBufferDurationMilliseconds = 10;
        private const float SineAmplitude = 0.25f;
        private const float SineFrequencyHz = 440f;

        private readonly int sampleRateHz;
        private readonly int channelCount;
        private readonly int totalFrameCount;
        private readonly int framesPerBuffer;

        private FakeMicrophoneCaptureSource(int sampleRateHz, int channelCount, TimeSpan captureDuration)
        {
            this.sampleRateHz = sampleRateHz;
            this.channelCount = channelCount;
            totalFrameCount = (int)(captureDuration.TotalSeconds * sampleRateHz);
            framesPerBuffer = sampleRateHz * CaptureBufferDurationMilliseconds / 1000;
        }

        public static FakeMicrophoneCaptureSource CapturingSineWave(int sampleRateHz, int channelCount, TimeSpan captureDuration) =>
            new(sampleRateHz, channelCount, captureDuration);

        public MicrophoneCaptureFormat CaptureFormat => new(sampleRateHz, channelCount);

        public event Action<ReadOnlyMemory<float>>? InterleavedFloatSamplesCaptured;

        public int StartCaptureCallCount { get; private set; }

        public int StopCaptureCallCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public void StartCapture()
        {
            StartCaptureCallCount++;
            for (int firstFrameIndexOfBuffer = 0; firstFrameIndexOfBuffer < totalFrameCount; firstFrameIndexOfBuffer += framesPerBuffer)
            {
                int frameCountInThisBuffer = Math.Min(framesPerBuffer, totalFrameCount - firstFrameIndexOfBuffer);
                var interleavedFloatSamples = new float[frameCountInThisBuffer * channelCount];
                for (int frameIndexWithinBuffer = 0; frameIndexWithinBuffer < frameCountInThisBuffer; frameIndexWithinBuffer++)
                {
                    float sineSample = SineAmplitude * MathF.Sin(
                        2f * MathF.PI * SineFrequencyHz * (firstFrameIndexOfBuffer + frameIndexWithinBuffer) / sampleRateHz);
                    for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
                    {
                        interleavedFloatSamples[(frameIndexWithinBuffer * channelCount) + channelIndex] = sineSample;
                    }
                }
                InterleavedFloatSamplesCaptured?.Invoke(interleavedFloatSamples);
            }
        }

        public void StopCapture() => StopCaptureCallCount++;

        public void Dispose() => IsDisposed = true;
    }

    /// <summary>
    /// Stands in for "resolve the current default capture endpoint": each open hands back the next
    /// staged fake source, so a reopen after a default-device change visibly returns a NEW source.
    /// </summary>
    private sealed class FakeMicrophoneCaptureSourceFactory(params FakeMicrophoneCaptureSource[] captureSourcesToOpenInOrder)
        : IMicrophoneCaptureSourceFactory
    {
        private int nextSourceIndex;

        public int OpenCallCount { get; private set; }

        public IMicrophoneCaptureSource OpenDefaultMicrophoneCaptureSource()
        {
            OpenCallCount++;
            return captureSourcesToOpenInOrder[Math.Min(nextSourceIndex++, captureSourcesToOpenInOrder.Length - 1)];
        }
    }

    /// <summary>The fake IMMNotificationClient surface: a test raises the default-device change itself.</summary>
    private sealed class FakeMicrophoneDeviceChangeNotifier : IMicrophoneDeviceChangeNotifier
    {
        public event Action? DefaultCaptureDeviceChanged;

        public int StartCallCount { get; private set; }

        public void Start() => StartCallCount++;

        public void RaiseDefaultCaptureDeviceChanged() => DefaultCaptureDeviceChanged?.Invoke();

        public void Dispose()
        {
        }
    }

    /// <summary>Sums the PCM16 frame bytes the pipeline produced (the PCM16/16k mono sink under test).</summary>
    private sealed class CollectingPcmFrameSink : IPcmFrameSink
    {
        public long TotalReceivedByteCount { get; private set; }

        public int ReceivePcm16FrameCallCount { get; private set; }

        public void ReceivePcm16Frame(ReadOnlyMemory<byte> pcm16LittleEndianFrameBytes)
        {
            TotalReceivedByteCount += pcm16LittleEndianFrameBytes.Length;
            ReceivePcm16FrameCallCount++;
        }
    }
}
