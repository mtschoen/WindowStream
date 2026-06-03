using WindowStream.Core.Transport;

namespace WindowStream.Core.Tests.Transport;

sealed class FakeClock : IClock
{
    DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    public DateTimeOffset UtcNow => _now;
    public void Advance(TimeSpan delta) => _now += delta;
}
