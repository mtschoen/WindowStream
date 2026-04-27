namespace WindowStream.Core.Hosting;

public sealed record CoordinatorOptions(
    int HeartbeatIntervalMilliseconds,
    int HeartbeatTimeoutMilliseconds,
    int ServerVersion,
    int MaximumConcurrentStreams);
