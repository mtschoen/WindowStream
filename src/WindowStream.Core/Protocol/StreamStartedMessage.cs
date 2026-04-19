namespace WindowStream.Core.Protocol;

public sealed record StreamStartedMessage(
    int StreamId,
    int UdpPort,
    string Codec,
    int Width,
    int Height,
    int FramesPerSecond) : ControlMessage;
