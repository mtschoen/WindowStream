using WindowStream.Core.Session.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Session.Testing;

public sealed class FakeTcpConnectionAcceptorTests
{
    [Fact]
    public void Constructor_NullTimeProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FakeTcpConnectionAcceptor(null!));
    }

    [Fact]
    public async Task StartListening_WithZeroPort_AssignsDefaultPort()
    {
        await using var acceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        acceptor.StartListening(0);
        Assert.True(acceptor.LocalPort > 0);
    }

    [Fact]
    public async Task StartListening_WithExplicitPort_UsesRequestedPort()
    {
        await using var acceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        acceptor.StartListening(9876);
        Assert.Equal(9876, acceptor.LocalPort);
    }

    [Fact]
    public async Task EnqueueIncomingConnection_AllowsAccept()
    {
        await using var acceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        acceptor.StartListening(0);

        var viewer = acceptor.EnqueueIncomingConnection();
        var channel = await acceptor.AcceptAsync(CancellationToken.None);

        await using (viewer)
        await using (channel)
        {
            Assert.NotNull(channel);
        }
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var acceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        acceptor.StartListening(0);
        await acceptor.DisposeAsync();
        await acceptor.DisposeAsync();
    }
}
