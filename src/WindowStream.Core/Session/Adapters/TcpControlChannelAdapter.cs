using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WindowStream.Core.Protocol;
using WindowStream.Core.Transport;

namespace WindowStream.Core.Session.Adapters;

/// <summary>
/// Wraps a <see cref="TcpClient"/> stream and implements <see cref="IControlChannel"/>
/// using length-prefix framing and <see cref="ControlMessageSerialization"/>.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Native TCP stream wrapper; framing + serialization are covered in isolation, and FakeControlChannel covers behaviour.")]
public sealed class TcpControlChannelAdapter : IControlChannel
{
    readonly TcpClient _tcpClient;
    readonly Stream _stream;
    readonly TimeProvider _timeProvider;
    DateTimeOffset _lastHeartbeatReceived;
    bool _disposed;

    public TcpControlChannelAdapter(TcpClient tcpClient, TimeProvider timeProvider)
    {
        _tcpClient = tcpClient ?? throw new ArgumentNullException(nameof(tcpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _stream = tcpClient.GetStream();
        _lastHeartbeatReceived = timeProvider.GetUtcNow();
    }

    public DateTimeOffset LastHeartbeatReceived => _lastHeartbeatReceived;

    public IPAddress? RemoteIpAddress => (_tcpClient.Client.RemoteEndPoint as IPEndPoint)?.Address;

    public void NotifyHeartbeatReceived()
    {
        _lastHeartbeatReceived = _timeProvider.GetUtcNow();
    }

    public async Task SendAsync(ControlMessage message, CancellationToken cancellationToken)
    {
        var json = ControlMessageSerialization.Serialize(message);
        var payload = Encoding.UTF8.GetBytes(json);
        await LengthPrefixFraming.WriteFrameAsync(_stream, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        var payload = await LengthPrefixFraming.ReadFrameAsync(_stream, cancellationToken).ConfigureAwait(false);
        var json = Encoding.UTF8.GetString(payload);
        return ControlMessageSerialization.Deserialize(json);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
#pragma warning disable CA1031 // best-effort dispose of stream and TCP client in async teardown
        try { _stream.Dispose(); } catch { /* best-effort */ }
        try { _tcpClient.Dispose(); } catch { /* best-effort */ }
#pragma warning restore CA1031
        return ValueTask.CompletedTask;
    }
}
