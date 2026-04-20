using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Session.Testing;
using WindowStream.Core.Transport;
using Xunit;

namespace WindowStream.Core.Tests.Session.Testing;

public sealed class FakeUdpVideoSenderTests
{
    [Fact]
    public async Task BindAsync_AssignsLocalPort()
    {
        await using FakeUdpVideoSender sender = new FakeUdpVideoSender();
        await sender.BindAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);
        Assert.True(sender.LocalPort > 0);
    }

    [Fact]
    public async Task BindAsync_WithExplicitPort_UsesRequestedPort()
    {
        await using FakeUdpVideoSender sender = new FakeUdpVideoSender();
        await sender.BindAsync(new IPEndPoint(IPAddress.Loopback, 12345), CancellationToken.None);
        Assert.Equal(12345, sender.LocalPort);
    }

    [Fact]
    public async Task SendPacketAsync_AccumulatesPackets()
    {
        await using FakeUdpVideoSender sender = new FakeUdpVideoSender();
        await sender.BindAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);

        PacketHeader header = new PacketHeader(StreamId: 1, Sequence: 0, PresentationTimestampMicroseconds: 0, Flags: PacketFlags.LastFragment, FragmentIndex: 0, FragmentTotal: 1);
        FragmentedPacket packet = new FragmentedPacket(header, new byte[] { 0x01 });
        IPEndPoint destination = new IPEndPoint(IPAddress.Loopback, 55000);

        await sender.SendPacketAsync(packet, destination, CancellationToken.None);
        await sender.SendPacketAsync(packet, destination, CancellationToken.None);

        Assert.Equal(2, sender.SentPacketCount);
        Assert.Equal(2, sender.SentPackets.Count);
    }

    [Fact]
    public async Task SendPacketAsync_HonorsCancellation()
    {
        await using FakeUdpVideoSender sender = new FakeUdpVideoSender();
        await sender.BindAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);

        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        PacketHeader header = new PacketHeader(StreamId: 1, Sequence: 0, PresentationTimestampMicroseconds: 0, Flags: PacketFlags.LastFragment, FragmentIndex: 0, FragmentTotal: 1);
        FragmentedPacket packet = new FragmentedPacket(header, new byte[] { 0x01 });

        await Assert.ThrowsAnyAsync<System.OperationCanceledException>(() =>
            sender.SendPacketAsync(packet, new IPEndPoint(IPAddress.Loopback, 55000), cancellation.Token));
    }

    [Fact]
    public async Task DisposeAsync_SetsDisposed()
    {
        FakeUdpVideoSender sender = new FakeUdpVideoSender();
        await sender.DisposeAsync();
        Assert.True(sender.Disposed);
    }
}
