using System;

namespace WindowStream.Core.Protocol;

public static class ProtocolErrorCodeNames
{
    public static string ToWireName(ProtocolErrorCode code)
    {
        return code switch
        {
            ProtocolErrorCode.VersionMismatch => "VERSION_MISMATCH",
            ProtocolErrorCode.ViewerBusy => "VIEWER_BUSY",
            ProtocolErrorCode.WindowGone => "WINDOW_GONE",
            ProtocolErrorCode.CaptureFailed => "CAPTURE_FAILED",
            ProtocolErrorCode.EncodeFailed => "ENCODE_FAILED",
            ProtocolErrorCode.MalformedMessage => "MALFORMED_MESSAGE",
            ProtocolErrorCode.EncoderCapacity => "ENCODER_CAPACITY",
            ProtocolErrorCode.WindowNotFound => "WINDOW_NOT_FOUND",
            ProtocolErrorCode.StreamNotFound => "STREAM_NOT_FOUND",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "unknown error code")
        };
    }

    public static ProtocolErrorCode Parse(string wireName)
    {
        return wireName switch
        {
            "VERSION_MISMATCH" => ProtocolErrorCode.VersionMismatch,
            "VIEWER_BUSY" => ProtocolErrorCode.ViewerBusy,
            "WINDOW_GONE" => ProtocolErrorCode.WindowGone,
            "CAPTURE_FAILED" => ProtocolErrorCode.CaptureFailed,
            "ENCODE_FAILED" => ProtocolErrorCode.EncodeFailed,
            "MALFORMED_MESSAGE" => ProtocolErrorCode.MalformedMessage,
            "ENCODER_CAPACITY" => ProtocolErrorCode.EncoderCapacity,
            "WINDOW_NOT_FOUND" => ProtocolErrorCode.WindowNotFound,
            "STREAM_NOT_FOUND" => ProtocolErrorCode.StreamNotFound,
            _ => throw new ArgumentException($"unknown protocol error code: {wireName}", nameof(wireName))
        };
    }
}
