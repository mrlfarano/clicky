using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using Clicky.Core;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.Storage.Streams;
using WinRT;

namespace Clicky.Windows;

/// <summary>
/// One-shot monitor capture through Windows.Graphics.Capture (audit D1): start a session, keep the first
/// frame, dispose. Grabbing frames and encoding them are separate steps so that on 1903, where the overlay
/// has to be hidden for the capture, it only stays hidden for the grabs.
/// </summary>
internal sealed class MonitorScreenCapturer
{
    private const float JpegQuality = 0.8f; // CompanionScreenCaptureUtility.swift:99-100
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(3);

    // One D3D device for every capture of the process (task T-025): created on the first capture, not per capture.
    private readonly Lazy<IDirect3DDevice> sharedCaptureDevice;
    private Task<AppCapabilityAccessStatus>? borderlessAccessRequest;

    public MonitorScreenCapturer()
        : this(Direct3D11CaptureInterop.CreateDirect3DDevice)
    {
    }

    /// <param name="createCaptureDevice">Called at most once; tests pass a fake to count device creations.</param>
    public MonitorScreenCapturer(Func<IDirect3DDevice> createCaptureDevice)
    {
        sharedCaptureDevice = new Lazy<IDirect3DDevice>(createCaptureDevice);
    }

    /// <summary>The device every capture's frame pool is created on.</summary>
    public IDirect3DDevice CaptureDevice => sharedCaptureDevice.Value;

    public async Task<SoftwareBitmap> CaptureFirstFrameAsync(DisplayMonitor monitor, CancellationToken cancellationToken)
    {
        GraphicsCaptureItem captureItem = Direct3D11CaptureInterop.CreateCaptureItemForMonitor((IntPtr)monitor.Handle);
        using Direct3D11CaptureFramePool framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            CaptureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, captureItem.Size);
        using GraphicsCaptureSession captureSession = framePool.CreateCaptureSession(captureItem);
        await RemoveCaptureBorderWhereSupportedAsync(captureSession);

        var firstFrameSource = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        framePool.FrameArrived += (arrivedFramePool, _) =>
        {
            Direct3D11CaptureFrame? arrivedFrame = arrivedFramePool.TryGetNextFrame();
            if (arrivedFrame is not null && !firstFrameSource.TrySetResult(arrivedFrame))
            {
                arrivedFrame.Dispose();
            }
        };
        captureSession.StartCapture();

