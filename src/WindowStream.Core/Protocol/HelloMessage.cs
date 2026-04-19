namespace WindowStream.Core.Protocol;

public sealed record HelloMessage(
    int ViewerVersion,
    DisplayCapabilities DisplayCapabilities) : ControlMessage;
