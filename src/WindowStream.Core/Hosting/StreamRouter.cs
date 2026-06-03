using System.Threading.Channels;

namespace WindowStream.Core.Hosting;

public sealed class StreamRouter
{
    readonly Channel<TaggedChunk> _sink;

    public StreamRouter(Channel<TaggedChunk> sink)
    {
        _sink = sink;
    }

    public async Task ReadFromPipeAsync(int streamId, Stream pipe, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await WorkerChunkPipe.ReadChunkAsync(pipe, cancellationToken).ConfigureAwait(false);
                await _sink.Writer.WriteAsync(new TaggedChunk(streamId, frame), cancellationToken).ConfigureAwait(false);
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
