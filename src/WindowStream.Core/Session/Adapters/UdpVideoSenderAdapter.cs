using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Transport;

namespace WindowStream.Core.Session.Adapters;

/// <summary>
/// Real <see cref="IUdpVideoSender"/> backed by a <see cref="UdpClient"/>.
/// Serialises each <see cref="FragmentedPacket"/> into a wire-format UDP datagram
/// (24-byte header + payload) and sends it to the destination endpoint.
/// </summary>
public sealed class UdpVideoSenderAdapter : IUdpVideoSender
{
    private UdpClient? udpClient;
    private IPEndPoint? localEndpoint;
    private bool disposed;

    public int LocalPort => localEndpoint?.Port ?? 0;

    public Task BindAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        udpClient = new UdpClient(endpoint);
        localEndpoint = (IPEndPoint)udpClient.Client.LocalEndPoint!;
        return Task.CompletedTask;
    }

    public async Task SendPacketAsync(FragmentedPacket packet, IPEndPoint destination, CancellationToken cancellationToken)
    {
        if (udpClient is null) throw new InvalidOperationException("BindAsync must be called before SendPacketAsync.");
        cancellationToken.ThrowIfCancellationRequested();

        ReadOnlyMemory<byte> payloadMemory = packet.Payload;
        int totalLength = PacketHeader.HeaderByteLength + payloadMemory.Length;
        byte[] datagram = new byte[totalLength];
        packet.Header.WriteTo(datagram.AsSpan(0, PacketHeader.HeaderByteLength));
        payloadMemory.Span.CopyTo(datagram.AsSpan(PacketHeader.HeaderByteLength));

        await udpClient.SendAsync(datagram, totalLength, destination).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        try { udpClient?.Dispose(); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }
}
