using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WindowStream.Core.Capture;

namespace WindowStream.Core.Encode.Testing;

public sealed class FakeVideoEncoder : IVideoEncoder
{
    private readonly Channel<EncodedChunk> channel =
        Channel.CreateUnbounded<EncodedChunk>(new UnboundedChannelOptions { SingleReader = true });
    private EncoderOptions? options;
    private bool nextKeyframe;
    private int nextIndex;
    private bool disposed;

    public IAsyncEnumerable<EncodedChunk> EncodedChunks { get; }

    public FakeVideoEncoder()
    {
        EncodedChunks = ReadAsync();
    }

    public void Configure(EncoderOptions options)
    {
        if (this.options is not null)
        {
            throw new InvalidOperationException("FakeVideoEncoder is already configured.");
        }
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task EncodeAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        if (options is null)
        {
            throw new InvalidOperationException("Configure must be called before EncodeAsync.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        bool keyframe = nextKeyframe || nextIndex == 0;
        nextKeyframe = false;
        byte[] bytes = new byte[] { (byte)nextIndex };
        nextIndex++;
        channel.Writer.TryWrite(new EncodedChunk(bytes, keyframe, frame.presentationTimestampMicroseconds));
        return Task.CompletedTask;
    }

    public void RequestKeyframe()
    {
        if (options is null)
        {
            throw new InvalidOperationException("Configure must be called before RequestKeyframe.");
        }
        nextKeyframe = true;
    }

    public void CompleteEncoding() => channel.Writer.TryComplete();

    private async IAsyncEnumerable<EncodedChunk> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out EncodedChunk? chunk))
            {
                yield return chunk;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
