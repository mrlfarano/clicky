using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Clicky.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// WS-10 part 1 (task T-035): the update service in-process, driven entirely by a fake feed client and a
/// FakeTimeProvider (the ReplayManifestTests.TimersUseInjectedClock pattern), so no test touches a feed or
/// waits out the 6 h interval. The real Velopack client needs a published feed and is covered by WS-10's
/// N -> N+1 CI run.
/// </summary>
public sealed class UpdateServiceTests
{
    private const string ProjectGitHubFeedUrl = "https://github.com/mrlfarano/clicky";

    [Fact]
    public async Task EmptyFeedMakesNoRequest()
    {
        // The shipped appsettings.json is the "UpdateFeedUrl empty" case: the updater is off by default.
        ClickySettings shippedSettings = ClickySettings.LoadFromFile(ShippedAppSettingsFilePath);
        Assert.Equal("", shippedSettings.UpdateFeedUrl);
        Assert.Null(shippedSettings.TryGetUpdateFeed());

        var fakeTimeProvider = new FakeTimeProvider();
        int createdUpdateFeedClientCount = 0;
        var fakeUpdateFeedClient = new FakeUpdateFeedClient(fakeTimeProvider);
        var loggedLines = new ConcurrentQueue<string>();
        var updateService = new ClickyUpdateService(
            shippedSettings.TryGetUpdateFeed(),
            _ => { createdUpdateFeedClientCount++; return fakeUpdateFeedClient; },
            new CompanionVoiceStateReporter(),
            fakeTimeProvider,
            loggedLines.Enqueue);

        // A full cycle: the startup check, two 6 h ticks, and a "Restart to update" request.
        await updateService.RunUpdateChecksAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        fakeTimeProvider.Advance(ClickyUpdateService.CheckInterval * 2);
        await updateService.ApplyStagedUpdateWhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, createdUpdateFeedClientCount);
        Assert.Empty(fakeUpdateFeedClient.UpdateRequestVirtualTimes);
        Assert.Equal(0, fakeUpdateFeedClient.ApplyCount);
        Assert.Contains(loggedLines, loggedLine => loggedLine.Contains("UpdateFeedUrl is empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppliesOnlyWhenIdle()
    {
        var fakeTimeProvider = new FakeTimeProvider();
        var fakeUpdateFeedClient = new FakeUpdateFeedClient(fakeTimeProvider) { ReportsUpdateStaged = true };
        var companionVoiceStateReporter = new CompanionVoiceStateReporter();
        companionVoiceStateReporter.ReportVoiceState(CompanionVoiceState.Responding);
        var updateService = new ClickyUpdateService(
            new ClickyUpdateFeed(ProjectGitHubFeedUrl, "win-beta"),
            _ => fakeUpdateFeedClient,
            companionVoiceStateReporter,
            fakeTimeProvider,
            _ => { });

        using var updateChecksCancellation = new CancellationTokenSource();
        Task updateChecksTask = updateService.RunUpdateChecksAsync(updateChecksCancellation.Token);
        await WaitUntilAsync(() => updateService.IsUpdateStaged, "the startup check to stage the update");

        // Mid-answer the apply request waits instead of restarting...
        Task applyTask = updateService.ApplyStagedUpdateWhenIdleAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.False(applyTask.IsCompleted, "the update applied while the companion was responding");

        // ...through every non-idle state...
        companionVoiceStateReporter.ReportVoiceState(CompanionVoiceState.Listening);
        companionVoiceStateReporter.ReportVoiceState(CompanionVoiceState.Processing);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.False(applyTask.IsCompleted, "the update applied while the companion was not idle");
        Assert.Equal(0, fakeUpdateFeedClient.ApplyCount);

        // ...and applies exactly once as soon as the companion is idle.
        companionVoiceStateReporter.ReportVoiceState(CompanionVoiceState.Idle);
        await applyTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, fakeUpdateFeedClient.ApplyCount);

        updateChecksCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updateChecksTask);
    }

