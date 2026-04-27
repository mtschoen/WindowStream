namespace WindowStream.Core.Protocol;

public sealed record OpenStreamMessage(ulong WindowId) : ControlMessage;
