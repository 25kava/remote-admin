using System.Runtime.InteropServices;
using RemoteAdmin.Shared;

internal sealed class DesktopFrameCapture(Action<string>? log = null) : IDisposable
{
    private GpuFrameCapture? gpu;
    private GdiFrameCapture? compatible;

    public async Task<ScreenFrame> CaptureAsync(string id, long sequence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!DesktopAccess.IsInteractiveDesktop()) throw new InvalidOperationException("The desktop is locked or unavailable.");
        if (compatible is not null) return await compatible.CaptureAsync(id, sequence, token);
        try
        {
            if (gpu is null || gpu.IsDisposed) gpu = new GpuFrameCapture(token);
            return await gpu.CaptureAsync(id, sequence, token);
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception ex) when (ex is NotSupportedException || ex is COMException &&
            ex.HResult is unchecked((int)0x887a0004) or unchecked((int)0x80004002) or unchecked((int)0x80040154))
        {
            // Capability/driver fallback only. Access-denied errors are not bypassed.
            gpu?.Dispose(); gpu = null;
            compatible = new GdiFrameCapture();
            log?.Invoke("GPU capture is unavailable; using compatible capture. Actual FPS may be lower.");
            return await compatible.CaptureAsync(id, sequence, token);
        }
    }

    public void Dispose() { gpu?.Dispose(); compatible?.Dispose(); }
}
