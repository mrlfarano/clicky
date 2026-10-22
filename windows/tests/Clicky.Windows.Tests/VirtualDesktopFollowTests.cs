using System.Text.RegularExpressions;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-005 (audit E2, parity PNT-07): on EVENT_SYSTEM_FOREGROUND the overlay moves to the foreground window's
/// virtual desktop when it is not already there. Driven entirely through a fake desktop service and the
/// follower's foreground-event seam, so no test ever switches the user's real desktops.
/// </summary>
public sealed class VirtualDesktopFollowTests
{
    private const nint OverlayWindowHandle = 0x1001;
    private const nint ForegroundWindowHandle = 0x2002;
    private static readonly Guid FirstDesktopId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondDesktopId = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void MovesOverlayWhenOffDesktop()
    {
        var fakeVirtualDesktopService = new FakeVirtualDesktopService();
        fakeVirtualDesktopService.WindowDesktopIds[OverlayWindowHandle] = FirstDesktopId;
        fakeVirtualDesktopService.WindowDesktopIds[ForegroundWindowHandle] = SecondDesktopId;
        var virtualDesktopFollower = new VirtualDesktopFollower(fakeVirtualDesktopService, OverlayWindowHandle);

        virtualDesktopFollower.HandleForegroundWindowChanged(ForegroundWindowHandle);

        Assert.Equal([(OverlayWindowHandle, SecondDesktopId)], fakeVirtualDesktopService.MoveWindowToDesktopCalls);
    }

    [Fact]
    public void NoMoveWhenAlreadyCurrent()
    {
        var fakeVirtualDesktopService = new FakeVirtualDesktopService();
        fakeVirtualDesktopService.WindowDesktopIds[OverlayWindowHandle] = FirstDesktopId;
        fakeVirtualDesktopService.WindowDesktopIds[ForegroundWindowHandle] = FirstDesktopId;
        var virtualDesktopFollower = new VirtualDesktopFollower(fakeVirtualDesktopService, OverlayWindowHandle);

        virtualDesktopFollower.HandleForegroundWindowChanged(ForegroundWindowHandle);

        Assert.Empty(fakeVirtualDesktopService.MoveWindowToDesktopCalls);
    }

    // The taskbar and the desktop are on no virtual desktop, so focusing them must not move the overlay anywhere;
    // and an overlay on no desktop (the tool-window case the spike found) is already on every one.
    [Fact]
    public void NoMoveWhenEitherDesktopIdUnknown()
    {
        var foregroundOnNoDesktopService = new FakeVirtualDesktopService();
        foregroundOnNoDesktopService.WindowDesktopIds[OverlayWindowHandle] = FirstDesktopId;
        new VirtualDesktopFollower(foregroundOnNoDesktopService, OverlayWindowHandle).HandleForegroundWindowChanged(ForegroundWindowHandle);

        var overlayOnNoDesktopService = new FakeVirtualDesktopService();
        overlayOnNoDesktopService.WindowDesktopIds[ForegroundWindowHandle] = SecondDesktopId;
        new VirtualDesktopFollower(overlayOnNoDesktopService, OverlayWindowHandle).HandleForegroundWindowChanged(ForegroundWindowHandle);

        Assert.Empty(foregroundOnNoDesktopService.MoveWindowToDesktopCalls);
        Assert.Empty(overlayOnNoDesktopService.MoveWindowToDesktopCalls);
    }

    [Fact]
    public void AdrAddendumRecordsVerdict()
    {
        string adrMarkdown = File.ReadAllText(Path.Combine(
            ClickyReplayProcess.RepositoryRootDirectory, "docs", "adr", "001-windows-strategy.md"));

        // The addendum runs from its heading to the next level-2 heading (or the end of the file).
        Match addendumSection = Regex.Match(
            adrMarkdown, @"^## Addendum — virtual-desktop spike verdict\s*$(.*?)(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(addendumSection.Success, "ADR 001 must have a '## Addendum — virtual-desktop spike verdict' section");
        Assert.Matches(@"\*\*Verdict: (ADOPTED|FALLBACK)\*\*", addendumSection.Groups[1].Value);
    }

    private sealed class FakeVirtualDesktopService : IVirtualDesktopService
    {
        public Dictionary<nint, Guid> WindowDesktopIds { get; } = [];

        public List<(nint TopLevelWindowHandle, Guid DesktopId)> MoveWindowToDesktopCalls { get; } = [];

        public Guid? TryGetWindowDesktopId(nint topLevelWindowHandle) =>
            WindowDesktopIds.TryGetValue(topLevelWindowHandle, out Guid desktopId) ? desktopId : null;

        public bool TryMoveWindowToDesktop(nint topLevelWindowHandle, Guid desktopId)
        {
            MoveWindowToDesktopCalls.Add((topLevelWindowHandle, desktopId));
            return true;
        }
    }
}
