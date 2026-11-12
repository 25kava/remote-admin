using System.Runtime.InteropServices;
using RemoteAdmin.Shared;

internal sealed class WindowsInputBlocker : ILocalInputBlocker, IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const uint WM_QUIT = 0x0012;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLMHF_INJECTED = 0x01;

    private readonly object gate = new();
    private readonly ManualResetEventSlim ready = new(false);
    private HookProc? keyboardProc;
    private HookProc? mouseProc;
    private Thread? thread;
    private uint threadId;
    private IntPtr keyboardHook;
    private IntPtr mouseHook;
    private volatile bool blocked;
    private bool disposed;

    public bool SetBlocked(bool block)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (block == blocked) return true;
            if (!block)
            {
                blocked = false;
                StopThread();
                return true;
            }

            if (thread is null)
            {
                ready.Reset();
                thread = new Thread(HookThread) { IsBackground = true, Name = "RemoteAdmin local input block" };
                thread.Start();
                if (!ready.Wait(TimeSpan.FromSeconds(2)))
                {
                    StopThread();
                    return false;
                }
            }

            if (keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero)
            {
                StopThread();
                return false;
            }

            blocked = true;
            return true;
        }
    }

    internal static bool IsInjected(uint flags, bool keyboard) =>
        (flags & (keyboard ? LLKHF_INJECTED : LLMHF_INJECTED)) != 0;

    private void HookThread()
    {
        threadId = GetCurrentThreadId();
        keyboardProc = KeyboardHook;
        mouseProc = MouseHook;
        try
        {
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, GetModuleHandle(null), 0);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, GetModuleHandle(null), 0);
            // Create this thread's message queue before the caller can post WM_QUIT.
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            ready.Set();
            if (keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero) return;

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            if (keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(keyboardHook); keyboardHook = IntPtr.Zero; }
            if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            ready.Set();
        }
    }

    private void StopThread()
    {
        var running = thread;
        if (running is null) return;
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        if (running != Thread.CurrentThread) running.Join(TimeSpan.FromSeconds(1));
        thread = null;
        threadId = 0;
        keyboardHook = IntPtr.Zero;
        mouseHook = IntPtr.Zero;
    }

    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && blocked && !IsInjected(Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam).Flags, keyboard: true))
                return (IntPtr)1;
        }
        catch (Exception) { }
        return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && blocked && !IsInjected(Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).Flags, keyboard: false))
                return (IntPtr)1;
        }
        catch (Exception) { }
        return CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            blocked = false;
            StopThread();
        }
        ready.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint VirtualKey, ScanCode, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT Point;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public POINT Point;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc procedure, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out MSG message, IntPtr window, uint filterMin, uint filterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessage(out MSG message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? name);
}
