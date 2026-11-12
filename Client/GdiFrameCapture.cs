using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RemoteAdmin.Shared;

// Capture directly into a reusable, already-scaled DIB. This avoids a full-size
// 24-bit screenshot allocation and a second managed resize for every frame.
internal sealed class GdiFrameCapture : IDisposable
{
    private IntPtr dc, dib, previous;
    private Bitmap? image;
    private readonly MemoryStream encoded = new();
    private readonly EncoderParameters quality = new(1);
    private static readonly ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().Single(x => x.FormatID == ImageFormat.Jpeg.Guid);

    public GdiFrameCapture() => quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 65L);

    public Task<ScreenFrame> CaptureAsync(string requestId, long sequence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!DesktopAccess.IsInteractiveDesktop()) throw new InvalidOperationException("The desktop is locked or unavailable.");
        var bounds = Screen.PrimaryScreen?.Bounds ?? throw new InvalidOperationException("No primary display is available.");
        var scale = Math.Min(1d, Math.Min(1280d / bounds.Width, 720d / bounds.Height));
        var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        var source = GetDC(IntPtr.Zero);
        if (source == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open the screen.");
        try
        {
            EnsureSurface(source, width, height);
            if (!StretchBlt(dc, 0, 0, width, height, source, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x00cc0020))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to capture the screen.");
            DesktopCursor.DrawToHdc(dc, bounds, (double)width / bounds.Width, (double)height / bounds.Height);
            GdiFlush();
        }
        finally { ReleaseDC(IntPtr.Zero, source); }
        encoded.Position = 0;
        encoded.SetLength(0);
        image!.Save(encoded, jpeg, quality);
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new ScreenFrame(requestId, width, height,
            Convert.ToBase64String(encoded.GetBuffer(), 0, (int)encoded.Length), sequence, DateTime.UtcNow));
    }

    private void EnsureSurface(IntPtr source, int width, int height)
    {
        if (image?.Width == width && image.Height == height) return;
        DisposeSurface();
        dc = CreateCompatibleDC(source);
        if (dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new BitmapInfo
        {
            Header = new()
            {
                Size = (uint)Marshal.SizeOf<BitmapHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                ImageSize = (uint)(width * height * 4)
            }
        };
        dib = CreateDIBSection(source, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        previous = SelectObject(dc, dib);
        if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        SetStretchBltMode(dc, 4); // HALFTONE, so text remains readable when downscaled.
        SetBrushOrgEx(dc, 0, 0, IntPtr.Zero);
        image = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, bits);
    }

    private void DisposeSurface()
    {
        image?.Dispose(); image = null;
        if (dc != IntPtr.Zero && previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
        if (dib != IntPtr.Zero) DeleteObject(dib);
        if (dc != IntPtr.Zero) DeleteDC(dc);
        dc = dib = previous = IntPtr.Zero;
    }

    public void Dispose() { DisposeSurface(); encoded.Dispose(); quality.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPels, YPels; public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapHeader Header; public uint Color; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool StretchBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, int sw, int sh, uint mode);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
