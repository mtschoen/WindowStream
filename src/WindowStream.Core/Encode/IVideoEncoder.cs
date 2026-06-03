using WindowStream.Core.Capture;

namespace WindowStream.Core.Encode;

public interface IVideoEncoder : IAsyncDisposable
{
    void Configure(EncoderOptions options);
    Task EncodeAsync(CapturedFrame frame, CancellationToken cancellationToken);
    void RequestKeyframe();
    IAsyncEnumerable<EncodedChunk> EncodedChunks { get; }
}