    [Fact]
    public async Task ChecksAtStartupAndEverySixHours()
    {
        var fakeTimeProvider = new FakeTimeProvider();
        // The startup check fails; the service must log it and simply try again on the next cycle.
        var fakeUpdateFeedClient = new FakeUpdateFeedClient(fakeTimeProvider) { FailingRequestCount = 1 };
        var loggedLines = new ConcurrentQueue<string>();
        var updateService = new ClickyUpdateService(
            new ClickyUpdateFeed(ProjectGitHubFeedUrl, "win-beta"),
            _ => fakeUpdateFeedClient,
            new CompanionVoiceStateReporter(),
            fakeTimeProvider,
            loggedLines.Enqueue);
        DateTimeOffset startupVirtualTime = fakeTimeProvider.GetUtcNow();

        using var updateChecksCancellation = new CancellationTokenSource();
        Task updateChecksTask = updateService.RunUpdateChecksAsync(updateChecksCancellation.Token);

        // 0 h: the startup check ran (and failed) before the first wait, and the failure is logged.
        await WaitUntilAsync(() => fakeUpdateFeedClient.UpdateRequestVirtualTimes.Count == 1, "the startup check");
        Assert.Contains(loggedLines, loggedLine => loggedLine.Contains("check failed", StringComparison.Ordinal)
            && loggedLine.Contains(FakeUpdateFeedClient.FailureMessage, StringComparison.Ordinal));
        Assert.False(updateChecksTask.IsFaulted, "a failed check stopped the update loop");

        // Nothing more until the full 6 h have elapsed...
        fakeTimeProvider.Advance(ClickyUpdateService.CheckInterval - TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.Single(fakeUpdateFeedClient.UpdateRequestVirtualTimes);

        // ...then the retry fires at exactly 6 h, and the next one at 12 h.
        fakeTimeProvider.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => fakeUpdateFeedClient.UpdateRequestVirtualTimes.Count == 2, "the 6 h check");
        // The 6 h check ran on a pool thread; give the loop a moment to arm its next delay before advancing.
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        fakeTimeProvider.Advance(ClickyUpdateService.CheckInterval);
        await WaitUntilAsync(() => fakeUpdateFeedClient.UpdateRequestVirtualTimes.Count == 3, "the 12 h check");

        Assert.Equal(
            new[] { TimeSpan.Zero, TimeSpan.FromHours(6), TimeSpan.FromHours(12) },
            fakeUpdateFeedClient.UpdateRequestVirtualTimes.Select(requestVirtualTime => requestVirtualTime - startupVirtualTime));

        updateChecksCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updateChecksTask);
    }

    [Theory]
    [InlineData("win-beta")]
    [InlineData("win")]
    public async Task ChannelSelectedFromConfig(string configuredUpdateChannel)
    {
        string appSettingsFilePath = WriteAppSettingsVariant(ProjectGitHubFeedUrl, configuredUpdateChannel);
        try
        {
            ClickySettings configuredSettings = ClickySettings.LoadFromFile(appSettingsFilePath);

            var fakeTimeProvider = new FakeTimeProvider();
            var fakeUpdateFeedClient = new FakeUpdateFeedClient(fakeTimeProvider);
            var feedsUpdateClientsWereCreatedFor = new ConcurrentQueue<ClickyUpdateFeed>();
            var updateService = new ClickyUpdateService(
                configuredSettings.TryGetUpdateFeed(),
                updateFeed => { feedsUpdateClientsWereCreatedFor.Enqueue(updateFeed); return fakeUpdateFeedClient; },
                new CompanionVoiceStateReporter(),
                fakeTimeProvider,
                _ => { });

            using var updateChecksCancellation = new CancellationTokenSource();
            Task updateChecksTask = updateService.RunUpdateChecksAsync(updateChecksCancellation.Token);
            await WaitUntilAsync(() => fakeUpdateFeedClient.UpdateRequestVirtualTimes.Count == 1, "the startup check");

            Assert.Equal(new ClickyUpdateFeed(ProjectGitHubFeedUrl, configuredUpdateChannel), Assert.Single(feedsUpdateClientsWereCreatedFor));

            updateChecksCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updateChecksTask);
        }
        finally
        {
            File.Delete(appSettingsFilePath);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DebugUpdateNowChecksOnceAndAppliesWithoutRestart(bool feedHasNewerRelease)
    {
        // --debug-update-now (T-036): one check right now, and an apply without relaunching only when it staged a release.
        var fakeTimeProvider = new FakeTimeProvider();
        var fakeUpdateFeedClient = new FakeUpdateFeedClient(fakeTimeProvider) { ReportsUpdateStaged = feedHasNewerRelease };
        var updateService = new ClickyUpdateService(
            new ClickyUpdateFeed(@"C:\clicky-update-feed", "win-beta"),
            _ => fakeUpdateFeedClient,
            new CompanionVoiceStateReporter(),
            fakeTimeProvider,
            _ => { });

        bool updateHandedOver = await updateService.CheckAndApplyNowAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(feedHasNewerRelease, updateHandedOver);
        Assert.Single(fakeUpdateFeedClient.UpdateRequestVirtualTimes);
        Assert.Equal(feedHasNewerRelease ? 1 : 0, fakeUpdateFeedClient.ApplyCount);
        Assert.Equal(feedHasNewerRelease ? (bool?)false : null, fakeUpdateFeedClient.LastApplyRestartedIntoUpdate);
    }

    [Fact]
    public void UnknownChannelIsRejectedAtLoad()
    {
        // The Mac's Sparkle channel is not a Windows feed; a typo must fail startup, not silently poll nothing.
        string appSettingsFilePath = WriteAppSettingsVariant(ProjectGitHubFeedUrl, "mac");
        try
        {
            Assert.Throws<InvalidDataException>(() => ClickySettings.LoadFromFile(appSettingsFilePath));
        }
        finally
        {
            File.Delete(appSettingsFilePath);
        }
    }

    private static string ShippedAppSettingsFilePath => Path.Combine(
        ClickyReplayProcess.RepositoryRootDirectory, "windows", "src", "Clicky.Windows", "appsettings.json");

    /// <summary>The shipped file with only the update keys changed, so the rest still passes load validation.</summary>
    private static string WriteAppSettingsVariant(string updateFeedUrl, string updateChannel)
    {
        JsonNode appSettings = JsonNode.Parse(File.ReadAllText(ShippedAppSettingsFilePath))!;
        appSettings["UpdateFeedUrl"] = updateFeedUrl;
        appSettings["UpdateChannel"] = updateChannel;
        string appSettingsFilePath = Path.Combine(Path.GetTempPath(), $"clicky-appsettings-{Guid.NewGuid():N}.json");
        File.WriteAllText(appSettingsFilePath, appSettings.ToJsonString());
        return appSettingsFilePath;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string conditionDescription)
    {
        var wallClockTimeout = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(
                wallClockTimeout.Elapsed < TimeSpan.FromSeconds(5),
                $"Timed out waiting for {conditionDescription}.");
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    /// <summary>Records every update request at its virtual time; the first <see cref="FailingRequestCount"/> requests throw.</summary>
    private sealed class FakeUpdateFeedClient(FakeTimeProvider fakeTimeProvider) : IUpdateFeedClient
    {
        public const string FailureMessage = "fake feed unreachable";

        private int applyCount;

        public ConcurrentQueue<DateTimeOffset> UpdateRequestVirtualTimes { get; } = new();

        public int FailingRequestCount { get; init; }

        public bool ReportsUpdateStaged { get; init; }

        public int ApplyCount => Volatile.Read(ref applyCount);

        public Task<bool> CheckForUpdateAndDownloadAsync(CancellationToken cancellationToken)
        {
            UpdateRequestVirtualTimes.Enqueue(fakeTimeProvider.GetUtcNow());
            if (UpdateRequestVirtualTimes.Count <= FailingRequestCount)
            {
                throw new HttpRequestException(FailureMessage);
            }
            return Task.FromResult(ReportsUpdateStaged);
        }

        public bool? LastApplyRestartedIntoUpdate { get; private set; }

        public void ApplyStagedUpdate(bool restartIntoUpdate)
        {
            LastApplyRestartedIntoUpdate = restartIntoUpdate;
            Interlocked.Increment(ref applyCount);
        }
    }
}
