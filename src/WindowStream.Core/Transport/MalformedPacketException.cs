using System;

namespace WindowStream.Core.Transport;

public sealed class MalformedPacketException : Exception
{
    public MalformedPacketException(string message) : base(message) { }
}
