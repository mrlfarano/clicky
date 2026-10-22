using Clicky.Core;

namespace Clicky.Windows;

/// <summary>Where the updater looks (WS-10): the UpdateFeedUrl from appsettings.json and the release channel within it.</summary>
internal sealed record ClickyUpdateFeed(string FeedUrl, string Channel)
{
    /// <summary>WS-10's two Windows channels; the Mac Sparkle feed is separate and never touched.</summary>
    public static readonly IReadOnlyList<string> KnownUpdateChannels = ["win-beta", "win"];
}

/// <summary>
/// The one seam between <see cref="ClickyUpdateService"/> and Velopack, so every test drives the service
/// with a fake feed and no network. Each call to <see cref="CheckForUpdateAndDownloadAsync"/> is one
/// update request against the feed.
/// </summary>
internal interface IUpdateFeedClient
{
    /// <summary>Asks the feed for a newer release and downloads it in the background; true once one is staged.</summary>
    Task<bool> CheckForUpdateAndDownloadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Hands the staged release to Velopack's updater, which applies it once this process exits. With
    /// <paramref name="restartIntoUpdate"/> the updater relaunches the app and this call exits the process.
    /// </summary>
    void ApplyStagedUpdate(bool restartIntoUpdate);
}

/// <summary>
/// WS-10 auto-update orchestration over an injected feed client, voice-state provider and clock: checks at
/// startup and every 6 h, logs errors and retries on the next cycle, and applies a staged update only while
/// the companion is idle. With no feed configured it never creates a feed client, so no update request
/// can be made.
/// </summary>
internal sealed class ClickyUpdateService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly ClickyUpdateFeed? updateFeed;
    private readonly Func<ClickyUpdateFeed, IUpdateFeedClient> createUpdateFeedClient;
    private readonly ICompanionVoiceStateProvider companionVoiceStateProvider;
    private readonly TimeProvider timeProvider;
    private readonly Action<string> writeLogLine;
    private IUpdateFeedClient? updateFeedClient;

    public ClickyUpdateService(
        ClickyUpdateFeed? updateFeed,
        Func<ClickyUpdateFeed, IUpdateFeedClient> createUpdateFeedClient,
        ICompanionVoiceStateProvider companionVoiceStateProvider,
        TimeProvider timeProvider,
        Action<string> writeLogLine)
    {
        this.updateFeed = updateFeed;
        this.createUpdateFeedClient = createUpdateFeedClient;
        this.companionVoiceStateProvider = companionVoiceStateProvider;
        this.timeProvider = timeProvider;
        this.writeLogLine = writeLogLine;
    }

    public bool IsUpdateStaged { get; private set; }

    /// <summary>
    /// Checks once immediately, then every <see cref="CheckInterval"/> on the injected clock, until
    /// cancelled. Returns at once when UpdateFeedUrl is empty.
    /// </summary>
    public async Task RunUpdateChecksAsync(CancellationToken cancellationToken)
    {
        if (updateFeed is null)
        {
            writeLogLine("update: UpdateFeedUrl is empty; the updater is off");
            return;
        }
        writeLogLine($"update: checking {updateFeed.FeedUrl} on channel {updateFeed.Channel} every {CheckInterval.TotalHours} h");
        while (true)
        {
            try
            {
                // Created inside the try so a failing client constructor is logged and retried like a failed check.
                updateFeedClient ??= createUpdateFeedClient(updateFeed);
                if (await updateFeedClient.CheckForUpdateAndDownloadAsync(cancellationToken))
                {
                    IsUpdateStaged = true;
                    writeLogLine("update: a new release is downloaded and staged");
                }
            }
            catch (Exception updateCheckException) when (updateCheckException is not OperationCanceledException)
            {
                // WS-10: a failed check is not fatal; the next cycle simply tries again.
                writeLogLine($"update: check failed, retrying in {CheckInterval.TotalHours} h: {updateCheckException.Message}");
            }
            // The TimeProvider overload is what lets FakeTimeProvider fire the next cycle in tests.
            await Task.Delay(CheckInterval, timeProvider, cancellationToken);
        }
    }

    /// <summary>
    /// The "Restart to update" action: when a release is staged, waits until the voice state is idle (a
    /// restart mid-answer would cut the user off), then applies it. Does nothing when nothing is staged.
    /// </summary>
    public async Task ApplyStagedUpdateWhenIdleAsync(CancellationToken cancellationToken, bool restartIntoUpdate = true)
    {
        if (!IsUpdateStaged || updateFeedClient is null)
        {
            return;
        }

        var companionBecameIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CompleteWhenCompanionIsIdle()
        {
            if (companionVoiceStateProvider.CurrentVoiceState == CompanionVoiceState.Idle)
            {
                companionBecameIdle.TrySetResult();
            }
        }
        companionVoiceStateProvider.VoiceStateChanged += CompleteWhenCompanionIsIdle;
        try
        {
            // Checked after subscribing, so a change between the check and the subscription cannot be missed.
            CompleteWhenCompanionIsIdle();
            await companionBecameIdle.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            companionVoiceStateProvider.VoiceStateChanged -= CompleteWhenCompanionIsIdle;
        }
        writeLogLine(restartIntoUpdate
            ? "update: companion is idle; applying the staged release and restarting"
            : "update: companion is idle; the staged release applies when this process exits");
        updateFeedClient.ApplyStagedUpdate(restartIntoUpdate);
    }

    /// <summary>
    /// The --debug-update-now trigger (WS-10's CI N -> N+1 test): one check right now instead of waiting for
    /// the 6 h loop, then the same idle-gated apply, without restarting. The debug run installs no keyboard
    /// hook and starts no interaction, so the voice state is idle and the gate opens at once; keeping the
    /// gate means CI exercises the apply path users ride. A relaunch would start live mode on the runner, so
    /// the caller exits instead and Velopack applies the release behind it. True once the apply is handed
    /// over; a check failure propagates so the run fails loudly.
    /// </summary>
    public async Task<bool> CheckAndApplyNowAsync(CancellationToken cancellationToken)
    {
        if (updateFeed is null)
        {
            writeLogLine("update: UpdateFeedUrl is empty; --debug-update-now has no feed to check");
            return false;
        }
        writeLogLine($"update: --debug-update-now checking {updateFeed.FeedUrl} on channel {updateFeed.Channel}");
        updateFeedClient ??= createUpdateFeedClient(updateFeed);
        IsUpdateStaged = await updateFeedClient.CheckForUpdateAndDownloadAsync(cancellationToken);
        if (!IsUpdateStaged)
        {
            writeLogLine("update: --debug-update-now found no newer release");
            return false;
        }
        await ApplyStagedUpdateWhenIdleAsync(cancellationToken, restartIntoUpdate: false);
        return true;
    }
}
