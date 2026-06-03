using System.Net;
using System.Threading.Channels;
using WindowStream.Core.Protocol;

namespace WindowStream.Core.Session.Testing;

/// <summary>
/// A fake <see cref="IControlChannel"/> backed by a pair of in-memory channels.
/// The server reads from <c>inbound</c> (messages sent by the viewer fake)
/// and writes to <c>outbound</c> (messages read by the viewer fake).
/// </summary>
public sealed class FakeControlChannel : IControlChannel
{
    readonly ChannelReader<ControlMessage> _inbound;
    readonly ChannelWriter<ControlMessage> _outbound;
    readonly TimeProvider _timeProvider;
    readonly IPAddress? _remoteIpAddress;
    DateTimeOffset _lastHeartbeatReceived;
    bool _disposed;

    internal FakeControlChannel(
        ChannelReader<ControlMessage> inbound,
        ChannelWriter<ControlMessage> outbound,
        TimeProvider timeProvider,
        IPAddress? remoteIpAddress)
    {
        _inbound = inbound;
        _outbound = outbound;
        _timeProvider = timeProvider;
        _remoteIpAddress = remoteIpAddress;
        _lastHeartbeatReceived = timeProvider.GetUtcNow();
    }

    public DateTimeOffset LastHeartbeatReceived => _lastHeartbeatReceived;

    public IPAddress? RemoteIpAddress => _remoteIpAddress;

    public void NotifyHeartbeatReceived()
    {
        _lastHeartbeatReceived = _timeProvider.GetUtcNow();
    }

    public async Task SendAsync(ControlMessage message, CancellationToken cancellationToken)
    {
        await _outbound.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inbound.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new EndOfStreamException("The fake viewer disconnected.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _outbound.TryComplete();
        return ValueTask.CompletedTask;
    }
}
