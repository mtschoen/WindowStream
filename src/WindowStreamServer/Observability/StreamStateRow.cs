namespace WindowStream.Server.Observability;

public sealed record StreamStateRow
{
    public required ulong WindowId { get; init; }
    public string Title { get; init; } = "";
    public StageStatus WorkerSpawn { get; init; } = StageStatus.Pending;
    public string? WorkerSpawnError { get; init; }
    public StageStatus Capture { get; init; } = StageStatus.Pending;
    public string? CaptureError { get; init; }
    public int? CaptureWidth { get; init; }
    public int? CaptureHeight { get; init; }
    public StageStatus Encode { get; init; } = StageStatus.Pending;
    public string? EncodeError { get; init; }
    public int? EncodeFramesPerSecond { get; init; }
    public int? EncodeBitrateKilobitsPerSecond { get; init; }
    public StageStatus UdpSend { get; init; } = StageStatus.Pending;
    public double? MeasuredFramesPerSecond { get; init; }
    public int? MeasuredBitrateKilobitsPerSecond { get; init; }
}
