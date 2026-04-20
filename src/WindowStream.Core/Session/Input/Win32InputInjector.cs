#if WINDOWS
using System;
using System.Runtime.InteropServices;

namespace WindowStream.Core.Session.Input;

public static class Win32InputInjector
{
    public static void InjectKey(int keyCode, bool isUnicode, bool isDown)
    {
        INPUT input = new INPUT { type = INPUT_KEYBOARD };
        input.U.keyboard.wVk = isUnicode ? (ushort)0 : (ushort)keyCode;
        input.U.keyboard.wScan = isUnicode ? (ushort)keyCode : (ushort)0;
        input.U.keyboard.dwFlags = 0;
        if (isUnicode) input.U.keyboard.dwFlags |= KEYEVENTF_UNICODE;
        if (!isDown) input.U.keyboard.dwFlags |= KEYEVENTF_KEYUP;
        input.U.keyboard.time = 0;
        input.U.keyboard.dwExtraInfo = UIntPtr.Zero;
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT keyboard;
        [FieldOffset(0)] public MOUSEINPUT mouse;
        [FieldOffset(0)] public HARDWAREINPUT hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags;
        public uint time; public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData;
        public uint dwFlags; public uint time; public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
}
#endif
