using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Interop;
using System.Windows.Threading;
using Clicky.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-004: the overlay is ONE small content-sized window that moves (ADR 001 "UI framework", audit risk 1),
/// so these tests drive the real CursorOverlayWindow in-process on a dedicated STA thread (the shell is
/// visible via InternalsVisibleTo): the CompositionTarget.Rendering placement loop must call SetWindowPos
/// only when the placement actually changed, and WM_DISPLAYCHANGE must re-read the monitor list. They share
/// the process-replay collection so their window work never overlaps the latency-sensitive Clicky.exe runs.
/// </summary>
[Collection(ClickyProcessReplayCollection.Name)]
public sealed class OverlayMoveTests
{
    private const int WindowMessageDpiChanged = 0x02E0; // WM_DPICHANGED
    private const int WindowMessageDisplayChange = 0x007E; // WM_DISPLAYCHANGE
    private const int StationaryFrameCount = 60;
    private const int RescaledDpi = 144; // 150%, a scale the acceptance rig's secondary monitor uses

    [Fact]
    public void SetWindowPosOnlyOnChange()
    {
        RunOnOverlayStaThread(overlayWindow =>
        {
            var firstTargetPx = new System.Drawing.Point(300, 300);
            overlayWindow.PointAt(firstTargetPx, ReadMonitorDeviceNameAt(firstTargetPx));
            Assert.True(overlayWindow.SetWindowPosCallCount >= 1, "a changed placement must produce a SetWindowPos call");
            Assert.Equal(firstTargetPx, overlayWindow.ReadHotspotPositionPx());
            int setWindowPosCallsAfterMove = overlayWindow.SetWindowPosCallCount;

            // Stationary for 60 frames: the render loop re-checks the placement every tick and must issue
            // zero calls because the pending placement equals what was last applied.
            for (int frameIndex = 0; frameIndex < StationaryFrameCount; frameIndex++)
            {
                overlayWindow.SimulateCompositionTargetRenderingTickForTest();
            }
            int setWindowPosCallsWhileStationary = overlayWindow.SetWindowPosCallCount - setWindowPosCallsAfterMove;
            Assert.Equal(0, setWindowPosCallsWhileStationary);
            AssertWrittenReportSetWindowPosCallsIs(setWindowPosCallsWhileStationary);

            // The zero above is not a dead loop or a broken counter: a genuinely changed placement still
            // produces calls, and the window that follows it is again stationary at zero.
            var secondTargetPx = new System.Drawing.Point(600, 450);
            overlayWindow.PointAt(secondTargetPx, ReadMonitorDeviceNameAt(secondTargetPx));
            Assert.Equal(secondTargetPx, overlayWindow.ReadHotspotPositionPx());
            int setWindowPosCallsAfterSecondMove = overlayWindow.SetWindowPosCallCount;
            Assert.True(setWindowPosCallsAfterSecondMove > setWindowPosCallsAfterMove, "a second, different target must move the window again");
            for (int frameIndex = 0; frameIndex < StationaryFrameCount; frameIndex++)
            {
                overlayWindow.SimulateCompositionTargetRenderingTickForTest();
            }
            Assert.Equal(0, overlayWindow.SetWindowPosCallCount - setWindowPosCallsAfterSecondMove);
        });
    }

    [Fact]
    public void MonitorsReenumerated()
    {
        RunOnOverlayStaThread(overlayWindow =>
        {
            var targetPx = new System.Drawing.Point(200, 200);
            overlayWindow.PointAt(targetPx, ReadMonitorDeviceNameAt(targetPx));
            int monitorListReadsBeforeDisplayChange = overlayWindow.MonitorListReadCount;
            Assert.True(monitorListReadsBeforeDisplayChange >= 1, "targeting must have read the monitor list at least once");

            SendWindowMessage(overlayWindow, WindowMessageDisplayChange, wParam: 0, lParam: 0);

            Assert.True(
                overlayWindow.MonitorListReadCount > monitorListReadsBeforeDisplayChange,
                "WM_DISPLAYCHANGE must re-read the monitor list so the device-name lookup and the work-area clamp see fresh geometry");
        });
    }

    // T-004: WM_DPICHANGED must rescale the content-sized window (the glyph grows with the new DPI) while
    // the hotspot stays exactly on the target — the property that makes hotspotPx == targetPx hold on the
    // mixed-DPI acceptance rig.
    [Fact]
    public void DpiChangedRescalesContentSizedWindowAndKeepsHotspotOnTarget()
    {
        RunOnOverlayStaThread(overlayWindow =>
        {
            var targetPx = new System.Drawing.Point(400, 250);
            overlayWindow.PointAt(targetPx, ReadMonitorDeviceNameAt(targetPx));
            Assert.Equal(targetPx, overlayWindow.ReadHotspotPositionPx());
            (int widthBeforeDpiChange, int heightBeforeDpiChange) = ReadWindowSizePx(overlayWindow);

            SendDpiChangedMessageKeepingCurrentWindowRectAsSuggested(overlayWindow, RescaledDpi);
            // The handler queues the re-placement at Render priority so it runs after WPF's own DPI handling;
            // pump the dispatcher once at that priority to execute it.
            overlayWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            Assert.Equal(targetPx, overlayWindow.ReadHotspotPositionPx());
            (int widthAfterDpiChange, int heightAfterDpiChange) = ReadWindowSizePx(overlayWindow);
            // The bubble is empty in this test, so the window is exactly the glyph square: 18 DIP scaled by
            // the new 150% — independent of whatever DPI the test monitor itself reports.
            int expectedGlyphSidePx = (int)Math.Round(OverlayPlacement.GlyphSideLengthDip * (RescaledDpi / 96.0), MidpointRounding.AwayFromZero);
            Assert.Equal(expectedGlyphSidePx, widthAfterDpiChange);
            Assert.Equal(expectedGlyphSidePx, heightAfterDpiChange);
            Assert.True(widthAfterDpiChange > widthBeforeDpiChange && heightAfterDpiChange > heightBeforeDpiChange,
                $"content did not rescale for DPI {RescaledDpi}: {widthBeforeDpiChange}x{heightBeforeDpiChange} -> {widthAfterDpiChange}x{heightAfterDpiChange}");
        });
    }

