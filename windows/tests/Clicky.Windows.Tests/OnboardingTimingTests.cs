using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-038 (WS-22 part 1): parity spec ONB-01 and ONB-05, rescoped by the owner decision of 2026-10-23 that drops
/// the onboarding video on Windows. What remains is the onboarding music on a virtual clock, the ff.mp3 content
/// file, and the decision itself recorded in the docs. Everything runs in-process through the silent replay
/// output, so no test opens an audio device or a window.
/// </summary>
public sealed class OnboardingTimingTests
{
    private static readonly string[] OwnerDecisionStatements =
    [
        "Owner decision 2026-10-23",
        "the onboarding video is dropped on Windows",
        "no video host",
        "no 40 s demo trigger",
        "`onboarding_video_completed` never fires",
    ];

    [Fact]
    public async Task MusicFadesOver3sAfter90s()
    {
        var fakeTimeProvider = new FakeTimeProvider();
        var musicAudioOutput = new SilentReplayAudioOutput(fakeTimeProvider);
        var onboardingMusicPlayer = new OnboardingMusicPlayer(musicAudioOutput, fakeTimeProvider);

        onboardingMusicPlayer.Start();
        Assert.True(musicAudioOutput.IsPlaying, "the music did not start");
        Assert.Equal(0.3f, musicAudioOutput.Volume);

        // CompanionManager.swift:249-256, 265-281: 0.3 until 90 s, then 30 linear steps of 0.1 s down to 0 at 93 s.
        TimeSpan virtualStep = TimeSpan.FromSeconds(0.1);
        TimeSpan elapsedSinceMusicStarted = TimeSpan.Zero;
        while (elapsedSinceMusicStarted < TimeSpan.FromSeconds(93))
        {
            fakeTimeProvider.Advance(virtualStep);
            elapsedSinceMusicStarted += virtualStep;
            double elapsedSeconds = elapsedSinceMusicStarted.TotalSeconds;
            double expectedVolume = elapsedSeconds <= 90 ? 0.3 : 0.3 * (93 - elapsedSeconds) / 3;
            await WaitUntilAsync(
                () => Math.Abs(musicAudioOutput.Volume - expectedVolume) < 0.0001,
                $"volume {expectedVolume:F3} at {elapsedSeconds:F1} s (was {musicAudioOutput.Volume:F3})");
            if (elapsedSeconds < 93)
            {
                Assert.True(musicAudioOutput.IsPlaying, $"the music stopped early at {elapsedSeconds:F1} s");
            }
        }

        // At 93 s the fade has reached 0 and the music is stopped, as player.stop() at :277.
        await WaitUntilAsync(() => !musicAudioOutput.IsPlaying, "the music to stop at 93 s");
        Assert.Equal(0f, musicAudioOutput.Volume);
    }

    [Fact]
    public void FfMp3IsContentFile()
    {
        string builtMusicFilePath = Path.Combine(Path.GetDirectoryName(ClickyReplayProcess.ClickyExecutablePath)!, "ff.mp3");
        Assert.True(File.Exists(builtMusicFilePath), $"ff.mp3 was not copied next to Clicky.exe: {builtMusicFilePath}");

        // The Mac bundle's own asset, byte for byte.
        byte[] macMusicBytes = File.ReadAllBytes(Path.Combine(ClickyReplayProcess.RepositoryRootDirectory, "leanring-buddy", "ff.mp3"));
        Assert.True(macMusicBytes.AsSpan().SequenceEqual(File.ReadAllBytes(builtMusicFilePath)), "the built ff.mp3 differs from leanring-buddy/ff.mp3");
    }

    [Fact]
    public void OwnerDecisionRecorded()
    {
        string paritySpecText = ReadRepositoryFile("docs", "02-parity-spec.md");
        string onboardingFlowScenario = SectionBetween(paritySpecText, "**ONB-01 ·", "**ONB-02 ·");
        string replayOnboardingScenario = SectionBetween(paritySpecText, "**ONB-05 ·", "## 8. Analytics");

        string strategyAdrText = ReadRepositoryFile("docs", "adr", "001-windows-strategy.md");
        string videoDroppedAddendum = SectionBetween(strategyAdrText, "## Addendum — onboarding video dropped", null);

        foreach (string ownerDecisionStatement in OwnerDecisionStatements)
        {
            Assert.Contains(ownerDecisionStatement, onboardingFlowScenario);
            Assert.Contains(ownerDecisionStatement, replayOnboardingScenario);
            Assert.Contains(ownerDecisionStatement, videoDroppedAddendum);
        }
    }

    private static string ReadRepositoryFile(params string[] pathSegmentsBelowRepositoryRoot) =>
        File.ReadAllText(Path.Combine([ClickyReplayProcess.RepositoryRootDirectory, .. pathSegmentsBelowRepositoryRoot]));

    /// <summary>The text from the start marker up to the end marker, or to the next top-level heading when it is null.</summary>
    private static string SectionBetween(string documentText, string startMarker, string? endMarker)
    {
        int sectionStartIndex = documentText.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(sectionStartIndex >= 0, $"the document has no '{startMarker}'");
        int sectionEndIndex = documentText.IndexOf(endMarker ?? "\n## ", sectionStartIndex + startMarker.Length, StringComparison.Ordinal);
        return sectionEndIndex < 0 ? documentText[sectionStartIndex..] : documentText[sectionStartIndex..sectionEndIndex];
    }

    /// <summary>
    /// FakeTimeProvider fires the fade's delay during Advance, but the awaiting continuation runs on the thread
    /// pool a moment later. Only the wait is wall clock; the volumes under assertion come from the virtual clock.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string conditionDescription)
    {
        var wallClockTimeout = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(wallClockTimeout.Elapsed < TimeSpan.FromSeconds(5), $"Timed out waiting for {conditionDescription}.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken);
        }
    }
}
