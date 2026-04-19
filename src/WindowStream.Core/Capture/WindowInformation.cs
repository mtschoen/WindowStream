namespace WindowStream.Core.Capture;

public sealed record WindowInformation(
    WindowHandle handle,
    string title,
    string processName,
    int widthPixels,
    int heightPixels);
