namespace WindowStream.Core.Protocol;

public sealed record StreamStartedMessage(
    int StreamId,
    ulong WindowId,
    string Codec,
    int Width,
    int Height,
    int FramesPerSecond) : ControlMessage;
