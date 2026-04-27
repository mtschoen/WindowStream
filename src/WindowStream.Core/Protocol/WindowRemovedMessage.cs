namespace WindowStream.Core.Protocol;

public sealed record WindowRemovedMessage(ulong WindowId) : ControlMessage;
