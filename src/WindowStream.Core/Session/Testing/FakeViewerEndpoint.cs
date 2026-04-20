using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WindowStream.Core.Protocol;

namespace WindowStream.Core.Session.Testing;

/// <summary>
/// The viewer-side of a fake connection. Writes messages that the server reads,
/// and reads messages that the server sends.
/// </summary>
public sealed class FakeViewerEndpoint : IAsyncDisposable
{
    private readonly ChannelWriter<ControlMessage> toServer;
    private readonly ChannelReader<ControlMessage> fromServer;
    private bool disposed;

    internal FakeViewerEndpoint(
        ChannelWriter<ControlMessage> toServer,
        ChannelReader<ControlMessage> fromServer)
    {
        this.toServer = toServer;
        this.fromServer = fromServer;
    }

    public async Task SendAsync(ControlMessage message, CancellationToken cancellationToken)
    {
        await toServer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TMessage> ReceiveAsync<TMessage>(CancellationToken cancellationToken)
        where TMessage : ControlMessage
    {
        ControlMessage message = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (message is not TMessage typed)
        {
            throw new InvalidOperationException(
                $"Expected {typeof(TMessage).Name} but received {message.GetType().Name}");
        }
        return typed;
    }

    public async Task<ControlMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await fromServer.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new System.IO.EndOfStreamException("The server disconnected.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        toServer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
