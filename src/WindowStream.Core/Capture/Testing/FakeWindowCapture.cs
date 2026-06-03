using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace WindowStream.Core.Capture.Testing;

public sealed class FakeWindowCapture : IWindowCapture
{
    internal readonly Channel<object> _channel =
        Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public WindowHandle Handle { get; }
    public CaptureOptions Options { get; }
    public IAsyncEnumerable<CapturedFrame> Frames { get; }

    readonly CancellationToken _cancellationToken;

    public FakeWindowCapture(WindowHandle handle, CaptureOptions options, CancellationToken cancellationToken)
    {
        Handle = handle;
        Options = options;
        _cancellationToken = cancellationToken;
        Frames = ReadFramesAsync(cancellationToken);
    }

    async IAsyncEnumerable<CapturedFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken enumeratorCancellation = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _cancellationToken, enumeratorCancellation);
        while (await _channel.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out var next))
            {
                if (next is CapturedFrame frame)
                {
                    yield return frame;
                }
                else if (next is Exception exception)
                {
                    throw exception;
                }
                else
                {
                    yield break;
                }
            }
        }
    }

    public bool Stopped { get; private set; }

    public ValueTask DisposeAsync()
    {
        Stopped = true;
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
