#if WINDOWS
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Session;

namespace WindowStream.Integration.Tests.Support;

/// <summary>
/// Real <see cref="ITcpConnectionAcceptor"/> backed by a <see cref="TcpListener"/>.
/// Binds to a random available port when <c>port 0</c> is passed to
/// <see cref="StartListening"/>.
/// </summary>
internal sealed class TcpConnectionAcceptorAdapter : ITcpConnectionAcceptor
{
    private readonly TimeProvider timeProvider;
    private TcpListener? listener;
    private bool disposed;

    internal TcpConnectionAcceptorAdapter(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public int LocalPort => ((IPEndPoint?)listener?.LocalEndpoint)?.Port ?? 0;

    public void StartListening(int port)
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
    }

    public async Task<IControlChannel> AcceptAsync(CancellationToken cancellationToken)
    {
        if (listener is null) throw new InvalidOperationException("StartListening must be called before AcceptAsync.");
        TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        return new TcpControlChannelAdapter(client, timeProvider);
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        try { listener?.Stop(); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }
}
#endif
