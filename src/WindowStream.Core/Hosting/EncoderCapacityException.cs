using System;

namespace WindowStream.Core.Hosting;

public sealed class EncoderCapacityException : Exception
{
    public EncoderCapacityException(int maximum)
        : base($"server is at NVENC capacity ({maximum} streams)")
    {
    }
}
