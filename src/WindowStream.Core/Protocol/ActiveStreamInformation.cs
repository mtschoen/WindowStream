namespace WindowStream.Core.Protocol;

public sealed record ActiveStreamInformation(
    int StreamId,
    int UdpPort,
    string Codec,
    int Width,
    int Height,
    int FramesPerSecond);
