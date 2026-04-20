using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Transport;

namespace WindowStream.Core.Session.Testing;

public sealed class FakeUdpVideoSender : IUdpVideoSender
{
    private readonly List<(FragmentedPacket Packet, IPEndPoint Destination)> sentPackets = new();
    private int localPort;
    private bool disposed;

    public int LocalPort => localPort;
    public bool Disposed => disposed;

    public IReadOnlyList<(FragmentedPacket Packet, IPEndPoint Destination)> SentPackets => sentPackets;
    public int SentPacketCount => sentPackets.Count;

    public Task BindAsync(IPEndPoint localEndpoint, CancellationToken cancellationToken)
    {
        localPort = localEndpoint.Port == 0 ? 51235 : localEndpoint.Port;
        return Task.CompletedTask;
    }

    public Task SendPacketAsync(FragmentedPacket packet, IPEndPoint destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sentPackets.Add((packet, destination));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        return ValueTask.CompletedTask;
    }
}
