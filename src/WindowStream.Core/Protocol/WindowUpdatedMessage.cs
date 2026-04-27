namespace WindowStream.Core.Protocol;

public sealed record WindowUpdatedMessage(
    ulong WindowId,
    string? Title,
    int? PhysicalWidth,
    int? PhysicalHeight) : ControlMessage;
