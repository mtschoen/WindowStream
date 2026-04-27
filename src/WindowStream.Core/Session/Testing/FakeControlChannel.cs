using System;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WindowStream.Core.Protocol;

namespace WindowStream.Core.Session.Testing;

/// <summary>
/// A fake <see cref="IControlChannel"/> backed by a pair of in-memory channels.
/// The server reads from <c>inbound</c> (messages sent by the viewer fake)
/// and writes to <c>outbound</c> (messages read by the viewer fake).
/// </summary>
public sealed class FakeControlChannel : IControlChannel
{
    private readonly ChannelReader<ControlMessage> inbound;
    private readonly ChannelWriter<ControlMessage> outbound;
    private readonly TimeProvider timeProvider;
    private readonly IPAddress? remoteIpAddress;
    private DateTimeOffset lastHeartbeatReceived;
    private bool disposed;

    internal FakeControlChannel(
        ChannelReader<ControlMessage> inbound,
        ChannelWriter<ControlMessage> outbound,
        TimeProvider timeProvider,
        IPAddress? remoteIpAddress)
    {
        this.inbound = inbound;
        this.outbound = outbound;
        this.timeProvider = timeProvider;
        this.remoteIpAddress = remoteIpAddress;
        this.lastHeartbeatReceived = timeProvider.GetUtcNow();
    }

    public DateTimeOffset LastHeartbeatReceived => lastHeartbeatReceived;

    public IPAddress? RemoteIpAddress => remoteIpAddress;

    public void NotifyHeartbeatReceived()
    {
        lastHeartbeatReceived = timeProvider.GetUtcNow();
    }

    public async Task SendAsync(ControlMessage message, CancellationToken cancellationToken)
    {
        await outbound.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await inbound.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new System.IO.EndOfStreamException("The fake viewer disconnected.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        outbound.TryComplete();
        return ValueTask.CompletedTask;
    }
}
