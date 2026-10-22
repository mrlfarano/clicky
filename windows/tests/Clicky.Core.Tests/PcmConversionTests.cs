using Xunit;

namespace Clicky.Core.Tests;

/// <summary>
/// Pins the Core port of the macOS audio math: BuddyAudioConversionSupport.swift:11-70
/// (stereo float to PCM16 mono 16 kHz, task T-019 / parity scenario STT-02) and
/// BuddyDictationManager.swift:687-721 (RMS level metering). All expected values are
/// hand-computed from the formulas, never taken from the code under test.
/// </summary>
public sealed class PcmConversionTests
{
    private const int InputSampleRate = 48000;
    private const int InputChannelCount = 2;
    private const int OutputSampleRate = Pcm16AudioConverter.OutputSampleRate;

    [Fact]
    public void ResamplesToExpected16kFrameCount()
    {
        // floor(inputFrameCount * 16000 / 48000): 1 s of input is exactly 16000 output frames
        // (32000 little-endian PCM16 bytes), a quarter second is exactly 4000, and a frame
        // count that is not a multiple of 3 floors rather than rounds (48007 -> 16002).
        Assert.Equal(32000, ConvertWholeInput(BuildStereoSineInterleaved(48000)).Length);
        Assert.Equal(8000, ConvertWholeInput(BuildStereoSineInterleaved(12000)).Length);
        Assert.Equal(32004, ConvertWholeInput(BuildStereoSineInterleaved(48007)).Length);
    }

    [Fact]
    public void FiveSecondsIs160000Bytes()
    {
        // 5 s * 16000 frames/s * 2 bytes per PCM16 frame = 160000 B; the task allows +/- 1%
        // for resampler edge handling, this implementation is exact.
        byte[] fiveSecondsOfPcm16 = ConvertWholeInput(BuildStereoSineInterleaved(5 * InputSampleRate));
        Assert.InRange(fiveSecondsOfPcm16.Length, 158400, 161600);
        Assert.Equal(160000, fiveSecondsOfPcm16.Length);
    }

    [Fact]
    public void FullScaleAudioConvertsWithoutClipping()
    {
        // Constant +1.0 / -1.0 input: the per-frame weight normalization holds the DC gain at
        // exactly 1, so full scale must scale to the endpoints (+32767 / -32768) instead of
        // wrapping past them - the CoreAudio 32768-then-clamp convention from AVAudioConverter.
        short[] positiveFullScaleSamples = DecodePcm16LittleEndian(
            ConvertWholeInput(BuildConstantStereoInterleaved(4800, 1f)));
        Assert.All(positiveFullScaleSamples, sample => Assert.Equal(32767, sample));

        short[] negativeFullScaleSamples = DecodePcm16LittleEndian(
            ConvertWholeInput(BuildConstantStereoInterleaved(4800, -1f)));
        Assert.All(negativeFullScaleSamples, sample => Assert.Equal(-32768, sample));

        // A full-scale sine can overshoot slightly after windowed-sinc interpolation; the clamp
        // must absorb that without wraparound, and both polarities must stay near full scale.
        short[] fullScaleSineSamples = DecodePcm16LittleEndian(
            ConvertWholeInput(BuildStereoSineInterleaved(24000, sineAmplitude: 1f)));
        Assert.All(fullScaleSineSamples, sample => Assert.InRange(sample, short.MinValue, short.MaxValue));
        Assert.InRange(fullScaleSineSamples.Max(), 32000, 32767);
        Assert.InRange(fullScaleSineSamples.Min(), -32768, -32000);
    }

    [Fact]
    public void SilenceIsZeroRootMeanSquare()
    {
        float[] silentBuffer = new float[1024];

        Assert.Equal(0f, AudioPowerLevelMeter.ComputeRootMeanSquare(silentBuffer));

        var audioPowerLevelMeter = new AudioPowerLevelMeter();
        Assert.Equal(0f, audioPowerLevelMeter.ProcessMonoFloatSamples(silentBuffer));
        Assert.Equal(0f, audioPowerLevelMeter.CurrentAudioPowerLevel);
    }

    [Fact]
    public void RmsMatchesKnownValue()
    {
        // Squares are 0.0625 + 0.25 + 0.5625 + 0.015625 = 0.890625; every operand is exact in
        // binary, so the expected value below differs from the float accumulation only by the
        // final sqrt rounding, far inside the 1e-6 tolerance.
        float[] knownBuffer = [0.25f, 0.5f, -0.75f, 0.125f];
        double expectedRootMeanSquare = Math.Sqrt(0.890625 / 4);

        double actualRootMeanSquare = AudioPowerLevelMeter.ComputeRootMeanSquare(knownBuffer);

        Assert.InRange(actualRootMeanSquare, expectedRootMeanSquare - 1e-6, expectedRootMeanSquare + 1e-6);
    }

