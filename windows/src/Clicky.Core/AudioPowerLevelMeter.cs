namespace Clicky.Core;

/// <summary>
/// Port of the waveform audio-level meter in <c>BuddyDictationManager.updateAudioPowerLevel</c>
/// (BuddyDictationManager.swift:687-721), audit row F3. The macOS original reads channel 0 of
/// the captured <c>AVAudioPCMBuffer</c>; the Windows shell passes that same channel's float
/// samples to <see cref="ProcessMonoFloatSamples"/>. Pure math: no Windows, WASAPI or NAudio
/// references, so its tests also run on the Linux CI job.
/// </summary>
public sealed class AudioPowerLevelMeter
{
    // RMS boost from BuddyDictationManager.swift:701: min(max(rootMeanSquare * 10.2, 0), 1).
    // A raw speech RMS sits near 0.05, so the waveform would be invisible without the boost.
    private const float AudioPowerLevelBoostFactor = 10.2f;

    // Per-buffer decay from BuddyDictationManager.swift:706-709:
    // max(boostedLevel, currentAudioPowerLevel * 0.72). The level jumps to a new peak
    // instantly but falls only 28% per buffer, which is what makes the waveform glide down
    // instead of flickering with every quiet sample.
    private const float AudioPowerLevelDecayFactorPerBuffer = 0.72f;

    /// <summary>The smoothed level the overlay waveform renders; 0..1.</summary>
    public float CurrentAudioPowerLevel { get; private set; }

    /// <summary>
    /// Root mean square of a buffer of float samples. Float accumulation on purpose: Swift
    /// accumulates into <c>var summedSquares: Float</c> (BuddyDictationManager.swift:694-700)
    /// and the acceptance test compares within 1e-6, so matching the operand type keeps the
    /// sequence identical. An empty buffer has no RMS in Swift (early return), so it reads as 0.
    /// </summary>
    public static float ComputeRootMeanSquare(ReadOnlySpan<float> floatSamples)
    {
        if (floatSamples.IsEmpty)
        {
            return 0;
        }

        float summedSquares = 0;
        foreach (float floatSample in floatSamples)
        {
            summedSquares += floatSample * floatSample;
        }
        return MathF.Sqrt(summedSquares / floatSamples.Length);
    }

    /// <summary>
    /// Feeds one capture buffer's first-channel samples and returns the new smoothed level.
    /// </summary>
    public float ProcessMonoFloatSamples(ReadOnlySpan<float> monoFloatSamples)
    {
        if (monoFloatSamples.IsEmpty)
        {
            // Swift returns early when frameCount == 0 (BuddyDictationManager.swift:692), so an
            // empty buffer must not tick the decay.
            return CurrentAudioPowerLevel;
        }

        float rootMeanSquare = ComputeRootMeanSquare(monoFloatSamples);

        // BuddyDictationManager.swift:701: min(max(rootMeanSquare * 10.2, 0), 1).
        float boostedAudioPowerLevel = Math.Clamp(rootMeanSquare * AudioPowerLevelBoostFactor, 0f, 1f);

        // BuddyDictationManager.swift:706-709: the smoothed level is the higher of the new
        // boosted level and the previous level decayed by 0.72.
        float smoothedAudioPowerLevel = Math.Max(
            boostedAudioPowerLevel,
            CurrentAudioPowerLevel * AudioPowerLevelDecayFactorPerBuffer);
        CurrentAudioPowerLevel = smoothedAudioPowerLevel;

        return smoothedAudioPowerLevel;
    }
}
