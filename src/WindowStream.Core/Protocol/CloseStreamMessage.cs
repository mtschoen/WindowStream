namespace WindowStream.Core.Protocol;

public sealed record CloseStreamMessage(int StreamId) : ControlMessage;
