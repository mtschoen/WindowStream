using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;

namespace WindowStream.Core.Encode;

public interface IVideoEncoder : IAsyncDisposable
{
    void Configure(EncoderOptions options);
    Task EncodeAsync(CapturedFrame frame, CancellationToken cancellationToken);
    void RequestKeyframe();
    IAsyncEnumerable<EncodedChunk> EncodedChunks { get; }
}
