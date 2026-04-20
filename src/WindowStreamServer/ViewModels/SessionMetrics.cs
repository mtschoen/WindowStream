namespace WindowStream.Server.ViewModels;

public sealed record SessionMetrics(
    double FramesPerSecond,
    int BitrateKilobitsPerSecond,
    string? ConnectedViewerEndpoint);
