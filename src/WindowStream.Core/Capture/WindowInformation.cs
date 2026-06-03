namespace WindowStream.Core.Capture;

public sealed record WindowInformation(
    WindowHandle Handle,
    string Title,
    string ProcessName,
    int WidthPixels,
    int HeightPixels);
