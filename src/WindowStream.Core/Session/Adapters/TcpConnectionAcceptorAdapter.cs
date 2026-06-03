using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace WindowStream.Core.Session.Adapters;

/// <summary>
/// Real <see cref="ITcpConnectionAcceptor"/> backed by a <see cref="TcpListener"/>.
/// Binds to all interfaces so an Android viewer on the LAN can connect.
/// Pass <c>0</c> for the port to let the OS assign one.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Native socket I/O thin wrapper; FakeTcpConnectionAcceptor + integration tests cover behaviour.")]
public sealed class TcpConnectionAcceptorAdapter : ITcpConnectionAcceptor
{
    readonly TimeProvider _timeProvider;
    readonly IPAddress _bindAddress;
    TcpListener? _listener;
    bool _disposed;

    public TcpConnectionAcceptorAdapter(TimeProvider timeProvider)
        : this(timeProvider, IPAddress.Any)
    {
    }

    public TcpConnectionAcceptorAdapter(TimeProvider timeProvider, IPAddress bindAddress)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _bindAddress = bindAddress ?? throw new ArgumentNullException(nameof(bindAddress));
    }

    public int LocalPort => ((IPEndPoint?)_listener?.LocalEndpoint)?.Port ?? 0;

    public void StartListening(int port)
    {
        _listener = new TcpListener(_bindAddress, port);
        _listener.Start();
    }

    public async Task<IControlChannel> AcceptAsync(CancellationToken cancellationToken)
    {
        if (_listener is null) throw new InvalidOperationException("StartListening must be called before AcceptAsync.");
        var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        return new TcpControlChannelAdapter(client, _timeProvider);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
#pragma warning disable CA1031 // best-effort stop/dispose of TCP listener in async teardown
        try { _listener?.Stop(); _listener?.Dispose(); } catch { /* best-effort */ }
#pragma warning restore CA1031
        return ValueTask.CompletedTask;
    }
}
