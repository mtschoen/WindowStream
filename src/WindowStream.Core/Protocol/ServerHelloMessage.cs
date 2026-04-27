namespace WindowStream.Core.Protocol;

public sealed record ServerHelloMessage(
    int ServerVersion,
    int UdpPort,
    WindowDescriptor[] Windows) : ControlMessage;
