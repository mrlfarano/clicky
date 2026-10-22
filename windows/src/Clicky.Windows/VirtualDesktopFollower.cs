using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Clicky.Windows;

/// <summary>
/// The two documented IVirtualDesktopManager operations the follow behaviour needs (audit E2). Production
/// wires the shell's COM object; VirtualDesktopFollowTests substitute a fake, because a test must never
/// switch the user's real virtual desktops.
/// </summary>
internal interface IVirtualDesktopService
{
    /// <summary>The desktop the top-level window lives on, or null when it is on none or the shell cannot say
    /// (the taskbar, the desktop, tool and no-activate windows, hidden windows, and failures on some builds).</summary>
    Guid? TryGetWindowDesktopId(nint topLevelWindowHandle);

    /// <summary>Moves a window this process owns to the desktop; returns false when the shell refused.</summary>
    bool TryMoveWindowToDesktop(nint topLevelWindowHandle, Guid desktopId);
}

/// <summary>
/// The Mac pins the overlay to every Space (canJoinAllSpaces); Windows has no documented equivalent, so
/// on every foreground change the overlay moves to the foreground window's desktop when it is not there
/// already (audit E2, ADR 001 addendum "virtual-desktop spike verdict"). A Win+Ctrl+Arrow switch always
/// changes the foreground window, which is what makes the foreground event a usable "desktop switched" signal.
/// </summary>
internal sealed class VirtualDesktopFollower(IVirtualDesktopService virtualDesktopService, nint followedWindowHandle)
{
    /// <summary>
    /// The EVENT_SYSTEM_FOREGROUND handler; also the injection seam the tests drive directly, so no real
    /// foreground change or desktop switch is needed to exercise the follow decision.
    /// </summary>
    public void HandleForegroundWindowChanged(nint foregroundWindowHandle)
    {
        if (foregroundWindowHandle == 0 || foregroundWindowHandle == followedWindowHandle)
        {
            return;
        }
        if (virtualDesktopService.TryGetWindowDesktopId(foregroundWindowHandle) is not Guid foregroundDesktopId)
        {
            return;
        }
        // No desktop id means the shell assigns the window to no desktop and so never hides it on a switch:
        // the overlay's WS_EX_TOOLWINDOW / WS_EX_NOACTIVATE styles do exactly that (spike, ADR 001 addendum),
        // and moving it would be an S_OK no-op on every foreground change.
        if (virtualDesktopService.TryGetWindowDesktopId(followedWindowHandle) is not Guid followedWindowDesktopId
            || followedWindowDesktopId == foregroundDesktopId)
        {
            return;
        }
        if (virtualDesktopService.TryMoveWindowToDesktop(followedWindowHandle, foregroundDesktopId))
        {
            ClickyLog.Write($"virtual desktop: overlay moved to the foreground window's desktop {foregroundDesktopId}");
        }
    }
}

/// <summary>
/// The shell's documented IVirtualDesktopManager (shobjidl_core.h, Windows 10+). Every call is wrapped:
/// the COM class is missing on some SKUs (Server Core, some VMs) and its methods return failure HRESULTs
/// for windows the shell does not track, and neither may take the app down — the ADR fallback is simply
/// a buddy that stays on one desktop.
/// </summary>
internal sealed class WindowsVirtualDesktopService : IVirtualDesktopService
{
    private static readonly Guid VirtualDesktopManagerClassId = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");

    private readonly IVirtualDesktopManager virtualDesktopManager;

    private WindowsVirtualDesktopService(IVirtualDesktopManager virtualDesktopManager) =>
        this.virtualDesktopManager = virtualDesktopManager;

    /// <summary>Null when the shell's COM class cannot be created on this machine.</summary>
    public static WindowsVirtualDesktopService? TryCreate()
    {
        try
        {
            Type virtualDesktopManagerType = Type.GetTypeFromCLSID(VirtualDesktopManagerClassId, throwOnError: true)!;
            return new WindowsVirtualDesktopService((IVirtualDesktopManager)Activator.CreateInstance(virtualDesktopManagerType)!);
        }
        catch (Exception creationException) when (creationException is COMException or InvalidCastException)
        {
            ClickyLog.Write($"virtual desktop: IVirtualDesktopManager unavailable (0x{creationException.HResult:X8}); the overlay stays on one desktop");
            return null;
        }
    }

    public Guid? TryGetWindowDesktopId(nint topLevelWindowHandle)
    {
        // Failure here is routine (taskbar, desktop, windows mid-creation), so it is not logged.
        int resultCode = virtualDesktopManager.GetWindowDesktopId(topLevelWindowHandle, out Guid desktopId);
        return resultCode >= 0 && desktopId != Guid.Empty ? desktopId : null;
    }

    public bool TryMoveWindowToDesktop(nint topLevelWindowHandle, Guid desktopId)
    {
        int resultCode = virtualDesktopManager.MoveWindowToDesktop(topLevelWindowHandle, ref desktopId);
        if (resultCode < 0)
        {
            ClickyLog.Write($"virtual desktop: MoveWindowToDesktop failed (0x{resultCode:X8})");
        }
        return resultCode >= 0;
    }

    /// <summary>
    /// Declared by hand with PreserveSig so a failing HRESULT comes back as a value to check rather than a
    /// COMException thrown on every foreground change.
    /// </summary>
    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        // Never called, but COM binds methods by vtable position, so it has to be declared first.
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(nint topLevelWindowHandle, out int isOnCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(nint topLevelWindowHandle, out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(nint topLevelWindowHandle, ref Guid desktopId);
    }
}

/// <summary>
/// The EVENT_SYSTEM_FOREGROUND WinEvent hook. Out-of-context, so Windows delivers the callback through
/// the message loop of the thread that installed it (the WPF dispatcher thread), with no DLL injection.
/// </summary>
internal sealed class ForegroundWindowChangeHook : IDisposable
{
    // The native hook calls this delegate; the field keeps the GC from collecting it while the hook is live.
    private readonly WINEVENTPROC foregroundEventCallback;
    private readonly UnhookWinEventSafeHandle foregroundEventHookHandle;

    public ForegroundWindowChangeHook(Action<nint> handleForegroundWindowChanged)
    {
        foregroundEventCallback = (_, _, foregroundWindowHandle, _, _, _, _) => handleForegroundWindowChanged(foregroundWindowHandle);
        foregroundEventHookHandle = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_FOREGROUND,
            PInvoke.EVENT_SYSTEM_FOREGROUND,
            null,
            foregroundEventCallback,
            0,
            0,
            PInvoke.WINEVENT_OUTOFCONTEXT);
        if (foregroundEventHookHandle.IsInvalid)
        {
            ClickyLog.Write("virtual desktop: SetWinEventHook(EVENT_SYSTEM_FOREGROUND) failed; the overlay stays on one desktop");
        }
    }

    public void Dispose() => foregroundEventHookHandle.Dispose();
}