    [Fact]
    public void SmoothingSequenceMatchesSwiftDecay()
    {
        // BuddyDictationManager.swift:701 and :706-709: level = min(max(rms * 10.2, 0), 1),
        // then smoothed = max(boosted, previous * 0.72). A constant +1.0 buffer has RMS 1, which
        // the boost clamps to 1; each following silent buffer decays the level by exactly 0.72;
        // a new loud buffer takes the max, so it rises instantly instead of averaging.
        float[] loudBuffer = new float[480];
        Array.Fill(loudBuffer, 1f);
        float[] silentBuffer = new float[480];

        var audioPowerLevelMeter = new AudioPowerLevelMeter();

        Assert.Equal(1f, audioPowerLevelMeter.ProcessMonoFloatSamples(loudBuffer));
        AssertDecayedLevel(0.72, audioPowerLevelMeter.ProcessMonoFloatSamples(silentBuffer));
        AssertDecayedLevel(0.72 * 0.72, audioPowerLevelMeter.ProcessMonoFloatSamples(silentBuffer));
        AssertDecayedLevel(0.72 * 0.72 * 0.72, audioPowerLevelMeter.ProcessMonoFloatSamples(silentBuffer));
        Assert.Equal(1f, audioPowerLevelMeter.ProcessMonoFloatSamples(loudBuffer));
        Assert.Equal(1f, audioPowerLevelMeter.CurrentAudioPowerLevel);

        static void AssertDecayedLevel(double expectedDecayedLevel, float actualDecayedLevel)
        {
            // 0.72 is not exactly representable in float; the accumulation error stays far
            // below 1e-6, matching the RMS tolerance above.
            Assert.InRange(
                actualDecayedLevel,
                expectedDecayedLevel - 1e-6,
                expectedDecayedLevel + 1e-6);
        }
    }

    [Fact]
    public void ChunkedConversionMatchesSingleShotConversion()
    {
        // WASAPI feeds ~10 ms packets while the sinc kernel reads 48 input frames on either
        // side of each output frame, so the converter carries pending frames across calls.
        // Feeding the same 2 s either as one buffer or in irregular chunks (including chunk
        // sizes that are not multiples of the 3:1 ratio) must produce identical bytes.
        float[] wholeInputBuffer = BuildStereoSineInterleaved(2 * InputSampleRate);
        byte[] singleShotBytes = ConvertWholeInput(wholeInputBuffer);

        int[] chunkFrameSizes = [480, 1000, 7, 4096, 1, 3583, 3, 5111, 12000, 16000, 53719];
        var chunkedConverter = new Pcm16AudioConverter(InputSampleRate, InputChannelCount);
        var chunkedBytes = new List<byte>();
        int frameOffset = 0;
        foreach (int chunkFrameSize in chunkFrameSizes)
        {
            chunkedBytes.AddRange(chunkedConverter.ConvertInterleavedFloatSamples(
                wholeInputBuffer.AsSpan(frameOffset * InputChannelCount, chunkFrameSize * InputChannelCount)));
            frameOffset += chunkFrameSize;
        }
        chunkedBytes.AddRange(chunkedConverter.FlushRemainingAudio());

        Assert.Equal(2 * InputSampleRate, frameOffset);
        Assert.Equal(singleShotBytes, chunkedBytes);
    }

    /// <summary>Both channels carry the same 440 Hz sine, so the stereo mixdown is that sine.</summary>
    private static float[] BuildStereoSineInterleaved(int inputFrameCount, float sineAmplitude = 0.5f)
    {
        var interleavedSamples = new float[inputFrameCount * InputChannelCount];
        for (int frameIndex = 0; frameIndex < inputFrameCount; frameIndex++)
        {
            float sineSample = (float)(sineAmplitude
                * Math.Sin(2 * Math.PI * 440 * frameIndex / InputSampleRate));
            interleavedSamples[frameIndex * InputChannelCount] = sineSample;
            interleavedSamples[(frameIndex * InputChannelCount) + 1] = sineSample;
        }
        return interleavedSamples;
    }

    private static float[] BuildConstantStereoInterleaved(int inputFrameCount, float constantSampleValue)
    {
        var interleavedSamples = new float[inputFrameCount * InputChannelCount];
        Array.Fill(interleavedSamples, constantSampleValue);
        return interleavedSamples;
    }

    /// <summary>Feeds the whole buffer, then flushes the trailing kernel windows (the key-up path).</summary>
    private static byte[] ConvertWholeInput(float[] interleavedInputSamples)
    {
        var converter = new Pcm16AudioConverter(InputSampleRate, InputChannelCount);
        byte[] convertedDuringFeed = converter.ConvertInterleavedFloatSamples(interleavedInputSamples);
        byte[] convertedAtFlush = converter.FlushRemainingAudio();
        return [.. convertedDuringFeed, .. convertedAtFlush];
    }

    private static short[] DecodePcm16LittleEndian(byte[] pcm16LittleEndianBytes)
    {
        var decodedSamples = new short[pcm16LittleEndianBytes.Length / 2];
        for (int sampleIndex = 0; sampleIndex < decodedSamples.Length; sampleIndex++)
        {
            decodedSamples[sampleIndex] = (short)(pcm16LittleEndianBytes[sampleIndex * 2]
                | (pcm16LittleEndianBytes[(sampleIndex * 2) + 1] << 8));
        }
        return decodedSamples;
    }
}
