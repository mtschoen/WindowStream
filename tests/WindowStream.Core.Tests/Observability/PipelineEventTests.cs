using WindowStream.Core.Observability;
using Xunit;

namespace WindowStream.Core.Tests.Observability;

public class PipelineEventTests
{
    [Fact]
    public void Listening_Has_Info_Severity_And_Captures_Ports()
    {
        PipelineEvent.Listening listening = new(TcpPort: 53234, UdpPort: 53235);
        Assert.Equal(Severity.Info, listening.Severity);
        Assert.Null(listening.StreamId);
        Assert.Equal(53234, listening.TcpPort);
        Assert.Equal(53235, listening.UdpPort);
    }

    [Fact]
    public void WorkerSpawnFailed_Has_Error_Severity_And_Carries_StreamId()
    {
        var exception = new InvalidOperationException("worker boom");
        PipelineEvent.WorkerSpawnFailed evt = new(StreamId: 7, Exception: exception);
        Assert.Equal(Severity.Error, evt.Severity);
        Assert.Equal(7, evt.StreamId);
        Assert.Same(exception, evt.Exception);
    }

    [Fact]
    public void FramesFlowing_Heartbeat_Has_Info_And_StreamId()
    {
        PipelineEvent.FramesFlowing evt = new(StreamId: 3, MeasuredFramesPerSecond: 60.0, BitrateKilobitsPerSecond: 4800);
        Assert.Equal(Severity.Info, evt.Severity);
        Assert.Equal(3, evt.StreamId);
        Assert.Equal(60.0, evt.MeasuredFramesPerSecond);
    }

    [Fact]
    public void ViewerAccepted_Has_Info_Severity_And_Endpoint()
    {
        PipelineEvent.ViewerAccepted evt = new("192.168.1.42:48512");
        Assert.Equal(Severity.Info, evt.Severity);
        Assert.Null(evt.StreamId);
        Assert.Equal("192.168.1.42:48512", evt.Endpoint);
    }

    [Fact]
    public void ViewerDisconnected_Carries_Endpoint_And_Reason()
    {
        PipelineEvent.ViewerDisconnected evt = new("192.168.1.42:48512", "viewer-quit");
        Assert.Equal(Severity.Info, evt.Severity);
        Assert.Equal("viewer-quit", evt.Reason);
    }

    [Fact]
    public void ServerHelloSent_Carries_WindowCount()
    {
        PipelineEvent.ServerHelloSent evt = new(WindowCount: 12);
        Assert.Equal(Severity.Info, evt.Severity);
        Assert.Equal(12, evt.WindowCount);
    }

    [Fact]
    public void WindowAppeared_Carries_All_Window_Fields()
    {
        PipelineEvent.WindowAppeared evt = new(WindowId: 42UL, Title: "Firefox", ProcessName: "firefox.exe", Width: 1920, Height: 1080);
        Assert.Equal(Severity.Info, evt.Severity);
        Assert.Equal(42UL, evt.WindowId);
        Assert.Equal("Firefox", evt.Title);
        Assert.Equal("firefox.exe", evt.ProcessName);
        Assert.Equal(1920, evt.Width);
        Assert.Equal(1080, evt.Height);
    }

    [Fact]
    public void WindowDisappeared_Carries_WindowId()
    {
        PipelineEvent.WindowDisappeared evt = new(WindowId: 42UL);
        Assert.Equal(Severity.Info, evt.Severity);
        Assert.Equal(42UL, evt.WindowId);
    }

    [Fact]
    public void WindowChanged_Supports_Nullable_Updates()
    {
        PipelineEvent.WindowChanged evt = new(WindowId: 42UL, NewTitle: "new title", NewWidth: null, NewHeight: 720);
        Assert.Equal(Severity.Info, evt.Severity);
        Assert.Equal("new title", evt.NewTitle);
        Assert.Null(evt.NewWidth);
        Assert.Equal(720, evt.NewHeight);
    }

    [Fact]
    public void OpenStreamReceived_Propagates_StreamId_To_Base()
    {
        PipelineEvent.OpenStreamReceived evt = new(StreamId: 5, WindowId: 42UL);
        Assert.Equal(Severity.Info, evt.Severity);
        Assert.Equal(5, evt.StreamId);
        Assert.Equal(42UL, evt.WindowId);
    }

