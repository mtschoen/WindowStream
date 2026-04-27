namespace WindowStream.Core.Protocol;

public sealed record WindowSnapshotMessage(WindowDescriptor[] Windows) : ControlMessage;
