using WindowStream.Core.Capture.Detection;

namespace WindowStream.Core.Protocol;

public static class StallCauseNames
{
    public static string ToWireName(StallCause cause)
    {
        return cause switch
        {
            StallCause.NeverStarted => "NEVER_STARTED",
            StallCause.SourceStalled => "SOURCE_STALLED",
            StallCause.WorkerSilent => "WORKER_SILENT",
            _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "unknown stall cause")
        };
    }

    public static StallCause Parse(string wireName)
    {
        return wireName switch
        {
            "NEVER_STARTED" => StallCause.NeverStarted,
            "SOURCE_STALLED" => StallCause.SourceStalled,
            "WORKER_SILENT" => StallCause.WorkerSilent,
            _ => throw new ArgumentException($"unknown stall cause: {wireName}", nameof(wireName))
        };
    }
}
