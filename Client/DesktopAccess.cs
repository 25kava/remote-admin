using System.Runtime.InteropServices;
using System.Text;

internal static class DesktopAccess
{
    internal static bool IsInteractiveDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) &&
                name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);
    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);
}
