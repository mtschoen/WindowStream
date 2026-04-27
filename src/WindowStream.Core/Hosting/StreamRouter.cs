using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WindowStream.Core.Hosting;

public sealed class StreamRouter
{
    private readonly Channel<TaggedChunk> sink;

    public StreamRouter(Channel<TaggedChunk> sink)
    {
        this.sink = sink;
    }

    public async Task ReadFromPipeAsync(int streamId, Stream pipe, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                WorkerChunkFrame frame = await WorkerChunkPipe.ReadChunkAsync(pipe, cancellationToken).ConfigureAwait(false);
                await sink.Writer.WriteAsync(new TaggedChunk(streamId, frame), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (EndOfStreamException)
        {
            // worker pipe closed — normal stop
        }
        catch (OperationCanceledException)
        {
            // normal cancellation
        }
    }
}
