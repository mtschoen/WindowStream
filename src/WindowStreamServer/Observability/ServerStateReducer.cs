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

            // StreamId is non-null by construction for every stream-scoped PipelineEvent subtype
            // below (see PipelineEvent.cs); the `{} streamId` not-null pattern makes that invariant
            // explicit instead of asserting it with `!`, and falls back to a no-op if it's ever violated.
            PipelineEvent.OpenStreamReceived { StreamId: { } streamId } open => State with
            {
                Streams = State.Streams.SetItem(streamId, new StreamStateRow
                {
                    WindowId = open.WindowId,
                }),
            },

            PipelineEvent.WorkerSpawning { StreamId: { } streamId } => UpdateStream(streamId, row => row with
            {
                WorkerSpawn = StageStatus.InProgress,
            }),

            PipelineEvent.WorkerSpawned { StreamId: { } streamId } => UpdateStream(streamId, row => row with
            {
                WorkerSpawn = StageStatus.Ok,
            }),

            PipelineEvent.WorkerSpawnFailed { StreamId: { } streamId } failed => UpdateStream(streamId, row => row with
            {
                WorkerSpawn = StageStatus.Error,
                WorkerSpawnError = failed.Exception.Message,
            }),

            PipelineEvent.CaptureStarted { StreamId: { } streamId } captured => UpdateStream(streamId, row => row with
            {
                Capture = StageStatus.Ok,
                CaptureWidth = captured.Width,
                CaptureHeight = captured.Height,
            }),

            PipelineEvent.CaptureFailed { StreamId: { } streamId } captureFailed => UpdateStream(streamId, row => row with
            {
                Capture = StageStatus.Error,
                CaptureError = captureFailed.Exception.Message,
            }),

            PipelineEvent.EncodeStarted { StreamId: { } streamId } encodeStarted => UpdateStream(streamId, row => row with
            {
                Encode = StageStatus.Ok,
                EncodeFramesPerSecond = encodeStarted.TargetFramesPerSecond,
                EncodeBitrateKilobitsPerSecond = encodeStarted.BitrateKilobitsPerSecond,
            }),

            PipelineEvent.EncodeFailed { StreamId: { } streamId } encodeFailed => UpdateStream(streamId, row => row with
            {
                Encode = StageStatus.Error,
                EncodeError = encodeFailed.Exception.Message,
            }),

            PipelineEvent.FramesFlowing { StreamId: { } streamId } flowing => UpdateStream(streamId, row => row with
            {
                UdpSend = StageStatus.Ok,
                MeasuredFramesPerSecond = flowing.MeasuredFramesPerSecond,
                MeasuredBitrateKilobitsPerSecond = flowing.BitrateKilobitsPerSecond,
            }),

            PipelineEvent.SourceStalled { StreamId: { } streamId } stalled => UpdateStream(streamId, row => row with
            {
                IsStalled = true,
                StallCause = stalled.Cause,
            }),

            PipelineEvent.SourceResumed { StreamId: { } streamId } => UpdateStream(streamId, row => row with
            {
                IsStalled = false,
                StallCause = null,
            }),

            PipelineEvent.StreamStopped { StreamId: { } streamId } => State with
            {
                Streams = State.Streams.Remove(streamId),
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
