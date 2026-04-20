using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WindowStream.Core.Protocol;

namespace WindowStream.Core.Session.Testing;

/// <summary>
/// A fake TCP acceptor that allows test code to inject pre-wired <see cref="FakeControlChannel"/> /
/// <see cref="FakeViewerEndpoint"/> pairs without using real network sockets.
/// </summary>
public sealed class FakeTcpConnectionAcceptor : ITcpConnectionAcceptor
{
    private readonly Channel<IControlChannel> pendingConnections =
        Channel.CreateUnbounded<IControlChannel>(new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });
    private readonly TimeProvider timeProvider;
    private int localPort;
    private bool disposed;

    public FakeTcpConnectionAcceptor(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public int LocalPort => localPort;

    public void StartListening(int port)
    {
        localPort = port == 0 ? 51234 : port;
    }

    /// <summary>
    /// Creates a paired channel/viewer. The channel is queued for the server to accept;
    /// the viewer endpoint is returned to the caller (test code) for sending and receiving.
    /// </summary>
    public FakeViewerEndpoint EnqueueIncomingConnection()
    {
        // viewerToServer: messages the viewer writes that the server reads
        Channel<ControlMessage> viewerToServer = Channel.CreateUnbounded<ControlMessage>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
        // serverToViewer: messages the server writes that the viewer reads
        Channel<ControlMessage> serverToViewer = Channel.CreateUnbounded<ControlMessage>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

        FakeControlChannel serverSide = new FakeControlChannel(viewerToServer.Reader, serverToViewer.Writer, timeProvider);
        FakeViewerEndpoint viewerSide = new FakeViewerEndpoint(viewerToServer.Writer, serverToViewer.Reader);

        pendingConnections.Writer.TryWrite(serverSide);
        return viewerSide;
    }

    public Task<IControlChannel> AcceptAsync(CancellationToken cancellationToken) =>
        pendingConnections.Reader.ReadAsync(cancellationToken).AsTask();

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        pendingConnections.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
