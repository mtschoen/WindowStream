namespace WindowStream.Core.Protocol;

public sealed record PauseStreamMessage(int StreamId) : ControlMessage;
