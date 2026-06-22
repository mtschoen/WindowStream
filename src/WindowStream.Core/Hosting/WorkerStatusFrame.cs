using WindowStream.Core.Capture.Detection;

namespace WindowStream.Core.Hosting;

// Out-of-band status sent worker -> coordinator alongside chunk frames. For stall/resume the
// cause is in Kind/Cause; for *Error kinds Message carries the exception text.
public sealed record WorkerStatusFrame(
    WorkerStatusKind Kind,
    StallCause Cause,
    uint LastFrameAgeMilliseconds,
    string Message);
