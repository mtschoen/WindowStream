namespace WindowStream.Core.Protocol;

public enum ProtocolErrorCode
{
    VersionMismatch,
    ViewerBusy,
    WindowGone,
    CaptureFailed,
    EncodeFailed,
    MalformedMessage,
    EncoderCapacity,
    WindowNotFound,
    StreamNotFound
}
