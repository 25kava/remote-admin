namespace RemoteAdmin.Shared;

public static class RemoteInputRules
{
    public static bool IsValid(InputEvent e) => e.Kind switch
    {
        "move" => Position(e),
        "buttonDown" or "buttonUp" => Position(e) && e.Button is >= 0 and <= 2,
        "wheel" => Position(e) && e.Delta is >= -1200 and <= 1200,
        "keyDown" or "keyUp" => TryGetScanCode(e.Key, out _, out _),
        _ => false
    };
    private static bool Position(InputEvent e) => double.IsFinite(e.X) && double.IsFinite(e.Y) && e.X is >= 0 and <= 1 && e.Y is >= 0 and <= 1;

    public static (int X, int Y) AbsolutePosition(double x, double y,
        int displayLeft, int displayTop, int displayWidth, int displayHeight,
        int virtualLeft, int virtualTop, int virtualWidth, int virtualHeight)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1 ||
            displayWidth < 1 || displayHeight < 1 || virtualWidth < 1 || virtualHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(x));
        var px = displayLeft + x * (displayWidth - 1);
        var py = displayTop + y * (displayHeight - 1);
        return ((int)Math.Round(Math.Clamp((px - virtualLeft) / Math.Max(1, virtualWidth - 1), 0, 1) * 65535),
            (int)Math.Round(Math.Clamp((py - virtualTop) / Math.Max(1, virtualHeight - 1), 0, 1) * 65535));
    }

    public static bool TryGetScanCode(string? code, out ushort scan, out bool extended)
    {
        var encoded = code is not null && Scans.TryGetValue(code, out var value) ? value : 0;
        scan = (ushort)(encoded & 0xff);
        extended = (encoded & 0x100) != 0;
        return encoded != 0;
    }

    private static readonly Dictionary<string, int> Scans = new(StringComparer.Ordinal)
    {
        ["Escape"]=0x01,["Digit1"]=0x02,["Digit2"]=0x03,["Digit3"]=0x04,["Digit4"]=0x05,["Digit5"]=0x06,
        ["Digit6"]=0x07,["Digit7"]=0x08,["Digit8"]=0x09,["Digit9"]=0x0a,["Digit0"]=0x0b,["Minus"]=0x0c,["Equal"]=0x0d,
        ["Backspace"]=0x0e,["Tab"]=0x0f,["KeyQ"]=0x10,["KeyW"]=0x11,["KeyE"]=0x12,["KeyR"]=0x13,["KeyT"]=0x14,
        ["KeyY"]=0x15,["KeyU"]=0x16,["KeyI"]=0x17,["KeyO"]=0x18,["KeyP"]=0x19,["BracketLeft"]=0x1a,["BracketRight"]=0x1b,
        ["Enter"]=0x1c,["ControlLeft"]=0x1d,["KeyA"]=0x1e,["KeyS"]=0x1f,["KeyD"]=0x20,["KeyF"]=0x21,["KeyG"]=0x22,
        ["KeyH"]=0x23,["KeyJ"]=0x24,["KeyK"]=0x25,["KeyL"]=0x26,["Semicolon"]=0x27,["Quote"]=0x28,["Backquote"]=0x29,
        ["ShiftLeft"]=0x2a,["Backslash"]=0x2b,["KeyZ"]=0x2c,["KeyX"]=0x2d,["KeyC"]=0x2e,["KeyV"]=0x2f,["KeyB"]=0x30,
        ["KeyN"]=0x31,["KeyM"]=0x32,["Comma"]=0x33,["Period"]=0x34,["Slash"]=0x35,["ShiftRight"]=0x36,["NumpadMultiply"]=0x37,
        ["AltLeft"]=0x38,["Space"]=0x39,["CapsLock"]=0x3a,["F1"]=0x3b,["F2"]=0x3c,["F3"]=0x3d,["F4"]=0x3e,
        ["F5"]=0x3f,["F6"]=0x40,["F7"]=0x41,["F8"]=0x42,["F9"]=0x43,["F10"]=0x44,["NumLock"]=0x45,["ScrollLock"]=0x46,
        ["Numpad7"]=0x47,["Numpad8"]=0x48,["Numpad9"]=0x49,["NumpadSubtract"]=0x4a,["Numpad4"]=0x4b,["Numpad5"]=0x4c,
        ["Numpad6"]=0x4d,["NumpadAdd"]=0x4e,["Numpad1"]=0x4f,["Numpad2"]=0x50,["Numpad3"]=0x51,["Numpad0"]=0x52,
        ["NumpadDecimal"]=0x53,["IntlBackslash"]=0x56,["F11"]=0x57,["F12"]=0x58,["ControlRight"]=0x11d,["AltRight"]=0x138,
        ["NumpadEnter"]=0x11c,["NumpadDivide"]=0x135,["Home"]=0x147,["ArrowUp"]=0x148,["PageUp"]=0x149,["ArrowLeft"]=0x14b,
        ["ArrowRight"]=0x14d,["End"]=0x14f,["ArrowDown"]=0x150,["PageDown"]=0x151,["Insert"]=0x152,["Delete"]=0x153,
        ["MetaLeft"]=0x15b,["MetaRight"]=0x15c,["ContextMenu"]=0x15d
    };
}
