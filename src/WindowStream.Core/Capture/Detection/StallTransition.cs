namespace WindowStream.Core.Capture.Detection;

// The edge a detector observed on a single Evaluate/RecordFrame call. None most of the time.
public enum StallTransition
{
    None,
    Stalled,
    Resumed
}
