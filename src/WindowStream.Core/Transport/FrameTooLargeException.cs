using System;

namespace WindowStream.Core.Transport;

public sealed class FrameTooLargeException : Exception
{
    public FrameTooLargeException(int actualLength, int maximumLength)
        : base($"frame payload {actualLength} bytes exceeds maximum {maximumLength}")
    {
        ActualLength = actualLength;
        MaximumLength = maximumLength;
    }

    public int ActualLength { get; }
    public int MaximumLength { get; }
}
