namespace WindowStream.Core.Capture;

public sealed record CaptureOptions(
    int targetFramesPerSecond,
    bool includeCursor);
