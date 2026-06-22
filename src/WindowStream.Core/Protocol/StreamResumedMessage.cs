namespace WindowStream.Core.Protocol;

public sealed record StreamResumedMessage(int StreamId) : ControlMessage;
