namespace WindowStream.Core.Capture;

public sealed record CaptureOptions(
    int TargetFramesPerSecond,
    bool IncludeCursor);
