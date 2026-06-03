using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WindowStream.Core.Capture;

namespace WindowStream.Core.Encode.Testing;

public sealed class FakeVideoEncoder : IVideoEncoder
{
    readonly Channel<EncodedChunk> _channel =
        Channel.CreateUnbounded<EncodedChunk>(new UnboundedChannelOptions { SingleReader = true });

    EncoderOptions? _options;
    bool _nextKeyframe;
    int _nextIndex;
    bool _disposed;

    public int KeyframeRequestCount { get; private set; }

    public IAsyncEnumerable<EncodedChunk> EncodedChunks { get; }

    public FakeVideoEncoder()
    {
        EncodedChunks = ReadAsync();
    }

    public void Configure(EncoderOptions options)
    {
        if (_options is not null)
        {
            throw new InvalidOperationException("FakeVideoEncoder is already configured.");
        }
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task EncodeAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        if (_options is null)
        {
            throw new InvalidOperationException("Configure must be called before EncodeAsync.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var keyframe = _nextKeyframe || _nextIndex == 0;
        _nextKeyframe = false;
        var bytes = new[] { (byte)_nextIndex };
        _nextIndex++;
        _channel.Writer.TryWrite(new EncodedChunk(bytes, keyframe, frame.PresentationTimestampMicroseconds));
        return Task.CompletedTask;
    }

    public bool Stopped => _disposed;

    public void RequestKeyframe()
    {
        if (_options is null)
        {
            throw new InvalidOperationException("Configure must be called before RequestKeyframe.");
        }
        KeyframeRequestCount++;
        _nextKeyframe = true;
    }

    public void CompleteEncoding() => _channel.Writer.TryComplete();

    async IAsyncEnumerable<EncodedChunk> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out var chunk))
            {
                yield return chunk;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
