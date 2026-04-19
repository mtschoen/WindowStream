using System;
using WindowStream.Core.Transport;

namespace WindowStream.Core.Tests.Transport;

internal sealed class FakeClock : IClock
{
    private DateTimeOffset now = DateTimeOffset.UnixEpoch;
    public DateTimeOffset UtcNow => now;
    public void Advance(TimeSpan delta) => now += delta;
}
