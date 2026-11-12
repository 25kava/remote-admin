using System.Runtime.InteropServices;

internal static class DesktopCursor
{
    public static void Draw(Graphics graphics, Rectangle display)
    {
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || (cursor.Flags & 3) != 1 || !display.Contains(cursor.Position.X, cursor.Position.Y)) return;
        DrawCursor(graphics, cursor.Handle, new Point(cursor.Position.X, cursor.Position.Y), display);
    }

    internal static void DrawToHdc(IntPtr dc, Rectangle display, double scaleX, double scaleY)
    {
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || (cursor.Flags & 3) != 1 || !display.Contains(cursor.Position.X, cursor.Position.Y)) return;
        DrawHandle(dc, cursor.Handle, new Point(cursor.Position.X, cursor.Position.Y), display, scaleX, scaleY);
    }

    internal static void DrawCursor(Graphics graphics, IntPtr handle, Point position, Rectangle display)
    {
        var dc = graphics.GetHdc();
        try { DrawHandle(dc, handle, position, display, 1, 1); }
        finally { graphics.ReleaseHdc(dc); }
    }

    private static void DrawHandle(IntPtr dc, IntPtr handle, Point position, Rectangle display, double scaleX, double scaleY)
    {
        var copy = CopyIcon(handle);
        if (copy == IntPtr.Zero) return;
        try
        {
            if (!GetIconInfo(copy, out var icon)) return;
            try
            {
                var width = 0; var height = 0;
                if (GetObject(icon.Color != IntPtr.Zero ? icon.Color : icon.Mask, Marshal.SizeOf<NativeBitmap>(), out var bitmap) != 0)
                {
                    width = Math.Max(1, (int)Math.Round(bitmap.Width * scaleX));
                    height = Math.Max(1, (int)Math.Round(bitmap.Height * (icon.Color == IntPtr.Zero ? .5 : 1) * scaleY));
                }
                DrawIconEx(dc, (int)Math.Round((position.X - display.Left - icon.HotspotX) * scaleX),
                    (int)Math.Round((position.Y - display.Top - icon.HotspotY) * scaleY), copy, width, height, 0, IntPtr.Zero, 3);
            }
            finally
            {
                if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
                if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
            }
        }
        finally { DestroyIcon(copy); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo { public int Size; public uint Flags; public IntPtr Handle; public NativePoint Position; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public uint HotspotX, HotspotY; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap { public int Type, Width, Height, Stride; public ushort Planes, BitsPerPixel; public IntPtr Bits; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll", CharSet=CharSet.Unicode)] private static extern int GetObject(IntPtr handle, int size, out NativeBitmap bitmap);
}
