#if WINDOWS
using System.Runtime.InteropServices;

namespace WindowStream.Core.Session.Input;

// Win32 interop: struct and constant names mirror the Win32 API (INPUT, KEYBDINPUT,
// KEYEVENTF_*) by deliberate convention, so the FDG PascalCase naming rules do not apply.
// ReSharper disable InconsistentNaming
public static class Win32InputInjector
{
    public static void InjectKey(int keyCode, bool isUnicode, bool isDown)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.U.keyboard.wVk = isUnicode ? (ushort)0 : (ushort)keyCode;
        input.U.keyboard.wScan = isUnicode ? (ushort)keyCode : (ushort)0;
        input.U.keyboard.dwFlags = 0;
        if (isUnicode) input.U.keyboard.dwFlags |= KEYEVENTF_UNICODE;
        if (!isDown) input.U.keyboard.dwFlags |= KEYEVENTF_KEYUP;
        input.U.keyboard.time = 0;
        input.U.keyboard.dwExtraInfo = UIntPtr.Zero;
        _ = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static void InjectMouse(
        int absoluteX,
        int absoluteY,
        Protocol.MouseEventType eventType,
        int buttonFlags,
        int scrollDelta)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        // Win32 MOUSEINPUT absolute coordinates are normalized to [0, 65535].
        input.U.mouse.dx = absoluteX;
        input.U.mouse.dy = absoluteY;
        input.U.mouse.dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;

        switch (eventType)
        {
            case Protocol.MouseEventType.Move:
                input.U.mouse.dwFlags |= MOUSEEVENTF_MOVE;
                break;
            case Protocol.MouseEventType.ButtonDown:
                input.U.mouse.dwFlags |= MOUSEEVENTF_MOVE;
                if ((buttonFlags & Protocol.MouseButton.Left) != 0)
                    input.U.mouse.dwFlags |= MOUSEEVENTF_LEFTDOWN;
                if ((buttonFlags & Protocol.MouseButton.Right) != 0)
                    input.U.mouse.dwFlags |= MOUSEEVENTF_RIGHTDOWN;
                if ((buttonFlags & Protocol.MouseButton.Middle) != 0)
                    input.U.mouse.dwFlags |= MOUSEEVENTF_MIDDLEDOWN;
                break;
            case Protocol.MouseEventType.ButtonUp:
                input.U.mouse.dwFlags |= MOUSEEVENTF_MOVE;
                if ((buttonFlags & Protocol.MouseButton.Left) != 0)
                    input.U.mouse.dwFlags |= MOUSEEVENTF_LEFTUP;
                if ((buttonFlags & Protocol.MouseButton.Right) != 0)
                    input.U.mouse.dwFlags |= MOUSEEVENTF_RIGHTUP;
                if ((buttonFlags & Protocol.MouseButton.Middle) != 0)
                    input.U.mouse.dwFlags |= MOUSEEVENTF_MIDDLEUP;
                break;
            case Protocol.MouseEventType.Scroll:
                input.U.mouse.dwFlags |= MOUSEEVENTF_MOVE | MOUSEEVENTF_WHEEL;
                input.U.mouse.mouseData = unchecked((uint)scrollDelta);
                break;
        }

        input.U.mouse.time = 0;
        input.U.mouse.dwExtraInfo = UIntPtr.Zero;
        _ = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    const uint INPUT_MOUSE = 0;
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_UNICODE = 0x0004;

    const uint MOUSEEVENTF_MOVE = 0x0001;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;
    const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    const uint MOUSEEVENTF_WHEEL = 0x0800;
    const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT keyboard;
        [FieldOffset(0)] public MOUSEINPUT mouse;
        [FieldOffset(0)] public HARDWAREINPUT hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags;
        public uint time; public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData;
        public uint dwFlags; public uint time; public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
}
#endif