    [Fact]
    public void WorkerSpawning_Has_Info_With_StreamId()
    {
        PipelineEvent.WorkerSpawning evt = new(StreamId: 5, WindowId: 42UL);
        Assert.Equal(Severity.Info, evt.Severity);
        Assert.Equal(5, evt.StreamId);
        Assert.Equal(42UL, evt.WindowId);
    }

    [Fact]
    public void WorkerSpawned_Carries_ProcessId()
    {
        PipelineEvent.WorkerSpawned evt = new(StreamId: 5, ProcessId: 18420);
        Assert.Equal(Severity.Info, evt.Severity);
        Assert.Equal(18420, evt.ProcessId);
    }

    [Fact]
    public void CaptureStarted_Carries_Dimensions()
    {
        PipelineEvent.CaptureStarted evt = new(StreamId: 5, Width: 1920, Height: 1080);
        Assert.Equal(Severity.Info, evt.Severity);
        Assert.Equal(1920, evt.Width);
        Assert.Equal(1080, evt.Height);
    }

    [Fact]
    public void CaptureFailed_Has_Error_Severity_And_Exception()
    {
        InvalidOperationException exception = new("capture boom");
        PipelineEvent.CaptureFailed evt = new(StreamId: 5, Exception: exception);
        Assert.Equal(Severity.Error, evt.Severity);
        Assert.Same(exception, evt.Exception);
    }

    [Fact]
    public void EncodeStarted_Carries_TargetFramesPerSecond_And_Bitrate()
    {
        PipelineEvent.EncodeStarted evt = new(StreamId: 5, TargetFramesPerSecond: 60, BitrateKilobitsPerSecond: 4800);
        Assert.Equal(Severity.Info, evt.Severity);
        Assert.Equal(60, evt.TargetFramesPerSecond);
        Assert.Equal(4800, evt.BitrateKilobitsPerSecond);
    }

    [Fact]
    public void EncodeFailed_Has_Error_Severity_And_Exception()
    {
        InvalidOperationException exception = new("encode boom");
        PipelineEvent.EncodeFailed evt = new(StreamId: 5, Exception: exception);
        Assert.Equal(Severity.Error, evt.Severity);
        Assert.Same(exception, evt.Exception);
    }

    [Fact]
    public void StreamRefused_Has_Warning_Severity_And_Carries_Reason()
    {
        PipelineEvent.StreamRefused evt = new(StreamId: 5, ErrorCode: "WGC_FAIL", Message: "WGC E_FAIL");
        Assert.Equal(Severity.Warning, evt.Severity);
        Assert.Equal("WGC_FAIL", evt.ErrorCode);
        Assert.Equal("WGC E_FAIL", evt.Message);
    }

    [Fact]
    public void StreamStopped_Carries_Reason()
    {
        PipelineEvent.StreamStopped evt = new(StreamId: 5, Reason: "viewer-disconnect");
        Assert.Equal(Severity.Info, evt.Severity);
        Assert.Equal("viewer-disconnect", evt.Reason);
    }

    [Fact]
    public void ProbeFailed_Has_Error_With_Null_StreamId()
    {
        InvalidOperationException exception = new("probe boom");
        PipelineEvent.ProbeFailed evt = new(WindowId: 42UL, Hwnd: 131072L, Exception: exception);
        Assert.Equal(Severity.Error, evt.Severity);
        Assert.Null(evt.StreamId);
        Assert.Equal(42UL, evt.WindowId);
        Assert.Equal(131072L, evt.Hwnd);
        Assert.Same(exception, evt.Exception);
    }

    [Fact]
    public void EnumerationFailed_Has_Warning_And_Exception()
    {
        InvalidOperationException exception = new("enum boom");
        PipelineEvent.EnumerationFailed evt = new(exception);
        Assert.Equal(Severity.Warning, evt.Severity);
        Assert.Null(evt.StreamId);
        Assert.Same(exception, evt.Exception);
    }
}
