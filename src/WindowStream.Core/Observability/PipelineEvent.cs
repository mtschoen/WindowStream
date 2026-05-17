using System.Diagnostics.CodeAnalysis;

namespace WindowStream.Core.Observability;

// Per-frame [FRAMECOUNT] markers are intentionally excluded — they stay on stderr/logcat to avoid flooding the in-app buffer.
public abstract record PipelineEvent
{
    public Severity Severity { get; init; }
    public int? StreamId { get; init; }

    // ── Server-scope events (StreamId = null) ────────────────────────────────

    public sealed record Listening : PipelineEvent
    {
        public int TcpPort { get; init; }
        public int UdpPort { get; init; }

        public Listening(int TcpPort, int UdpPort)
        {
            this.TcpPort = TcpPort;
            this.UdpPort = UdpPort;
            Severity = Severity.Info;
        }
    }

    public sealed record ViewerAccepted : PipelineEvent
    {
        public required string Endpoint { get; init; }

        [SetsRequiredMembers]
        public ViewerAccepted(string Endpoint)
        {
            this.Endpoint = Endpoint;
            Severity = Severity.Info;
        }
    }

    public sealed record ViewerDisconnected : PipelineEvent
    {
        public required string Endpoint { get; init; }
        public required string Reason { get; init; }

        [SetsRequiredMembers]
        public ViewerDisconnected(string Endpoint, string Reason)
        {
            this.Endpoint = Endpoint;
            this.Reason = Reason;
            Severity = Severity.Info;
        }
    }

    public sealed record ServerHelloSent : PipelineEvent
    {
        public int WindowCount { get; init; }

        public ServerHelloSent(int WindowCount)
        {
            this.WindowCount = WindowCount;
            Severity = Severity.Info;
        }
    }

    public sealed record WindowAppeared : PipelineEvent
    {
        public ulong WindowId { get; init; }
        public required string Title { get; init; }
        public required string ProcessName { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        [SetsRequiredMembers]
        public WindowAppeared(ulong WindowId, string Title, string ProcessName, int Width, int Height)
        {
            this.WindowId = WindowId;
            this.Title = Title;
            this.ProcessName = ProcessName;
            this.Width = Width;
            this.Height = Height;
            Severity = Severity.Info;
        }
    }

    public sealed record WindowDisappeared : PipelineEvent
    {
        public ulong WindowId { get; init; }

        public WindowDisappeared(ulong WindowId)
        {
            this.WindowId = WindowId;
            Severity = Severity.Info;
        }
    }

    public sealed record WindowChanged : PipelineEvent
    {
        public ulong WindowId { get; init; }
        public string? NewTitle { get; init; }
        public int? NewWidth { get; init; }
        public int? NewHeight { get; init; }

        public WindowChanged(ulong WindowId, string? NewTitle, int? NewWidth, int? NewHeight)
        {
            this.WindowId = WindowId;
            this.NewTitle = NewTitle;
            this.NewWidth = NewWidth;
            this.NewHeight = NewHeight;
            Severity = Severity.Info;
        }
    }

    public sealed record ProbeFailed : PipelineEvent
    {
        public ulong WindowId { get; init; }
        public long Hwnd { get; init; }
        public required Exception Exception { get; init; }

        [SetsRequiredMembers]
        public ProbeFailed(ulong WindowId, long Hwnd, Exception Exception)
        {
            this.WindowId = WindowId;
            this.Hwnd = Hwnd;
            this.Exception = Exception;
            Severity = Severity.Error;
        }
    }

    public sealed record EnumerationFailed : PipelineEvent
    {
        public required Exception Exception { get; init; }

        [SetsRequiredMembers]
        public EnumerationFailed(Exception Exception)
        {
            this.Exception = Exception;
            Severity = Severity.Warning;
        }
    }

    // ── Stream-scope events (StreamId = stream identifier) ───────────────────

    public sealed record OpenStreamReceived : PipelineEvent
    {
        public ulong WindowId { get; init; }

