namespace WindowStream.Core.Protocol;

public sealed record FocusWindowMessage(int StreamId) : ControlMessage;
