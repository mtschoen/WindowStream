using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WindowStream.Core.Hosting;

public sealed class LoadShedder
{
    private readonly Channel<TaggedChunk> input;
    private readonly Channel<TaggedChunk> output;
    private readonly int perStreamMaximumQueueDepth;

    public LoadShedder(Channel<TaggedChunk> input, Channel<TaggedChunk> output, int perStreamMaximumQueueDepth)
    {
        this.input = input;
        this.output = output;
        this.perStreamMaximumQueueDepth = perStreamMaximumQueueDepth;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Dictionary<int, Queue<TaggedChunk>> perStreamQueues = new();
        await foreach (TaggedChunk chunk in input.Reader.ReadAllAsync(cancellationToken))
        {
            if (!perStreamQueues.TryGetValue(chunk.StreamId, out Queue<TaggedChunk>? queue))
            {
                queue = new Queue<TaggedChunk>();
                perStreamQueues[chunk.StreamId] = queue;
            }
            queue.Enqueue(chunk);

            // Drop oldest non-keyframes until under threshold.
            while (queue.Count > perStreamMaximumQueueDepth)
            {
                TaggedChunk oldest = queue.Peek();
                if (oldest.Frame.IsKeyframe)
                {
                    // Walk forward and find the oldest non-keyframe to drop instead.
                    bool dropped = false;
                    Queue<TaggedChunk> rebuilt = new Queue<TaggedChunk>();
                    foreach (TaggedChunk queuedChunk in queue)
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
            while (queue.Count > 0 && output.Writer.TryWrite(queue.Peek()))
            {
                queue.Dequeue();
            }
        }
    }
}
