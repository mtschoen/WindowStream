using System.Threading.Channels;

namespace WindowStream.Core.Hosting;

public sealed class LoadShedder
{
    readonly Channel<TaggedChunk> _input;
    readonly Channel<TaggedChunk> _output;
    readonly int _perStreamMaximumQueueDepth;

    public LoadShedder(Channel<TaggedChunk> input, Channel<TaggedChunk> output, int perStreamMaximumQueueDepth)
    {
        _input = input;
        _output = output;
        _perStreamMaximumQueueDepth = perStreamMaximumQueueDepth;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Dictionary<int, Queue<TaggedChunk>> perStreamQueues = new();
        await foreach (var chunk in _input.Reader.ReadAllAsync(cancellationToken))
        {
            if (!perStreamQueues.TryGetValue(chunk.StreamId, out var queue))
            {
                queue = new Queue<TaggedChunk>();
                perStreamQueues[chunk.StreamId] = queue;
            }
            queue.Enqueue(chunk);

            // Drop oldest non-keyframes until under threshold.
            while (queue.Count > _perStreamMaximumQueueDepth)
            {
                var oldest = queue.Peek();
                if (oldest.Frame.IsKeyframe)
                {
                    // Walk forward and find the oldest non-keyframe to drop instead.
                    var dropped = false;
                    var rebuilt = new Queue<TaggedChunk>();
                    foreach (var queuedChunk in queue)
                    {
                        if (!dropped && !queuedChunk.Frame.IsKeyframe) { dropped = true; continue; }
                        rebuilt.Enqueue(queuedChunk);
                    }
                    if (!dropped) break; // queue is all keyframes — leave it; pressure will resolve via output blocking
                    perStreamQueues[chunk.StreamId] = rebuilt;
                    queue = rebuilt;
                }
                else
                {
                    queue.Dequeue();
                }
            }

            // Try to push the head non-blockingly.
            while (queue.Count > 0 && _output.Writer.TryWrite(queue.Peek()))
            {
                queue.Dequeue();
            }
        }
    }
}
