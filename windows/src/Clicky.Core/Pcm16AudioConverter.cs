namespace Clicky.Core;

/// <summary>
/// Port of the audio conversion math in <c>BuddyPCM16AudioConverter</c>
/// (BuddyAudioConversionSupport.swift:11-70), audit row F2 / seam 1. The macOS original pushes
/// <c>AVAudioPCMBuffer</c>s through <c>AVAudioConverter</c>; on Windows the shell captures
/// interleaved float32 samples with WASAPI and hands them to this class, which produces the
/// PCM16 mono 16 kHz little-endian byte frames that AssemblyAI expects (parity spec STT-01
/// and STT-02). Pure math on top of raw floats: no Windows, WASAPI or NAudio references,
/// so its tests also run on the Linux CI job.
/// </summary>
/// <remarks>
/// Resampler choice: a windowed-sinc low-pass (Blackman window, 16 zero crossings per side)
/// with per-output-frame weight normalization, implemented from scratch.
/// Why not linear interpolation: 48 kHz to 16 kHz is a 3:1 downsample, and a linear
/// interpolator has no anti-aliasing stopband, so everything between 8 kHz and 24 kHz would
/// fold back into the 0-8 kHz speech band and degrade AssemblyAI accuracy. The Mac's
/// AVAudioConverter is itself sinc-based, so this keeps the same quality class.
/// Why not a library resampler (NAudio WDL / MediaFoundation): the core must stay pure, and
/// the acceptance tests pin exact frame counts and byte-exact chunk behaviour, which needs
/// arithmetic we own rather than a resampler whose output can change with library versions.
/// Determinism: the output frame count is <c>floor(inputFrameCount * 16000 / inputSampleRate)</c>
/// computed in 64-bit integer math, so a known-duration input always yields the exact same
/// frame count on every platform.
/// Cost: at 3:1 the kernel spans 2*48+1 input frames per output frame, roughly 1.5 million
/// multiply-adds per second of speech - negligible next to capture and network.
/// </remarks>
public sealed class Pcm16AudioConverter
{
    /// <summary>AssemblyAI's fixed websocket sample rate (sample_rate=16000, pcm_s16le).</summary>
    public const int OutputSampleRate = 16000;

    // Half-width of the sinc kernel measured in zero crossings. 16 crossings under a Blackman
    // window puts the stopband far below anything 16 kHz speech transcription cares about.
    private const int SincZeroCrossingCountPerSide = 16;

    private readonly int inputSampleRate;
    private readonly int inputChannelCount;
    private readonly double kernelHalfWidthInInputFrames;
    private readonly double kernelCutoffInCyclesPerInputSample;

    // Streaming state. WASAPI delivers ~10 ms packets, while the sinc kernel for one output
    // frame reads input frames up to 48 samples (16 crossings * ratio 3) on either side of its
    // center, so frames must be carried across Convert calls until every output frame that
    // reads them has been produced.
    private long totalInputFramesFed;
    private long nextOutputFrameIndexToProduce;
    private readonly List<float> pendingMonoInputFrames = [];
    private long absoluteIndexOfFirstPendingFrame;
    private bool hasFlushed;

