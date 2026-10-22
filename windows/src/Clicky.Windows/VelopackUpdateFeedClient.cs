using Velopack;
using Velopack.Sources;

namespace Clicky.Windows;

/// <summary>
/// The real <see cref="IUpdateFeedClient"/>: Velopack's UpdateManager on the configured channel. The
/// shipping feed is GitHub Releases (WS-10, GithubSource); any other URL or a local directory goes through
/// Velopack's plain web/file source, which is what the CI N -> N+1 test publishes to. Velopack downloads
/// deltas when the feed has them.
/// </summary>
internal sealed class VelopackUpdateFeedClient : IUpdateFeedClient
{
    private readonly UpdateManager updateManager;
    private UpdateInfo? stagedUpdateInfo;

    public VelopackUpdateFeedClient(ClickyUpdateFeed updateFeed)
    {
        var updateOptions = new UpdateOptions { ExplicitChannel = updateFeed.Channel };
        bool feedIsGitHubRepository = Uri.TryCreate(updateFeed.FeedUrl, UriKind.Absolute, out Uri? feedUri)
            && feedUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
        updateManager = feedIsGitHubRepository
            // win-beta releases are published as GitHub prereleases; the stable win channel ignores them.
            ? new UpdateManager(new GithubSource(updateFeed.FeedUrl, accessToken: null, prerelease: updateFeed.Channel == "win-beta"), updateOptions)
            : new UpdateManager(updateFeed.FeedUrl, updateOptions);
    }

    public async Task<bool> CheckForUpdateAndDownloadAsync(CancellationToken cancellationToken)
    {
        UpdateInfo? availableUpdateInfo = await updateManager.CheckForUpdatesAsync();
        if (availableUpdateInfo is null)
        {
            return false;
        }
        await updateManager.DownloadUpdatesAsync(availableUpdateInfo, progress: null, cancelToken: cancellationToken);
        stagedUpdateInfo = availableUpdateInfo;
        return true;
    }

    public void ApplyStagedUpdateAndRestart() => updateManager.ApplyUpdatesAndRestart(stagedUpdateInfo?.TargetFullRelease);
}
