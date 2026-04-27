#if WINDOWS
using System;
using System.Runtime.InteropServices;

namespace WindowStream.Core.Session.Input;

public sealed class ForegroundWindowApi : IForegroundWindowApi
{
    public long GetForegroundWindow() => GetForegroundWindowNative().ToInt64();

    public uint GetWindowThreadProcessId(long hwnd) =>
        GetWindowThreadProcessIdNative(new IntPtr(hwnd), out _);

    public bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach) =>
        AttachThreadInputNative(sourceThreadId, targetThreadId, attach);

    public bool SetForegroundWindow(long hwnd) =>
        SetForegroundWindowNative(new IntPtr(hwnd));

    public uint CurrentThreadId() => GetCurrentThreadIdNative();

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindowNative();

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessIdNative(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "AttachThreadInput")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInputNative(uint sourceThreadId, uint targetThreadId, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindowNative(IntPtr hwnd);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint GetCurrentThreadIdNative();
}
#endif
