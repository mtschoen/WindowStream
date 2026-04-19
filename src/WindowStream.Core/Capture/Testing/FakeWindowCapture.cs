using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;

namespace WindowStream.Core.Capture.Testing;

public sealed class FakeWindowCapture : IWindowCapture
{
    internal readonly Channel<object> channel =
        Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public WindowHandle handle { get; }
    public CaptureOptions options { get; }
    public IAsyncEnumerable<CapturedFrame> Frames { get; }

    private readonly CancellationToken cancellationToken;

    public FakeWindowCapture(WindowHandle handle, CaptureOptions options, CancellationToken cancellationToken)
    {
        this.handle = handle;
        this.options = options;
        this.cancellationToken = cancellationToken;
        this.Frames = ReadFramesAsync();
    }

    private async IAsyncEnumerable<CapturedFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken enumeratorCancellation = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, enumeratorCancellation);
        while (await channel.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out object? next))
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

    public ValueTask DisposeAsync()
    {
        channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
