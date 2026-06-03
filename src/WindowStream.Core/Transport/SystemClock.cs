namespace WindowStream.Core.Transport;

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new SystemClock();
    SystemClock() { }
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
