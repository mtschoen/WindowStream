namespace WindowStream.Core.Capture.Detection;

// Why a source stream stopped producing frames. v1 emits NeverStarted / SourceStalled
// (worker SourceFrameMonitor) and WorkerSilent (coordinator ChunkCadenceWatchdog).
// v2 will add Minimized / FocusThrottled / TargetClosed from WinEvent classification.
public enum StallCause
{
    NeverStarted,
    SourceStalled,
    WorkerSilent
}
