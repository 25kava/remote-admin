using System.ComponentModel;
using System.Runtime.InteropServices;
using RemoteAdmin.Shared;

internal sealed class WindowsInputSink(
    Func<WindowsInputSink.NativeInput[], uint>? send = null,
    Func<bool>? desktopAvailable = null,
    Func<(Rectangle Display, Rectangle Virtual)>? desktopBounds = null) : IRemoteInputSink
{
    private readonly HashSet<string> keys = new(StringComparer.Ordinal);
    private readonly HashSet<int> buttons = [];

    public void Apply(InputEvent input)
    {
        if (!RemoteInputRules.IsValid(input)) throw new ArgumentException("Unsupported input event.");
        if (!(desktopAvailable?.Invoke() ?? DesktopScreenSharing.IsInteractiveDesktop())) throw new InvalidOperationException("The desktop is not available for input.");
        if (input.Kind is "keyDown" or "keyUp")
        {
            var down = input.Kind == "keyDown";
            if (!down && !keys.Remove(input.Key!)) return;
            if (down) keys.Add(input.Key!);
            Send(Key(input.Key!, down));
            return;
        }
        var bounds = desktopBounds?.Invoke();
        var display = bounds?.Display ?? Screen.PrimaryScreen?.Bounds ?? throw new InvalidOperationException("No primary display.");
        var area = bounds?.Virtual ?? SystemInformation.VirtualScreen;
        var position = RemoteInputRules.AbsolutePosition(input.X, input.Y, display.Left, display.Top, display.Width, display.Height,
            area.Left, area.Top, area.Width, area.Height);
        uint flags = 0x0001 | 0x8000 | 0x4000;
        uint data = 0;
        if (input.Kind == "buttonDown") { buttons.Add(input.Button); flags |= ButtonFlag(input.Button, true); }
        else if (input.Kind == "buttonUp")
        {
            if (!buttons.Remove(input.Button)) return;
            flags |= ButtonFlag(input.Button, false);
        }
        else if (input.Kind == "wheel") { flags |= 0x0800; data = unchecked((uint)input.Delta); }
        Send(Mouse(position.X, position.Y, data, flags));
    }

    public void ReleaseAll()
    {
        var releases = keys.Select(key => Key(key, false))
            .Concat(buttons.Select(button => Mouse(0, 0, 0, ButtonFlag(button, false)))).ToArray();
        keys.Clear(); buttons.Clear();
        if (releases.Length > 0 && (desktopAvailable?.Invoke() ?? DesktopScreenSharing.IsInteractiveDesktop())) Send(releases);
    }

    private static uint ButtonFlag(int button, bool down) => (button, down) switch
    { (0,true)=>0x0002,(0,false)=>0x0004,(1,true)=>0x0020,(1,false)=>0x0040,(2,true)=>0x0008,(2,false)=>0x0010,_=>0 };
    private static NativeInput Key(string code, bool down)
    {
        RemoteInputRules.TryGetScanCode(code, out var scan, out var extended);
        return new() { Type = 1, Data = new() { Keyboard = new() { Scan = scan, Flags = 0x0008u | (down ? 0u : 0x0002u) | (extended ? 0x0001u : 0u) } } };
    }
    private static NativeInput Mouse(int x, int y, uint data, uint flags) =>
        new() { Type = 0, Data = new() { Mouse = new() { X=x,Y=y,Data=data,Flags=flags } } };
    private void Send(params NativeInput[] inputs)
    {
        if ((send?.Invoke(inputs) ?? SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>())) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows denied remote input; elevated or secure windows cannot be controlled.");
    }

    [StructLayout(LayoutKind.Sequential)] internal struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X,Y; public uint Data,Flags,Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort VirtualKey,Scan; public uint Flags,Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError=true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
