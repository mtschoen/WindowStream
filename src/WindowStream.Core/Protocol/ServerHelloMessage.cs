namespace WindowStream.Core.Protocol;

public sealed record ServerHelloMessage(
    int ServerVersion,
    ActiveStreamInformation? ActiveStream) : ControlMessage;
