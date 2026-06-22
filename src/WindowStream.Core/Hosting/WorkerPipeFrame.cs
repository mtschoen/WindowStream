namespace WindowStream.Core.Hosting;

// Discriminated worker -> coordinator frame: either an encoded chunk or a status report.
public abstract record WorkerPipeFrame
{
    public sealed record ChunkPayload(WorkerChunkFrame Frame) : WorkerPipeFrame;
    public sealed record StatusPayload(WorkerStatusFrame Status) : WorkerPipeFrame;
}
