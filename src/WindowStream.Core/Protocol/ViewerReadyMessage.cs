namespace WindowStream.Core.Protocol;

/// <summary>
/// Viewer → server notification that the viewer has bound its UDP receiver.
/// The server combines the supplied port with the TCP connection's peer IP to
/// address outgoing video packets to the viewer.
/// </summary>
public sealed record ViewerReadyMessage(int StreamId, int ViewerUdpPort) : ControlMessage;
