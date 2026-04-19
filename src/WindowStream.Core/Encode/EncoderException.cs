using System;

namespace WindowStream.Core.Encode;

public class EncoderException : Exception
{
    public int? ffmpegErrorCode { get; }

    public EncoderException(string message) : base(message) { }
    public EncoderException(string message, int ffmpegErrorCode) : base(message)
    {
        this.ffmpegErrorCode = ffmpegErrorCode;
    }
    public EncoderException(string message, Exception innerException) : base(message, innerException) { }
}
