using System;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Protocol;
using WindowStream.Core.Session;
using WindowStream.Core.Session.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Session.Testing;

public sealed class FakeControlChannelTests
{
    [Fact]
    public async Task Send_And_Receive_Roundtrips_ControlMessage()
    {
        FakeTcpConnectionAcceptor acceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        acceptor.StartListening(0);
        FakeViewerEndpoint viewer = acceptor.EnqueueIncomingConnection();
        IControlChannel channel = await acceptor.AcceptAsync(CancellationToken.None);

        await using (channel)
        await using (viewer)
        {
            HelloMessage sent = new HelloMessage(ViewerVersion: 1, DisplayCapabilities: new DisplayCapabilities(1920, 1080, new[] { "h264" }));
            await viewer.SendAsync(sent, CancellationToken.None);
            ControlMessage received = await channel.ReceiveAsync(CancellationToken.None);
            HelloMessage receivedHello = Assert.IsType<HelloMessage>(received);
            Assert.Equal(1, receivedHello.ViewerVersion);
        }
    }

    [Fact]
    public async Task NotifyHeartbeatReceived_Updates_LastHeartbeatReceived()
    {
        FakeTcpConnectionAcceptor acceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        acceptor.StartListening(0);
        FakeViewerEndpoint viewer = acceptor.EnqueueIncomingConnection();
        IControlChannel channel = await acceptor.AcceptAsync(CancellationToken.None);

        await using (channel)
        await using (viewer)
        {
            DateTimeOffset before = channel.LastHeartbeatReceived;
            await Task.Delay(20, CancellationToken.None);
            channel.NotifyHeartbeatReceived();
            Assert.True(channel.LastHeartbeatReceived >= before);
        }
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        FakeTcpConnectionAcceptor acceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        acceptor.StartListening(0);
        FakeViewerEndpoint viewer = acceptor.EnqueueIncomingConnection();
        IControlChannel channel = await acceptor.AcceptAsync(CancellationToken.None);
        await viewer.DisposeAsync();
        await channel.DisposeAsync();
        await channel.DisposeAsync(); // second call must not throw
    }

    [Fact]
    public async Task Receive_On_Closed_Viewer_Side_Throws_EndOfStream()
    {
        FakeTcpConnectionAcceptor acceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        acceptor.StartListening(0);
        FakeViewerEndpoint viewer = acceptor.EnqueueIncomingConnection();
        IControlChannel channel = await acceptor.AcceptAsync(CancellationToken.None);

        // Close the viewer side without sending anything.
        await viewer.DisposeAsync();
        await Assert.ThrowsAsync<System.IO.EndOfStreamException>(
            () => channel.ReceiveAsync(CancellationToken.None));
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task FakeViewerEndpoint_Receive_WrongType_Throws()
    {
        FakeTcpConnectionAcceptor acceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        acceptor.StartListening(0);
        FakeViewerEndpoint viewer = acceptor.EnqueueIncomingConnection();
        IControlChannel channel = await acceptor.AcceptAsync(CancellationToken.None);

        await using (channel)
        await using (viewer)
        {
            // Server sends a HeartbeatMessage; viewer tries to receive as HelloMessage.
            await channel.SendAsync(HeartbeatMessage.Instance, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => viewer.ReceiveAsync<HelloMessage>(CancellationToken.None));
        }
    }

    [Fact]
    public async Task FakeViewerEndpoint_Receive_On_Closed_Channel_Throws_EndOfStream()
    {
        FakeTcpConnectionAcceptor acceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        acceptor.StartListening(0);
        FakeViewerEndpoint viewer = acceptor.EnqueueIncomingConnection();
        IControlChannel channel = await acceptor.AcceptAsync(CancellationToken.None);

        // Server closes without sending anything.
        await channel.DisposeAsync();
        await Assert.ThrowsAsync<System.IO.EndOfStreamException>(
            () => viewer.ReceiveAsync(CancellationToken.None));
        await viewer.DisposeAsync();
    }

    [Fact]
    public async Task FakeViewerEndpoint_DisposeAsync_IsIdempotent()
    {
        FakeTcpConnectionAcceptor acceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        acceptor.StartListening(0);
        FakeViewerEndpoint viewer = acceptor.EnqueueIncomingConnection();
        IControlChannel channel = await acceptor.AcceptAsync(CancellationToken.None);
        await channel.DisposeAsync();
        await viewer.DisposeAsync();
        await viewer.DisposeAsync(); // idempotent
    }
}
