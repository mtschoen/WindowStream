using System.Collections.Immutable;
using WindowStream.Core.Observability;

namespace WindowStream.Server.Observability;

public sealed record ServerState
{
    public StageStatus Listening { get; init; } = StageStatus.Pending;
    public int? TcpPort { get; init; }
    public int? UdpPort { get; init; }
    public StageStatus ViewerConnected { get; init; } = StageStatus.Pending;
    public string? ViewerEndpoint { get; init; }
    public int WindowCount { get; init; }
    public ImmutableDictionary<int, StreamStateRow> Streams { get; init; }
        = ImmutableDictionary<int, StreamStateRow>.Empty;
}

public sealed class ServerStateReducer
{
    public ServerState State { get; private set; } = new();

    public void Apply(PipelineEvent pipelineEvent)
    {
        State = pipelineEvent switch
        {
            PipelineEvent.Listening listening => State with
            {
                Listening = StageStatus.Ok,
                TcpPort = listening.TcpPort,
                UdpPort = listening.UdpPort,
            },

            PipelineEvent.ViewerAccepted accepted => State with
            {
                ViewerConnected = StageStatus.Ok,
                ViewerEndpoint = accepted.Endpoint,
            },

            PipelineEvent.ViewerDisconnected => State with
            {
                ViewerConnected = StageStatus.Pending,
                ViewerEndpoint = null,
            },

            PipelineEvent.WindowAppeared => State with { WindowCount = State.WindowCount + 1 },

            PipelineEvent.WindowDisappeared => State with { WindowCount = Math.Max(0, State.WindowCount - 1) },

            PipelineEvent.OpenStreamReceived open => State with
            {
                Streams = State.Streams.SetItem(open.StreamId!.Value, new StreamStateRow
                {
                    WindowId = open.WindowId,
                }),
            },

            PipelineEvent.WorkerSpawning spawning => UpdateStream(spawning.StreamId!.Value, row => row with
            {
                WorkerSpawn = StageStatus.InProgress,
            }),

            PipelineEvent.WorkerSpawned => UpdateStream(pipelineEvent.StreamId!.Value, row => row with
            {
                WorkerSpawn = StageStatus.Ok,
            }),

            PipelineEvent.WorkerSpawnFailed failed => UpdateStream(failed.StreamId!.Value, row => row with
            {
                WorkerSpawn = StageStatus.Error,
                WorkerSpawnError = failed.Exception.Message,
            }),

            PipelineEvent.CaptureStarted captured => UpdateStream(captured.StreamId!.Value, row => row with
            {
                Capture = StageStatus.Ok,
                CaptureWidth = captured.Width,
                CaptureHeight = captured.Height,
            }),

            PipelineEvent.CaptureFailed captureFailed => UpdateStream(captureFailed.StreamId!.Value, row => row with
            {
                Capture = StageStatus.Error,
                CaptureError = captureFailed.Exception.Message,
            }),

            PipelineEvent.EncodeStarted encodeStarted => UpdateStream(encodeStarted.StreamId!.Value, row => row with
            {
                Encode = StageStatus.Ok,
                EncodeFramesPerSecond = encodeStarted.TargetFramesPerSecond,
                EncodeBitrateKilobitsPerSecond = encodeStarted.BitrateKilobitsPerSecond,
            }),

            PipelineEvent.EncodeFailed encodeFailed => UpdateStream(encodeFailed.StreamId!.Value, row => row with
            {
                Encode = StageStatus.Error,
                EncodeError = encodeFailed.Exception.Message,
            }),

            PipelineEvent.FramesFlowing flowing => UpdateStream(flowing.StreamId!.Value, row => row with
            {
                UdpSend = StageStatus.Ok,
                MeasuredFramesPerSecond = flowing.MeasuredFramesPerSecond,
                MeasuredBitrateKilobitsPerSecond = flowing.BitrateKilobitsPerSecond,
            }),

            PipelineEvent.StreamStopped stopped => State with
            {
                Streams = State.Streams.Remove(stopped.StreamId!.Value),
            },

            _ => State,
        };
    }

    ServerState UpdateStream(int streamId, Func<StreamStateRow, StreamStateRow> update)
    {
        if (!State.Streams.TryGetValue(streamId, out var existing))
            return State;

        return State with
        {
            Streams = State.Streams.SetItem(streamId, update(existing)),
        };
    }
}
