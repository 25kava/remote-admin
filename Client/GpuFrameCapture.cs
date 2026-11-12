using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;
using RemoteAdmin.Shared;

internal sealed class GpuFrameCapture : IDisposable
{
    private readonly object gate = new();
    private IDirect3DDevice? device;
    private Direct3D11CaptureFramePool? pool;
    private GraphicsCaptureSession? session;
    private Direct3D11CaptureFrame? latest;
    private TaskCompletionSource ready = Signal();
    private CancellationTokenRegistration cancellation;
    private bool disposed;
    private string? previousJpeg;
    private int previousWidth, previousHeight;
    private Windows.Graphics.SizeInt32 poolSize;
    public bool IsDisposed { get { lock (gate) return disposed; } }

    public GpuFrameCapture(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!DesktopScreenSharing.IsInteractiveDesktop()) throw new InvalidOperationException("The desktop is locked or unavailable.");
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows Graphics Capture is unavailable.");
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out var native, out _, out var context));
            try
            {
                if (context != IntPtr.Zero) Marshal.Release(context);
                var dxgiGuid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in dxgiGuid, out var dxgi));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var projected));
                    try { device = MarshalInterface<IDirect3DDevice>.FromAbi(projected); } finally { Marshal.Release(projected); }
                }
                finally { Marshal.Release(dxgi); }
            }
            finally { Marshal.Release(native); }
            var item = CreateItem();
            poolSize = item.Size;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, item.Size);
            pool.FrameArrived += FrameArrived;
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = true;
            // Keep the OS capture border and all platform privacy protections enabled.
            token.ThrowIfCancellationRequested();
            session.StartCapture();
            cancellation = token.Register(Dispose);
        }
        catch { Dispose(); throw; }
    }

    public async Task<ScreenFrame> CaptureAsync(string id, long sequence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!DesktopScreenSharing.IsInteractiveDesktop()) throw new InvalidOperationException("The desktop is locked or unavailable.");
        Direct3D11CaptureFrame? frame;
        Task wait;
        string? cached;
        int cachedWidth, cachedHeight;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            frame = latest; latest = null; wait = ready.Task;
            cached = previousJpeg; cachedWidth = previousWidth; cachedHeight = previousHeight;
            if (frame is not null) ready = Signal();
        }
        if (frame is null)
        {
            if (cached is not null) return new(id, cachedWidth, cachedHeight, cached, sequence, DateTime.UtcNow);
            await wait.WaitAsync(TimeSpan.FromSeconds(3), token);
            return await CaptureAsync(id, sequence, token);
        }
        using (frame)
        {
            if (frame.ContentSize.Width != poolSize.Width || frame.ContentSize.Height != poolSize.Height)
                throw new InvalidOperationException("The display size changed. Start a new screen session.");
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore).AsTask(token);
            var scale = Math.Min(1d, Math.Min(1280d / frame.ContentSize.Width, 720d / frame.ContentSize.Height));
            var width = Math.Max(1, (int)Math.Round(frame.ContentSize.Width * scale));
            var height = Math.Max(1, (int)Math.Round(frame.ContentSize.Height * scale));
            using var output = new InMemoryRandomAccessStream();
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(.65f, Windows.Foundation.PropertyType.Single) };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options).AsTask(token);
            encoder.SetSoftwareBitmap(bitmap);
            encoder.BitmapTransform.ScaledWidth = (uint)width; encoder.BitmapTransform.ScaledHeight = (uint)height;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
            await encoder.FlushAsync().AsTask(token);
            using var reader = new DataReader(output.GetInputStreamAt(0));
            await reader.LoadAsync((uint)output.Size).AsTask(token);
            var data = new byte[(int)output.Size]; reader.ReadBytes(data);
            token.ThrowIfCancellationRequested();
            var encoded = Convert.ToBase64String(data);
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                previousJpeg = encoded; previousWidth = width; previousHeight = height;
            }
            return new(id, width, height, encoded, sequence, DateTime.UtcNow);
        }
    }

    private void FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame(); if (frame is null) return;
            lock (gate)
            {
                if (disposed) return;
                latest?.Dispose(); latest = frame; frame = null; ready.TrySetResult();
            }
        }
        catch (ObjectDisposedException) { }
        catch (COMException) { }
        finally { frame?.Dispose(); }
    }

    public void Dispose()
    {
        Direct3D11CaptureFramePool? oldPool; GraphicsCaptureSession? oldSession; IDirect3DDevice? oldDevice; Direct3D11CaptureFrame? oldFrame;
        lock (gate)
        {
            if (disposed) return; disposed = true;
            oldPool = pool; oldSession = session; oldDevice = device; oldFrame = latest;
            pool = null; session = null; device = null; latest = null; previousJpeg = null; ready.TrySetCanceled();
        }
        if (oldPool is not null) Close(() => oldPool.FrameArrived -= FrameArrived);
        Close(() => oldSession?.Dispose()); Close(() => oldFrame?.Dispose()); Close(() => oldPool?.Dispose()); Close(() => oldDevice?.Dispose());
        cancellation.Dispose();
    }

    private static void Close(Action close)
    {
        try { close(); }
        catch (ObjectDisposedException) { }
        catch (COMException) { }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static GraphicsCaptureItem CreateItem()
    {
        var name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, (uint)name.Length, out var text));
        try
        {
            var factoryId = typeof(ICaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, ref factoryId, out var pointer));
            try
            {
                var factory = (ICaptureItemInterop)Marshal.GetObjectForIUnknown(pointer);
                var bounds = Screen.PrimaryScreen!.Bounds;
                var monitor = MonitorFromPoint(new NativePoint { X = bounds.Left + 1, Y = bounds.Top + 1 }, 2);
                var itemId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
                Marshal.ThrowExceptionForHR(factory.CreateForMonitor(monitor, ref itemId, out var item));
                try { return MarshalInterface<GraphicsCaptureItem>.FromAbi(item); } finally { Marshal.Release(item); }
            }
            finally { Marshal.Release(pointer); }
        }
        finally { WindowsDeleteString(text); }
    }

    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref Guid id, out IntPtr item);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid id, out IntPtr item);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, uint length, out IntPtr text);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr text);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr text, ref Guid id, out IntPtr factory);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, uint driver, IntPtr software, uint flags, IntPtr levels, uint count, uint sdk, out IntPtr device, out uint level, out IntPtr context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr device);
}
