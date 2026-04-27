using System;

namespace WindowStream.Core.Protocol;

public static class StreamStoppedReasonNames
{
    public static string ToWireName(StreamStoppedReason reason)
    {
        return reason switch
        {
            StreamStoppedReason.ClosedByViewer => "CLOSED_BY_VIEWER",
            StreamStoppedReason.WindowGone => "WINDOW_GONE",
            StreamStoppedReason.EncoderFailed => "ENCODER_FAILED",
            StreamStoppedReason.CaptureFailed => "CAPTURE_FAILED",
            StreamStoppedReason.StreamHung => "STREAM_HUNG",
            StreamStoppedReason.ServerShutdown => "SERVER_SHUTDOWN",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "unknown stream-stopped reason")
        };
    }

    public static StreamStoppedReason Parse(string wireName)
    {
        return wireName switch
        {
            "CLOSED_BY_VIEWER" => StreamStoppedReason.ClosedByViewer,
            "WINDOW_GONE" => StreamStoppedReason.WindowGone,
            "ENCODER_FAILED" => StreamStoppedReason.EncoderFailed,
            "CAPTURE_FAILED" => StreamStoppedReason.CaptureFailed,
            "STREAM_HUNG" => StreamStoppedReason.StreamHung,
            "SERVER_SHUTDOWN" => StreamStoppedReason.ServerShutdown,
            _ => throw new ArgumentException($"unknown stream-stopped reason: {wireName}", nameof(wireName))
        };
    }
}