        using Direct3D11CaptureFrame firstFrame = await firstFrameSource.Task.WaitAsync(FirstFrameTimeout, cancellationToken);
        // Copying to a CPU bitmap ends our use of the GPU surface, so the session can be torn down right away.
        return await SoftwareBitmap.CreateCopyFromSurfaceAsync(firstFrame.Surface, BitmapAlphaMode.Ignore);
    }

    /// <summary>
    /// On 20348+ asks once per process for borderless access and, when granted, turns the yellow capture border off
    /// (audit D1). Older Windows 10 builds keep the border, an accepted limitation.
    /// </summary>
    private async Task RemoveCaptureBorderWhereSupportedAsync(GraphicsCaptureSession captureSession)
    {
        CaptureOptions captureOptions = WindowCaptureExclusion.CurrentCaptureOptions;
        // The OS check repeats CaptureOptions.Decide's 20348 threshold so the platform analyzer sees the guard.
        if (!captureOptions.RequestBorderlessAccess || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            return;
        }
        borderlessAccessRequest ??= RequestBorderlessAccessAsync();
        if (await borderlessAccessRequest == AppCapabilityAccessStatus.Allowed)
        {
            captureSession.IsBorderRequired = captureOptions.IsBorderRequired;
        }
    }

    [SupportedOSPlatform("windows10.0.20348")]
    private static async Task<AppCapabilityAccessStatus> RequestBorderlessAccessAsync()
    {
        try
        {
            AppCapabilityAccessStatus borderlessAccessStatus = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            ClickyLog.Write($"capture: borderless access {borderlessAccessStatus}");
            return borderlessAccessStatus;
        }
        catch (Exception borderlessAccessException)
        {
            // A bordered screenshot beats no screenshot: keep capturing with the border.
            ClickyLog.Write($"capture: borderless access request failed, keeping the border: {borderlessAccessException.Message}");
            return AppCapabilityAccessStatus.DeniedBySystem;
        }
    }

    /// <summary>Downscales to the planned screenshot size (1280 on the long edge) and encodes JPEG at quality 0.8 (audit D5).</summary>
    public static async Task<byte[]> EncodeScaledJpegAsync(SoftwareBitmap capturedFrame, PixelSize screenshotSize)
    {
        using var encodedImageStream = new InMemoryRandomAccessStream();
        var jpegEncoderOptions = new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue(JpegQuality, PropertyType.Single) },
        };
        BitmapEncoder jpegEncoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, encodedImageStream, jpegEncoderOptions);
        jpegEncoder.SetSoftwareBitmap(capturedFrame);
        jpegEncoder.BitmapTransform.ScaledWidth = (uint)screenshotSize.Width;
        jpegEncoder.BitmapTransform.ScaledHeight = (uint)screenshotSize.Height;
        jpegEncoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        await jpegEncoder.FlushAsync();

        using var jpegBytes = new MemoryStream();
        await encodedImageStream.GetInputStreamAt(0).AsStreamForRead().CopyToAsync(jpegBytes);
        return jpegBytes.ToArray();
    }

    /// <summary>
    /// Share of sampled pixels with any channel above 16. ADR 001 acceptance 7 calls a frame black when this
    /// is under 5%, which is how a failed or protected capture usually shows up.
    /// </summary>
    public static double MeasureNonBlackPixelRatio(SoftwareBitmap capturedFrame)
    {
        const int BytesPerBgraPixel = 4;
        const int SampleEveryNthPixel = 97; // prime, so samples do not line up with UI stripes or columns
        const byte BlackChannelThreshold = 16;

        var pixelBuffer = new global::Windows.Storage.Streams.Buffer((uint)(capturedFrame.PixelWidth * capturedFrame.PixelHeight * BytesPerBgraPixel));
        capturedFrame.CopyToBuffer(pixelBuffer);
        byte[] pixelBytes = pixelBuffer.ToArray();

        int sampledPixelCount = 0;
        int nonBlackPixelCount = 0;
        for (int pixelStart = 0; pixelStart + 2 < pixelBytes.Length; pixelStart += SampleEveryNthPixel * BytesPerBgraPixel)
        {
            sampledPixelCount++;
            if (pixelBytes[pixelStart] > BlackChannelThreshold
                || pixelBytes[pixelStart + 1] > BlackChannelThreshold
                || pixelBytes[pixelStart + 2] > BlackChannelThreshold)
            {
                nonBlackPixelCount++;
            }
        }
        return sampledPixelCount == 0 ? 0 : (double)nonBlackPixelCount / sampledPixelCount;
    }

    /// <summary>
    /// The two COM entry points that hand WinRT capture objects across from Win32 handles. Declared by hand
    /// because they return raw WinRT pointers for C#/WinRT to wrap, which CsWin32's COM projection cannot do.
    /// </summary>
    private static class Direct3D11CaptureInterop
    {
        private static readonly Guid GraphicsCaptureItemInterfaceId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        private static readonly Guid DxgiDeviceInterfaceId = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

        [ComImport]
        [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            // Never called, but COM binds methods by vtable position, so it has to be declared first.
            IntPtr CreateForWindow([In] IntPtr windowHandle, [In] ref Guid interfaceId);

            IntPtr CreateForMonitor([In] IntPtr monitorHandle, [In] ref Guid interfaceId);
        }

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(
            IntPtr adapter,
            int driverType,
            IntPtr softwareRasterizer,
            uint creationFlags,
            IntPtr featureLevels,
            uint featureLevelCount,
            uint sdkVersion,
            out IntPtr device,
            out int obtainedFeatureLevel,
            out IntPtr immediateContext);

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        public static GraphicsCaptureItem CreateCaptureItemForMonitor(IntPtr monitorHandle)
        {
            var captureItemInterop = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem")
                .AsInterface<IGraphicsCaptureItemInterop>();
            Guid captureItemInterfaceId = GraphicsCaptureItemInterfaceId;
            IntPtr captureItemPointer = captureItemInterop.CreateForMonitor(monitorHandle, ref captureItemInterfaceId);
            try
            {
                return GraphicsCaptureItem.FromAbi(captureItemPointer);
            }
            finally
            {
                Marshal.Release(captureItemPointer);
            }
        }

        public static IDirect3DDevice CreateDirect3DDevice()
        {
            const int HardwareDriverType = 1; // D3D_DRIVER_TYPE_HARDWARE
            const int WarpDriverType = 5; // D3D_DRIVER_TYPE_WARP, the software rasterizer
            const uint BgraSupportFlag = 0x20; // D3D11_CREATE_DEVICE_BGRA_SUPPORT, required by Windows.Graphics.Capture
            const uint Direct3D11SdkVersion = 7; // D3D11_SDK_VERSION

            int createDeviceResult = D3D11CreateDevice(
                IntPtr.Zero, HardwareDriverType, IntPtr.Zero, BgraSupportFlag, IntPtr.Zero, 0, Direct3D11SdkVersion,
                out IntPtr direct3DDevice, out _, out IntPtr immediateContext);
            if (createDeviceResult < 0)
            {
                // VMs and CI runners often have no GPU adapter; WARP still produces capture frames there.
                createDeviceResult = D3D11CreateDevice(
                    IntPtr.Zero, WarpDriverType, IntPtr.Zero, BgraSupportFlag, IntPtr.Zero, 0, Direct3D11SdkVersion,
                    out direct3DDevice, out _, out immediateContext);
            }
            Marshal.ThrowExceptionForHR(createDeviceResult);

            try
            {
                Guid dxgiDeviceInterfaceId = DxgiDeviceInterfaceId;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(direct3DDevice, in dxgiDeviceInterfaceId, out IntPtr dxgiDevice));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr winRTDevice));
                    try
                    {
                        return MarshalInterface<IDirect3DDevice>.FromAbi(winRTDevice);
                    }
                    finally
                    {
                        Marshal.Release(winRTDevice);
                    }
                }
                finally
                {
                    Marshal.Release(dxgiDevice);
                }
            }
            finally
            {
                Marshal.Release(immediateContext);
                Marshal.Release(direct3DDevice);
            }
        }
    }
}
