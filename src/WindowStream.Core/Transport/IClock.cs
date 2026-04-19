using System;

namespace WindowStream.Core.Transport;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
