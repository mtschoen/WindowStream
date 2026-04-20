namespace WindowStream.Core.Session;

public sealed record SessionHostOptions(
    int HeartbeatIntervalMilliseconds = 2000,
    int HeartbeatTimeoutMilliseconds = 6000,
    int ServerVersion = 1,
    int StreamId = 1,
    string Codec = "h264");
