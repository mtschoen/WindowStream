namespace WindowStream.Core.Protocol;

public sealed record RequestKeyframeMessage(int StreamId) : ControlMessage;
