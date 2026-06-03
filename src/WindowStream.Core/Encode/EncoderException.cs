namespace WindowStream.Core.Encode;

public class EncoderException : Exception
{
    public int? FfmpegErrorCode { get; }

    public EncoderException(string message) : base(message) { }
    public EncoderException(string message, int ffmpegErrorCode) : base(message)
    {
        FfmpegErrorCode = ffmpegErrorCode;
    }
    public EncoderException(string message, Exception innerException) : base(message, innerException) { }
}
