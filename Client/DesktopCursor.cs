using System.Runtime.InteropServices;

internal static class DesktopCursor
{
    public static void Draw(Graphics graphics, Rectangle display)
    {
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || (cursor.Flags & 3) != 1 || !display.Contains(cursor.Position.X, cursor.Position.Y)) return;
        DrawCursor(graphics, cursor.Handle, new Point(cursor.Position.X, cursor.Position.Y), display);
    }

    internal static void DrawCursor(Graphics graphics, IntPtr handle, Point position, Rectangle display)
    {
        var copy = CopyIcon(handle);
        if (copy == IntPtr.Zero) return;
        try
        {
            if (!GetIconInfo(copy, out var icon)) return;
            try
            {
                var hdc = graphics.GetHdc();
                try
                {
                    DrawIconEx(hdc, position.X - display.Left - (int)icon.HotspotX,
                        position.Y - display.Top - (int)icon.HotspotY, copy, 0, 0, 0, IntPtr.Zero, 3);
                }
                finally { graphics.ReleaseHdc(hdc); }
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
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
