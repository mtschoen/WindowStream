namespace WindowStream.Core.Protocol;

public sealed record StreamStoppedMessage(int StreamId) : ControlMessage;
