namespace WindowStream.Core.Protocol;

public enum StreamStoppedReason
{
    ClosedByViewer,
    WindowGone,
    EncoderFailed,
    CaptureFailed,
    StreamHung,
    ServerShutdown
}
