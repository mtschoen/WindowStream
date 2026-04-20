using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WindowStream.Core.Session.Adapters;

/// <summary>
/// Real <see cref="ITcpConnectionAcceptor"/> backed by a <see cref="TcpListener"/>.
/// Binds to all interfaces so an Android viewer on the LAN can connect.
/// Pass <c>0</c> for the port to let the OS assign one.
/// </summary>
public sealed class TcpConnectionAcceptorAdapter : ITcpConnectionAcceptor
{
    private readonly TimeProvider timeProvider;
    private readonly IPAddress bindAddress;
    private TcpListener? listener;
    private bool disposed;

    public TcpConnectionAcceptorAdapter(TimeProvider timeProvider)
        : this(timeProvider, IPAddress.Any)
    {
    }

    public TcpConnectionAcceptorAdapter(TimeProvider timeProvider, IPAddress bindAddress)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.bindAddress = bindAddress ?? throw new ArgumentNullException(nameof(bindAddress));
    }

    public int LocalPort => ((IPEndPoint?)listener?.LocalEndpoint)?.Port ?? 0;

    public void StartListening(int port)
    {
        listener = new TcpListener(bindAddress, port);
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
