namespace WindowStream.Core.Protocol;

public sealed record WindowAddedMessage(WindowDescriptor Window) : ControlMessage;
