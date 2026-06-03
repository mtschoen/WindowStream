using System.Net;
using System.Threading.Channels;
using WindowStream.Core.Protocol;

namespace WindowStream.Core.Session.Testing;

/// <summary>
/// A fake TCP acceptor that allows test code to inject pre-wired <see cref="FakeControlChannel"/> /
/// <see cref="FakeViewerEndpoint"/> pairs without using real network sockets.
/// </summary>
public sealed class FakeTcpConnectionAcceptor : ITcpConnectionAcceptor
{
    readonly Channel<IControlChannel> _pendingConnections =
        Channel.CreateUnbounded<IControlChannel>(new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });

    readonly TimeProvider _timeProvider;
    int _localPort;
    bool _disposed;

    public FakeTcpConnectionAcceptor(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public int LocalPort => _localPort;

    public void StartListening(int port)
    {
        _localPort = port == 0 ? 51234 : port;
    }

    /// <summary>
    /// Creates a paired channel/viewer. The channel is queued for the server to accept;
    /// the viewer endpoint is returned to the caller (test code) for sending and receiving.
    /// The optional <paramref name="remoteIpAddress"/> is reported back through
    /// <see cref="IControlChannel.RemoteIpAddress"/> on the server-side channel.
    /// </summary>
    public FakeViewerEndpoint EnqueueIncomingConnection(IPAddress? remoteIpAddress = null)
    {
        // viewerToServer: messages the viewer writes that the server reads
        var viewerToServer = Channel.CreateUnbounded<ControlMessage>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
        // serverToViewer: messages the server writes that the viewer reads
        var serverToViewer = Channel.CreateUnbounded<ControlMessage>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

#pragma warning disable CA2000 // ownership of FakeControlChannel transfers to the channel consumer via pendingConnections
        var serverSide = new FakeControlChannel(
            viewerToServer.Reader, serverToViewer.Writer, _timeProvider, remoteIpAddress);
#pragma warning restore CA2000
        var viewerSide = new FakeViewerEndpoint(viewerToServer.Writer, serverToViewer.Reader);

        _pendingConnections.Writer.TryWrite(serverSide);
        return viewerSide;
    }

    public Task<IControlChannel> AcceptAsync(CancellationToken cancellationToken) =>
        _pendingConnections.Reader.ReadAsync(cancellationToken).AsTask();

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _pendingConnections.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
