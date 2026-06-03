using System.Net;
using WindowStream.Core.Transport;

namespace WindowStream.Core.Session.Testing;

public sealed class FakeUdpVideoSender : IUdpVideoSender
{
    readonly List<(FragmentedPacket Packet, IPEndPoint Destination)> _sentPackets = new();
    int _localPort;
    bool _disposed;

    public int LocalPort => _localPort;
    public bool Disposed => _disposed;

    public IReadOnlyList<(FragmentedPacket Packet, IPEndPoint Destination)> SentPackets => _sentPackets;
    public int SentPacketCount => _sentPackets.Count;

    public Task BindAsync(IPEndPoint localEndpoint, CancellationToken cancellationToken)
    {
        _localPort = localEndpoint.Port == 0 ? 51235 : localEndpoint.Port;
        return Task.CompletedTask;
    }

    public Task SendPacketAsync(FragmentedPacket packet, IPEndPoint destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _sentPackets.Add((packet, destination));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
