#if WINDOWS
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Protocol;
using WindowStream.Core.Session;
using WindowStream.Core.Transport;

namespace WindowStream.Integration.Tests.Support;

/// <summary>
/// Wraps a <see cref="TcpClient"/> stream and implements <see cref="IControlChannel"/>
/// using length-prefix framing and <see cref="ControlMessageSerialization"/>.
/// Used by the server side of the <see cref="TcpConnectionAcceptorAdapter"/>.
/// </summary>
internal sealed class TcpControlChannelAdapter : IControlChannel
{
    private readonly TcpClient tcpClient;
    private readonly Stream stream;
    private readonly TimeProvider timeProvider;
    private DateTimeOffset lastHeartbeatReceived;
    private bool disposed;

    internal TcpControlChannelAdapter(TcpClient tcpClient, TimeProvider timeProvider)
    {
        this.tcpClient = tcpClient ?? throw new ArgumentNullException(nameof(tcpClient));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        stream = tcpClient.GetStream();
        lastHeartbeatReceived = timeProvider.GetUtcNow();
    }

    public DateTimeOffset LastHeartbeatReceived => lastHeartbeatReceived;

    public void NotifyHeartbeatReceived()
    {
        lastHeartbeatReceived = timeProvider.GetUtcNow();
    }

    public async Task SendAsync(ControlMessage message, CancellationToken cancellationToken)
    {
        string json = ControlMessageSerialization.Serialize(message);
        byte[] payload = Encoding.UTF8.GetBytes(json);
        await LengthPrefixFraming.WriteFrameAsync(stream, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[] payload = await LengthPrefixFraming.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        string json = Encoding.UTF8.GetString(payload);
        return ControlMessageSerialization.Deserialize(json);
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        try { stream.Dispose(); } catch { /* best-effort */ }
        try { tcpClient.Dispose(); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }
}
#endif
