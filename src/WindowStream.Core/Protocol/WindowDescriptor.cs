namespace WindowStream.Core.Protocol;

public sealed record WindowDescriptor(
    ulong WindowId,
    long Hwnd,
    int ProcessId,
    string ProcessName,
    string Title,
    int PhysicalWidth,
    int PhysicalHeight);
