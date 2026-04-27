namespace WindowStream.Core.Protocol;

/// <summary>
/// Viewer → server notification that the viewer has bound its UDP receiver.
/// Sent once per control connection. The server combines the supplied port with
/// the TCP connection's peer IP to address outgoing video packets to the viewer
/// for every stream multiplexed on this connection.
/// </summary>
public sealed record ViewerReadyMessage(int ViewerUdpPort) : ControlMessage;
