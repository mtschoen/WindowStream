namespace WindowStream.Core.Hosting;

public sealed record WorkerChunkFrame(
    ulong PresentationTimestampMicroseconds,
    bool IsKeyframe,
    byte[] Payload);
