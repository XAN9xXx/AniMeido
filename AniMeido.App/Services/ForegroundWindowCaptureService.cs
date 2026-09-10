using AniMeido.Contracts.Desktop;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AniMeido.App.Services;

public sealed class ForegroundWindowCaptureService :
    IForegroundWindowCaptureService
{
    private static readonly Guid GraphicsCaptureItemGuid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public bool IsSupported => GraphicsCaptureSession.IsSupported()
        && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362);

    public async Task<ForegroundWindowCapture> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            throw new NotSupportedException(
                "当前 Windows 版本不支持前台窗口捕获。");
        }

        var window = GetForegroundWindow();
        if (window == 0 || IsIconic(window))
        {
            throw new InvalidOperationException("当前没有可捕获的前台窗口。");
        }

        var title = GetWindowTitle(window);
        _ = GetWindowThreadProcessId(window, out var processId);
        var processName = TryGetProcessName(processId);
        var item = CreateCaptureItem(window);
        if (item.Size.Width <= 0 || item.Size.Height <= 0)
        {
            throw new InvalidOperationException("前台窗口没有可捕获的画面。");
        }

        using var device = CreateDirect3DDevice();
        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            item.Size);
        using var session = framePool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        var completion =
            new TaskCompletionSource<Direct3D11CaptureFrame>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrameArrived(
            Direct3D11CaptureFramePool sender,
            object args)
        {
            var frame = sender.TryGetNextFrame();
            if (!completion.TrySetResult(frame))
            {
                frame.Dispose();
            }
        }

        framePool.FrameArrived += OnFrameArrived;
        session.StartCapture();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var registration = timeout.Token.Register(() =>
            completion.TrySetCanceled(timeout.Token));
        using var capturedFrame = await completion.Task;
        framePool.FrameArrived -= OnFrameArrived;

        using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
            capturedFrame.Surface);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.PngEncoderId,
            stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        stream.Seek(0);
        var length = checked((int)stream.Size);
        var bytes = new byte[length];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)length);
        reader.ReadBytes(bytes);

        return new ForegroundWindowCapture(
            bytes,
            title,
            processName,
            capturedFrame.ContentSize.Width,
            capturedFrame.ContentSize.Height,
            DateTimeOffset.UtcNow);
    }

    private static GraphicsCaptureItem CreateCaptureItem(nint window)
    {
        var factory = WinRT.ActivationFactory.Get(
            "Windows.Graphics.Capture.GraphicsCaptureItem");
        var interopGuid = typeof(IGraphicsCaptureItemInterop).GUID;
        var result = Marshal.QueryInterface(
            factory.ThisPtr,
            ref interopGuid,
            out var interopPointer);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            var interop = (IGraphicsCaptureItemInterop)
                Marshal.GetObjectForIUnknown(interopPointer);
            try
            {
                result = interop.CreateForWindow(
                    window,
                    GraphicsCaptureItemGuid,
                    out var captureItem);
                Marshal.ThrowExceptionForHR(result);
                try
                {
                    return WinRT.MarshalInterface<GraphicsCaptureItem>
                        .FromAbi(captureItem);
                }
                finally
                {
                    Marshal.Release(captureItem);
                }
            }
            finally
            {
                _ = Marshal.ReleaseComObject(interop);
            }
        }
        finally
        {
            Marshal.Release(interopPointer);
        }
    }

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        const uint createDeviceBgraSupport = 0x20;
        const uint d3d11SdkVersion = 7;
        const int hardwareDriver = 1;
        var result = D3D11CreateDevice(
            0,
            hardwareDriver,
            0,
            createDeviceBgraSupport,
            0,
            0,
            d3d11SdkVersion,
            out var d3dDevice,
            out _,
            out var deviceContext);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            var dxgiGuid = new Guid(
                "54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            result = Marshal.QueryInterface(
                d3dDevice,
                ref dxgiGuid,
                out var dxgiDevice);
            Marshal.ThrowExceptionForHR(result);
            try
            {
                result = CreateDirect3D11DeviceFromDXGIDevice(
                    dxgiDevice,
                    out var inspectable);
                Marshal.ThrowExceptionForHR(result);
                try
                {
                    return WinRT.MarshalInterface<IDirect3DDevice>
                        .FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            if (deviceContext != 0)
            {
                Marshal.Release(deviceContext);
            }
            if (d3dDevice != 0)
            {
                Marshal.Release(d3dDevice);
            }
        }
    }

    private static string GetWindowTitle(nint window)
    {
        var length = GetWindowTextLength(window);
        var buffer = new StringBuilder(Math.Max(1, length + 1));
        _ = GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string TryGetProcessName(uint processId)
    {
        try
        {
            return Process.GetProcessById((int)processId).ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(
            nint window,
            in Guid iid,
            out nint result);

        [PreserveSig]
        int CreateForMonitor(
            nint monitor,
            in Guid iid,
            out nint result);
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out int featureLevel,
        out nint immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        nint dxgiDevice,
        out nint graphicsDevice);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        nint window,
        StringBuilder text,
        int maximum);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);
}