        public OpenStreamReceived(int StreamId, ulong WindowId)
        {
            this.StreamId = StreamId;
            this.WindowId = WindowId;
            Severity = Severity.Info;
        }
    }

    public sealed record WorkerSpawning : PipelineEvent
    {
        public ulong WindowId { get; init; }

        public WorkerSpawning(int StreamId, ulong WindowId)
        {
            this.StreamId = StreamId;
            this.WindowId = WindowId;
            Severity = Severity.Info;
        }
    }

    public sealed record WorkerSpawned : PipelineEvent
    {
        public int ProcessId { get; init; }

        public WorkerSpawned(int StreamId, int ProcessId)
        {
            this.StreamId = StreamId;
            this.ProcessId = ProcessId;
            Severity = Severity.Info;
        }
    }

    public sealed record WorkerSpawnFailed : PipelineEvent
    {
        public required Exception Exception { get; init; }

        [SetsRequiredMembers]
        public WorkerSpawnFailed(int StreamId, Exception Exception)
        {
            this.StreamId = StreamId;
            this.Exception = Exception;
            Severity = Severity.Error;
        }
    }

    public sealed record CaptureStarted : PipelineEvent
    {
        public int Width { get; init; }
        public int Height { get; init; }

        public CaptureStarted(int StreamId, int Width, int Height)
        {
            this.StreamId = StreamId;
            this.Width = Width;
            this.Height = Height;
            Severity = Severity.Info;
        }
    }

    public sealed record CaptureFailed : PipelineEvent
    {
        public required Exception Exception { get; init; }

        [SetsRequiredMembers]
        public CaptureFailed(int StreamId, Exception Exception)
        {
            this.StreamId = StreamId;
            this.Exception = Exception;
            Severity = Severity.Error;
        }
    }

    public sealed record EncodeStarted : PipelineEvent
    {
        public int TargetFramesPerSecond { get; init; }
        public int BitrateKilobitsPerSecond { get; init; }

        public EncodeStarted(int StreamId, int TargetFramesPerSecond, int BitrateKilobitsPerSecond)
        {
            this.StreamId = StreamId;
            this.TargetFramesPerSecond = TargetFramesPerSecond;
            this.BitrateKilobitsPerSecond = BitrateKilobitsPerSecond;
            Severity = Severity.Info;
        }
    }

    public sealed record EncodeFailed : PipelineEvent
    {
        public required Exception Exception { get; init; }

        [SetsRequiredMembers]
        public EncodeFailed(int StreamId, Exception Exception)
        {
            this.StreamId = StreamId;
            this.Exception = Exception;
            Severity = Severity.Error;
        }
    }

    public sealed record FramesFlowing : PipelineEvent
    {
        public double MeasuredFramesPerSecond { get; init; }
        public int BitrateKilobitsPerSecond { get; init; }

        public FramesFlowing(int StreamId, double MeasuredFramesPerSecond, int BitrateKilobitsPerSecond)
        {
            this.StreamId = StreamId;
            this.MeasuredFramesPerSecond = MeasuredFramesPerSecond;
            this.BitrateKilobitsPerSecond = BitrateKilobitsPerSecond;
            Severity = Severity.Info;
        }
    }

    public sealed record StreamRefused : PipelineEvent
    {
        public required string ErrorCode { get; init; }
        public required string Message { get; init; }

        [SetsRequiredMembers]
        public StreamRefused(int StreamId, string ErrorCode, string Message)
        {
            this.StreamId = StreamId;
            this.ErrorCode = ErrorCode;
            this.Message = Message;
            Severity = Severity.Warning;
        }
    }

    public sealed record StreamStopped : PipelineEvent
    {
        public required string Reason { get; init; }

        [SetsRequiredMembers]
        public StreamStopped(int StreamId, string Reason)
        {
            this.StreamId = StreamId;
            this.Reason = Reason;
            Severity = Severity.Info;
        }
    }
}
