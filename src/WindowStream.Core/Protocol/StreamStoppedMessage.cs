namespace WindowStream.Core.Protocol;

public sealed record StreamStoppedMessage(
    int StreamId,
    StreamStoppedReason Reason) : ControlMessage;