    public Pcm16AudioConverter(int inputSampleRate, int inputChannelCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(inputSampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(inputChannelCount, 0);
        this.inputSampleRate = inputSampleRate;
        this.inputChannelCount = inputChannelCount;

        // Downsampling needs the kernel stretched and the cutoff lowered to the output Nyquist
        // (anti-aliasing); upsampling keeps full input bandwidth and an unstretched kernel.
        double resampleRatio = (double)inputSampleRate / OutputSampleRate;
        double kernelStretchFactor = Math.Max(1.0, resampleRatio);
        kernelHalfWidthInInputFrames = SincZeroCrossingCountPerSide * kernelStretchFactor;
        kernelCutoffInCyclesPerInputSample = 0.5 / kernelStretchFactor;
    }

    /// <summary>
    /// Mixes interleaved input-channel float samples down to mono, resamples to 16 kHz and
    /// encodes every output frame that is fully determined by the input fed so far. Call
    /// repeatedly per capture packet; finish the capture session with
    /// <see cref="FlushRemainingAudio"/> so the final partial kernel windows get emitted.
    /// </summary>
    public byte[] ConvertInterleavedFloatSamples(ReadOnlySpan<float> interleavedFloatSamples)
    {
        ThrowIfAlreadyFlushed();
        if (interleavedFloatSamples.Length % inputChannelCount != 0)
        {
            throw new ArgumentException(
                $"The interleaved sample count ({interleavedFloatSamples.Length}) must be a multiple of the input channel count ({inputChannelCount}), otherwise frames misalign.",
                nameof(interleavedFloatSamples));
        }

        int inputFrameCount = interleavedFloatSamples.Length / inputChannelCount;
        if (inputFrameCount == 0)
        {
            return [];
        }

        // N-channel to mono mixdown by averaging. Averaging (rather than summing) is what keeps
        // a mix of full-scale channels inside [-1, 1], which the non-clipping test pins.
        for (int frameIndex = 0; frameIndex < inputFrameCount; frameIndex++)
        {
            float channelSampleSum = 0;
            for (int channelIndex = 0; channelIndex < inputChannelCount; channelIndex++)
            {
                channelSampleSum += interleavedFloatSamples[(frameIndex * inputChannelCount) + channelIndex];
            }
            pendingMonoInputFrames.Add(channelSampleSum / inputChannelCount);
        }
        totalInputFramesFed += inputFrameCount;

        return ProduceReadyOutputFrames(isFinalFlush: false);
    }

    /// <summary>
    /// Emits the trailing output frames whose kernel windows extend past the last fed input
    /// frame, treating the samples after the end of the capture session as silence. This is
    /// the key-up transition; the instance is spent afterwards (the macOS original also runs
    /// one converter per capture session), so create a new one for the next session.
    /// </summary>
    public byte[] FlushRemainingAudio()
    {
        ThrowIfAlreadyFlushed();
        hasFlushed = true;
        return ProduceReadyOutputFrames(isFinalFlush: true);
    }

    private void ThrowIfAlreadyFlushed()
    {
        if (hasFlushed)
        {
            throw new InvalidOperationException(
                "This converter was already flushed; create a new Pcm16AudioConverter for the next capture session.");
        }
    }

    private byte[] ProduceReadyOutputFrames(bool isFinalFlush)
    {
        // Exact rational floor: total output frames for all input fed so far. Integer math on
        // purpose - the acceptance tests pin this count, so no floating-point rounding here.
        long totalOutputFrameCount = totalInputFramesFed * OutputSampleRate / inputSampleRate;

        var pcm16LittleEndianBytes = new List<byte>();
        while (nextOutputFrameIndexToProduce < totalOutputFrameCount)
        {
            long rightmostInputIndexNeeded = (long)Math.Floor(
                (nextOutputFrameIndexToProduce * inputSampleRate / (double)OutputSampleRate)
                + kernelHalfWidthInInputFrames);
            if (!isFinalFlush && rightmostInputIndexNeeded > totalInputFramesFed - 1)
            {
                // More packets are coming; the kernel window is not fully fed yet. Zero-padding
                // the right side mid-stream would glitch every packet boundary.
                break;
            }

            float resampledMonoSample = ResampleOneOutputFrame(nextOutputFrameIndexToProduce);
            AppendPcm16LittleEndianSample(pcm16LittleEndianBytes, resampledMonoSample);
            nextOutputFrameIndexToProduce++;
        }

        DiscardPendingFramesNoLongerNeeded();
        return pcm16LittleEndianBytes.ToArray();
    }

    private float ResampleOneOutputFrame(long outputFrameIndex)
    {
        double centerPositionInInputFrames = outputFrameIndex * inputSampleRate / (double)OutputSampleRate;
        long firstInputIndexInWindow = (long)Math.Ceiling(centerPositionInInputFrames - kernelHalfWidthInInputFrames);
        long lastInputIndexInWindow = (long)Math.Floor(centerPositionInInputFrames + kernelHalfWidthInInputFrames);

        // A capture session starts and ends at true silence, so input outside
        // [0, totalInputFramesFed - 1] is zero (zero padding). Weights for those positions are
        // excluded from the normalization below as well, which holds the DC gain at exactly 1
        // even for the first and last output frames (the full-scale non-clipping test pins this).
        long firstSummedInputIndex = Math.Max(firstInputIndexInWindow, 0);
        long lastSummedInputIndex = Math.Min(lastInputIndexInWindow, totalInputFramesFed - 1);

        double weightedSampleSum = 0;
        double weightSum = 0;
        for (long inputIndex = firstSummedInputIndex; inputIndex <= lastSummedInputIndex; inputIndex++)
        {
            double kernelWeight = ComputeWindowedSincWeight(inputIndex - centerPositionInInputFrames);
            weightedSampleSum += ReadPendingMonoFrame(inputIndex) * kernelWeight;
            weightSum += kernelWeight;
        }

        // weightSum is always positive for any reachable output frame (the window always covers
        // fed input); the guard only exists so a future logic bug cannot divide by zero.
        return weightSum > 0 ? (float)(weightedSampleSum / weightSum) : 0;
    }

    private double ComputeWindowedSincWeight(double distanceInInputFrames)
    {
        double sincArgument = 2 * kernelCutoffInCyclesPerInputSample * distanceInInputFrames;
        double sincValue = sincArgument == 0
            ? 1
            : Math.Sin(Math.PI * sincArgument) / (Math.PI * sincArgument);

        // Blackman window over [-1, 1]: 0.42 + 0.5*cos(pi*s) + 0.08*cos(2*pi*s). Its first
        // side lobe sits 58 dB down, which with 16 zero crossings keeps aliased content far
        // below the speech the transcription cares about.
        double windowPosition = distanceInInputFrames / kernelHalfWidthInInputFrames;
        double blackmanWindowValue =
            0.42 + (0.5 * Math.Cos(Math.PI * windowPosition)) + (0.08 * Math.Cos(2 * Math.PI * windowPosition));

        // The 2*cutoff factor is the passband gain of the ideal low-pass at this cutoff.
        return 2 * kernelCutoffInCyclesPerInputSample * sincValue * blackmanWindowValue;
    }

    private float ReadPendingMonoFrame(long absoluteInputFrameIndex)
    {
        long indexInPendingList = absoluteInputFrameIndex - absoluteIndexOfFirstPendingFrame;
        if (indexInPendingList < 0 || indexInPendingList >= pendingMonoInputFrames.Count)
        {
            // Only a trimming bug can get here; silently returning 0 would corrupt the audio.
            throw new InvalidOperationException(
                $"Audio frame index {absoluteInputFrameIndex} is no longer pending "
                + $"(pending range starts at {absoluteIndexOfFirstPendingFrame} and holds {pendingMonoInputFrames.Count} frames).");
        }
        return pendingMonoInputFrames[(int)indexInPendingList];
    }

    private void DiscardPendingFramesNoLongerNeeded()
    {
        double nextOutputCenterPositionInInputFrames =
            nextOutputFrameIndexToProduce * inputSampleRate / (double)OutputSampleRate;
        long firstInputIndexStillNeeded = (long)Math.Ceiling(
            nextOutputCenterPositionInInputFrames - kernelHalfWidthInInputFrames) - 1;

        int frameCountToDiscard = (int)Math.Max(0, firstInputIndexStillNeeded - absoluteIndexOfFirstPendingFrame);
        if (frameCountToDiscard > 0)
        {
            pendingMonoInputFrames.RemoveRange(0, frameCountToDiscard);
            absoluteIndexOfFirstPendingFrame += frameCountToDiscard;
        }
    }

    private static void AppendPcm16LittleEndianSample(List<byte> pcm16LittleEndianBytes, float monoFloatSample)
    {
        // CoreAudio / AVAudioConverter float-to-int16 convention: scale by 32768 then clamp, so
        // +1.0 maps to +32767 (after clipping) and -1.0 to -32768. The clamp also guarantees a
        // sinc overshoot above +1.0 can never wrap around into the negative range.
        float clampedSample = Math.Clamp(monoFloatSample, -1f, 1f);
        int scaledSample = (int)MathF.Round(clampedSample * 32768f, MidpointRounding.AwayFromZero);
        int clampedPcm16Sample = Math.Clamp(scaledSample, short.MinValue, short.MaxValue);

        // Least significant byte first: AssemblyAI's pcm_s16le encoding.
        pcm16LittleEndianBytes.Add((byte)clampedPcm16Sample);
        pcm16LittleEndianBytes.Add((byte)(clampedPcm16Sample >> 8));
    }
}
