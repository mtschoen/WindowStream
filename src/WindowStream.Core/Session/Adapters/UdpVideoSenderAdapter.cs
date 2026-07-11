using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using WindowStream.Core.Transport;

namespace WindowStream.Core.Session.Adapters;

/// <summary>
/// Real <see cref="IUdpVideoSender"/> backed by a <see cref="UdpClient"/>.
/// Serialises each <see cref="FragmentedPacket"/> into a wire-format UDP datagram
/// (24-byte header + payload) and sends it to the destination endpoint.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Native UDP socket wrapper; FakeUdpVideoSender + PacketHeader covers framing in isolation.")]
public sealed class UdpVideoSenderAdapter : IUdpVideoSender
{
    UdpClient? _udpClient;
    IPEndPoint? _localEndpoint;
    bool _disposed;

    public int LocalPort => _localEndpoint?.Port ?? 0;

#pragma warning disable CA1725 // CA1725: parameter kept as 'endpoint' to avoid shadowing the localEndpoint field
    public Task BindAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        _udpClient = new UdpClient(endpoint);
        _localEndpoint = (IPEndPoint?)_udpClient.Client.LocalEndPoint
            ?? throw new InvalidOperationException("Socket has no LocalEndPoint immediately after binding.");
        return Task.CompletedTask;
    }
#pragma warning restore CA1725

    public async Task SendPacketAsync(FragmentedPacket packet, IPEndPoint destination, CancellationToken cancellationToken)
    {
        if (_udpClient is null) throw new InvalidOperationException("BindAsync must be called before SendPacketAsync.");
        cancellationToken.ThrowIfCancellationRequested();

        var payloadMemory = packet.Payload;
        var totalLength = PacketHeader.HeaderByteLength + payloadMemory.Length;
        var datagram = new byte[totalLength];
        packet.Header.WriteTo(datagram.AsSpan(0, PacketHeader.HeaderByteLength));
        payloadMemory.Span.CopyTo(datagram.AsSpan(PacketHeader.HeaderByteLength));

        await _udpClient.SendAsync(datagram, totalLength, destination).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
#pragma warning disable CA1031 // best-effort dispose of UDP client in async teardown
        try { _udpClient?.Dispose(); } catch { /* best-effort */ }
#pragma warning restore CA1031
        return ValueTask.CompletedTask;
    }
}
