using System;

namespace WindowStream.Core.Protocol;

public sealed class MalformedMessageException : Exception
{
    public MalformedMessageException(string message) : base(message) { }
    public MalformedMessageException(string message, Exception innerException) : base(message, innerException) { }
}
