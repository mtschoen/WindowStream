using System;

namespace WindowStream.Core.Observability;

/// <summary>
/// Typed pipeline-stage events emitted from coordinator and worker code.
/// The Diagnostics façade routes these through ILogger; sinks fan them out
/// to the in-app dashboard and a rotating JSONL file.
///
/// Per-frame markers ([FRAMECOUNT]) are deliberately NOT modeled here —
/// they live on stderr / logcat to avoid flooding the in-app buffer.
/// </summary>
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
        public string Endpoint { get; init; }

        public ViewerAccepted(string Endpoint)
        {
            this.Endpoint = Endpoint;
            Severity = Severity.Info;
        }
    }

    public sealed record ViewerDisconnected : PipelineEvent
    {
        public string Endpoint { get; init; }
        public string Reason { get; init; }

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
        public string Title { get; init; }
        public string ProcessName { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

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
        public Exception Exception { get; init; }

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
        public Exception Exception { get; init; }

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
        public int Pid { get; init; }

        public WorkerSpawned(int StreamId, int Pid)
        {
            this.StreamId = StreamId;
            this.Pid = Pid;
            Severity = Severity.Info;
        }
    }

    public sealed record WorkerSpawnFailed : PipelineEvent
    {
        public Exception Exception { get; init; }

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
        public Exception Exception { get; init; }

        public CaptureFailed(int StreamId, Exception Exception)
        {
            this.StreamId = StreamId;
            this.Exception = Exception;
            Severity = Severity.Error;
        }
    }

    public sealed record EncodeStarted : PipelineEvent
    {
        public int Fps { get; init; }
        public int Kbps { get; init; }

        public EncodeStarted(int StreamId, int Fps, int Kbps)
        {
            this.StreamId = StreamId;
            this.Fps = Fps;
            this.Kbps = Kbps;
            Severity = Severity.Info;
        }
    }

    public sealed record EncodeFailed : PipelineEvent
    {
        public Exception Exception { get; init; }

        public EncodeFailed(int StreamId, Exception Exception)
        {
            this.StreamId = StreamId;
            this.Exception = Exception;
            Severity = Severity.Error;
        }
    }

    public sealed record FramesFlowing : PipelineEvent
    {
        public double Fps { get; init; }
        public int Kbps { get; init; }

        public FramesFlowing(int StreamId, double Fps, int Kbps)
        {
            this.StreamId = StreamId;
            this.Fps = Fps;
            this.Kbps = Kbps;
            Severity = Severity.Info;
        }
    }

    public sealed record StreamRefused : PipelineEvent
    {
        public string ErrorCode { get; init; }
        public string Message { get; init; }

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
        public string Reason { get; init; }

        public StreamStopped(int StreamId, string Reason)
        {
            this.StreamId = StreamId;
            this.Reason = Reason;
            Severity = Severity.Info;
        }
    }
}