    /// <summary>
    /// Runs the test body on a dedicated STA thread that owns the overlay window, because WPF needs an STA
    /// thread and the xUnit thread is not one. Same-thread SendMessage dispatches into the window procedure
    /// synchronously, so the tests need no message pump for the messages they send.
    /// </summary>
    private static void RunOnOverlayStaThread(Action<CursorOverlayWindow> runTestBodyWithOverlayWindow)
    {
        Exception? capturedException = null;
        var overlayStaThread = new Thread(() =>
        {
            try
            {
                // Match the app's Per-Monitor-V2 context (app.manifest) so WPF treats the window's DPI the
                // way the real shell does; the test host process itself carries no DPI manifest.
                PInvoke.SetThreadDpiAwarenessContext(global::Windows.Win32.UI.HiDpi.DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

                var overlayWindow = new CursorOverlayWindow();
                overlayWindow.CreateHiddenWindowHandle();
                runTestBodyWithOverlayWindow(overlayWindow);
            }
            catch (Exception testException)
            {
                capturedException = testException;
            }
        })
        { IsBackground = true, Name = "clicky-overlay-move-tests" };
        overlayStaThread.SetApartmentState(ApartmentState.STA);
        overlayStaThread.Start();
        overlayStaThread.Join();
        if (capturedException is not null)
        {
            ExceptionDispatchInfo.Capture(capturedException).Throw();
        }
    }

    /// <summary>The task's assertion surface is the report counter, so write a report carrying the stationary count and read the JSON back.</summary>
    private static void AssertWrittenReportSetWindowPosCallsIs(int expectedSetWindowPosCalls)
    {
        string stationaryReportPath = Path.Combine(Path.GetTempPath(), $"clicky-overlay-move-report-{Guid.NewGuid():N}.json");
        // EffectiveSettings is required on every report (T-002); this harness report carries defaults,
        // which the setWindowPosCalls assertion never reads.
        var stationaryReport = new TracerRunReport
        {
            Mode = "in-process overlay-move",
            EffectiveSettings = EffectiveSettingsReport.FromCompanionSettings(CompanionSettings.Defaults),
        };
        stationaryReport.SetWindowPosCalls = expectedSetWindowPosCalls;
        stationaryReport.WriteTo(stationaryReportPath);
        try
        {
            Assert.Equal(expectedSetWindowPosCalls, (int)JsonNode.Parse(File.ReadAllText(stationaryReportPath))!["setWindowPosCalls"]!);
        }
        finally
        {
            File.Delete(stationaryReportPath);
        }
    }

    private static string ReadMonitorDeviceNameAt(System.Drawing.Point pointPx) =>
        DisplayMonitorEnumerator.ReadDeviceName(PInvoke.MonitorFromPoint(pointPx, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST));

    private static HWND ReadOverlayWindowHandle(CursorOverlayWindow overlayWindow) =>
        (HWND)new WindowInteropHelper(overlayWindow).Handle;

    private static (int WidthPx, int HeightPx) ReadWindowSizePx(CursorOverlayWindow overlayWindow)
    {
        PInvoke.GetWindowRect(ReadOverlayWindowHandle(overlayWindow), out RECT windowRect);
        return (windowRect.right - windowRect.left, windowRect.bottom - windowRect.top);
    }

    private static void SendWindowMessage(CursorOverlayWindow overlayWindow, int message, nuint wParam, nint lParam) =>
        PInvoke.SendMessage(ReadOverlayWindowHandle(overlayWindow), (uint)message, wParam, lParam);

    /// <summary>
    /// Sends the WM_DPICHANGED the OS would send when the window's majority lands on a monitor with another
    /// DPI: both DPI words set to <paramref name="newDpi"/>, and the current window rect as Windows'
    /// suggested rectangle, so WPF's own handling has nothing the test does not already control.
    /// </summary>
    private static void SendDpiChangedMessageKeepingCurrentWindowRectAsSuggested(CursorOverlayWindow overlayWindow, int newDpi)
    {
        HWND windowHandle = ReadOverlayWindowHandle(overlayWindow);
        PInvoke.GetWindowRect(windowHandle, out RECT suggestedWindowRect);
        nint suggestedRectPointer = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        try
        {
            Marshal.StructureToPtr(suggestedWindowRect, suggestedRectPointer, fDeleteOld: false);
            nuint newDpiWords = (nuint)((newDpi & 0xFFFF) | (newDpi << 16)); // X DPI in the low word, Y DPI in the high word
            PInvoke.SendMessage(windowHandle, (uint)WindowMessageDpiChanged, newDpiWords, suggestedRectPointer);
        }
        finally
        {
            Marshal.FreeHGlobal(suggestedRectPointer);
        }
    }
}
