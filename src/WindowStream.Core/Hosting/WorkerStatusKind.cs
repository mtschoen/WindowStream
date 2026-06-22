namespace WindowStream.Core.Hosting;

public enum WorkerStatusKind : byte
{
    SourceStalled = 0,
    SourceResumed = 1,
    CaptureError = 2,
    EncodeError = 3
}
