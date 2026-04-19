namespace WindowStream.Core.Protocol;

public sealed record HeartbeatMessage : ControlMessage
{
    public static HeartbeatMessage Instance { get; } = new HeartbeatMessage();
}
