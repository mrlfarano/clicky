using Clicky.Core;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32.Foundation;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// WS-15 part 2 (task T-025, CLA-01, audit D1/D2): the OS-build branches of screen capture. Decide is pure, so
/// each build's choices are asserted here on any machine; device reuse runs against a fake device factory.
/// </summary>
public sealed class CaptureOptionsTests
{
    private const long Windows10Version1903Build = 18362;
    private const long Windows10Version2004Build = 19041;
    private const long WindowsServer2022Build = 20348;
    private const long Windows11Version24H2Build = 26100;

    [Fact]
    public void BorderlessOnlyOn20348Plus()
    {
        CaptureOptions windows10Options = CaptureOptions.Decide(Windows10Version2004Build);
        Assert.False(windows10Options.RequestBorderlessAccess);
        Assert.True(windows10Options.IsBorderRequired);

        CaptureOptions oneBuildBelowBorderlessOptions = CaptureOptions.Decide(WindowsServer2022Build - 1);
        Assert.False(oneBuildBelowBorderlessOptions.RequestBorderlessAccess);
        Assert.True(oneBuildBelowBorderlessOptions.IsBorderRequired);

        foreach (long borderlessBuild in new[] { WindowsServer2022Build, Windows11Version24H2Build })
        {
            CaptureOptions borderlessOptions = CaptureOptions.Decide(borderlessBuild);
            Assert.True(borderlessOptions.RequestBorderlessAccess, $"build {borderlessBuild}");
            Assert.False(borderlessOptions.IsBorderRequired, $"build {borderlessBuild}");
        }
    }

    [Fact]
    public void PanelGetsSameExclusionAsOverlay()
    {
        // The panel window does not exist yet (T-031+); a stand-in handle proves it goes down the overlay's path.
        var overlayWindowHandle = new HWND(0x1001);
        var panelWindowHandle = new HWND(0x2002);

        foreach (long excludingBuild in new[] { Windows10Version2004Build, Windows11Version24H2Build })
        {
            CaptureOptions excludingOptions = CaptureOptions.Decide(excludingBuild);
            Assert.Equal(OwnWindowCaptureExclusion.ExcludeFromCapture, excludingOptions.OwnWindowExclusion);
            var excludedWindowHandles = new List<HWND>();
            bool RecordExcludedWindow(HWND windowHandle)
            {
                excludedWindowHandles.Add(windowHandle);
                return true;
            }

            Assert.True(WindowCaptureExclusion.TryExcludeFromCapture(overlayWindowHandle, "overlay", excludingOptions, RecordExcludedWindow));
            Assert.True(WindowCaptureExclusion.TryExcludeFromCapture(panelWindowHandle, "panel", excludingOptions, RecordExcludedWindow));
            Assert.Equal([overlayWindowHandle, panelWindowHandle], excludedWindowHandles);
        }

        // 1903: WDA_EXCLUDEFROMCAPTURE is unsupported, so neither window is excluded and both must be hidden.
        CaptureOptions hidingOptions = CaptureOptions.Decide(Windows10Version1903Build);
        Assert.Equal(OwnWindowCaptureExclusion.HideDuringCapture, hidingOptions.OwnWindowExclusion);
        bool FailIfAffinityIsSet(HWND windowHandle) => throw new InvalidOperationException("SetWindowDisplayAffinity must not run below 19041");
        Assert.False(WindowCaptureExclusion.TryExcludeFromCapture(overlayWindowHandle, "overlay", hidingOptions, FailIfAffinityIsSet));
        Assert.False(WindowCaptureExclusion.TryExcludeFromCapture(panelWindowHandle, "panel", hidingOptions, FailIfAffinityIsSet));
    }

    [Fact]
    public void D3DDeviceReused()
    {
        int createdDeviceCount = 0;
        var screenCapturer = new MonitorScreenCapturer(() =>
        {
            createdDeviceCount++;
            return new FakeDirect3DDevice();
        });
        Assert.Equal(0, createdDeviceCount);

        // Each capture creates its frame pool on CaptureDevice; two captures read it twice.
        IDirect3DDevice firstCaptureDevice = screenCapturer.CaptureDevice;
        IDirect3DDevice secondCaptureDevice = screenCapturer.CaptureDevice;

        Assert.Same(firstCaptureDevice, secondCaptureDevice);
        Assert.Equal(1, createdDeviceCount);
    }

    private sealed partial class FakeDirect3DDevice : IDirect3DDevice
    {
        public void Trim()
        {
        }

        public void Dispose()
        {
        }
    }
}
