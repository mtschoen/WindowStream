namespace WindowStream.Core.Hosting;

public sealed record TaggedChunk(int StreamId, WorkerChunkFrame Frame);
