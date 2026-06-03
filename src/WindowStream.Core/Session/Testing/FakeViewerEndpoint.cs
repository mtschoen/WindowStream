using System.Threading.Channels;
using WindowStream.Core.Protocol;

namespace WindowStream.Core.Session.Testing;

/// <summary>
/// The viewer-side of a fake connection. Writes messages that the server reads,
/// and reads messages that the server sends.
/// </summary>
public sealed class FakeViewerEndpoint : IAsyncDisposable
{
    readonly ChannelWriter<ControlMessage> _toServer;
    readonly ChannelReader<ControlMessage> _fromServer;
    bool _disposed;

    internal FakeViewerEndpoint(
        ChannelWriter<ControlMessage> toServer,
        ChannelReader<ControlMessage> fromServer)
    {
        _toServer = toServer;
        _fromServer = fromServer;
    }

    public async Task SendAsync(ControlMessage message, CancellationToken cancellationToken)
    {
        await _toServer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TMessage> ReceiveAsync<TMessage>(CancellationToken cancellationToken)
        where TMessage : ControlMessage
    {
        var message = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
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
            return await _fromServer.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new EndOfStreamException("The server disconnected.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _toServer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
