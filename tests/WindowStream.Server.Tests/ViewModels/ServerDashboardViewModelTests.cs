using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Events;
using WindowStream.Core.Observability;
using WindowStream.Core.Session;
using WindowStream.Core.Session.Testing;
using WindowStream.Server.Observability;
using WindowStream.Server.ViewModels;
using Xunit;

namespace WindowStream.Server.Tests.ViewModels;

public sealed class ServerDashboardViewModelTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static readonly Serilog.Parsing.MessageTemplateParser MessageTemplateParser = new();

    private static LogEvent MakeLogEvent(string message = "test message")
    {
        return new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            MessageTemplateParser.Parse(message),
            Array.Empty<LogEventProperty>());
    }

    private static ServerDashboardViewModel MakeViewModel(InAppDashboardSink? sink = null, ISessionHostLauncher? launcher = null)
        => new(launcher ?? new FakeSessionHostLauncher(), sink ?? new InAppDashboardSink(capacity: 16));

    // ── constructor + snapshot-replay ────────────────────────────────────────

    [Fact]
    public void Constructor_Replays_Existing_Sink_Entries_Into_Recent_Events()
    {
        InAppDashboardSink sink = new(capacity: 16);
        sink.Emit(MakeLogEvent("first entry"));

        ServerDashboardViewModel viewModel = MakeViewModel(sink);

        Assert.Single(viewModel.RecentEvents);
        Assert.Equal("first entry", viewModel.RecentEvents[0].Message);
    }

    [Fact]
    public void Constructor_Replays_Multiple_Existing_Sink_Entries_In_Order()
    {
        InAppDashboardSink sink = new(capacity: 16);
        sink.Emit(MakeLogEvent("alpha"));
        sink.Emit(MakeLogEvent("beta"));

        ServerDashboardViewModel viewModel = MakeViewModel(sink);

        Assert.Equal(2, viewModel.RecentEvents.Count);
        Assert.Equal("alpha", viewModel.RecentEvents[0].Message);
        Assert.Equal("beta", viewModel.RecentEvents[1].Message);
    }

    // ── initial state ────────────────────────────────────────────────────────

    [Fact]
    public void Initial_Server_Status_Is_Starting()
    {
        ServerDashboardViewModel viewModel = MakeViewModel();

        Assert.Equal("Starting…", viewModel.ServerStatus);
    }

    [Fact]
    public void Initial_Tcp_And_Udp_Ports_Are_Zero()
    {
        ServerDashboardViewModel viewModel = MakeViewModel();

        Assert.Equal(0, viewModel.TcpPort);
        Assert.Equal(0, viewModel.UdpPort);
    }

    [Fact]
    public void Initial_Connected_Viewer_Is_Null()
    {
        ServerDashboardViewModel viewModel = MakeViewModel();

        Assert.Null(viewModel.ConnectedViewer);
    }

    [Fact]
    public void Initial_Active_Stream_Count_Is_Zero()
    {
        ServerDashboardViewModel viewModel = MakeViewModel();

        Assert.Equal(0, viewModel.ActiveStreamCount);
    }

    [Fact]
    public void Initial_Available_Window_Count_Is_Zero()
    {
        ServerDashboardViewModel viewModel = MakeViewModel();

        Assert.Equal(0, viewModel.AvailableWindowCount);
    }

    // ── ApplyEvent + reducer path ─────────────────────────────────────────────

    [Fact]
    public void Apply_Listening_Event_Updates_Server_Status_To_Serving()
    {
        ServerDashboardViewModel viewModel = MakeViewModel();

        viewModel.ApplyEvent(new PipelineEvent.Listening(TcpPort: 9000, UdpPort: 9001));

        Assert.Equal("Serving", viewModel.ServerStatus);
    }

    [Fact]
    public void Apply_Listening_Event_Updates_Tcp_And_Udp_Ports()
    {
        ServerDashboardViewModel viewModel = MakeViewModel();

        viewModel.ApplyEvent(new PipelineEvent.Listening(TcpPort: 7777, UdpPort: 7778));

        Assert.Equal(7777, viewModel.TcpPort);
        Assert.Equal(7778, viewModel.UdpPort);
    }

    [Fact]
    public void Apply_Viewer_Accepted_Updates_Connected_Viewer()
    {
        ServerDashboardViewModel viewModel = MakeViewModel();

        viewModel.ApplyEvent(new PipelineEvent.ViewerAccepted("10.0.0.5:51001"));

        Assert.Equal("10.0.0.5:51001", viewModel.ConnectedViewer);
    }

    [Fact]
    public void Apply_Viewer_Disconnected_Clears_Connected_Viewer()
    {
        ServerDashboardViewModel viewModel = MakeViewModel();
        viewModel.ApplyEvent(new PipelineEvent.ViewerAccepted("10.0.0.5:51001"));

        viewModel.ApplyEvent(new PipelineEvent.ViewerDisconnected("10.0.0.5:51001", "closed"));

        Assert.Null(viewModel.ConnectedViewer);
    }

    [Fact]
    public void Apply_Window_Appeared_Increments_Available_Window_Count()
    {
        ServerDashboardViewModel viewModel = MakeViewModel();

        viewModel.ApplyEvent(new PipelineEvent.WindowAppeared(1UL, "Notepad", "notepad", 800, 600));

        Assert.Equal(1, viewModel.AvailableWindowCount);
    }

    [Fact]
    public void Apply_Open_Stream_Then_Stream_Stopped_Updates_Active_Stream_Count()
    {
        ServerDashboardViewModel viewModel = MakeViewModel();
        viewModel.ApplyEvent(new PipelineEvent.OpenStreamReceived(StreamId: 1, WindowId: 42UL));
        Assert.Equal(1, viewModel.ActiveStreamCount);

        viewModel.ApplyEvent(new PipelineEvent.StreamStopped(StreamId: 1, Reason: "done"));

        Assert.Equal(0, viewModel.ActiveStreamCount);
    }

    // ── Headless-host fallback paths ──────────────────────────────────────────

    [Fact]
    public void On_Sink_Event_Appends_Entry_Synchronously_In_Headless_Host()
    {
        // Ensures OnSinkEvent catch-fallback runs AppendEntry synchronously when
        // BeginInvokeOnMainThread is unavailable (headless xUnit).
        InAppDashboardSink sink = new(capacity: 16);
        ServerDashboardViewModel viewModel = MakeViewModel(sink);

        sink.Emit(MakeLogEvent("live event"));

        Assert.Single(viewModel.RecentEvents);
        Assert.Equal("live event", viewModel.RecentEvents[0].Message);
    }

    [Fact]
    public void Apply_Event_Fires_Property_Changed_Synchronously_In_Headless_Host()
    {
        // Ensures ApplyEvent catch-fallback calls RaiseAll synchronously, covering
        // the non-null PropertyChanged?.Invoke branch in headless xUnit.
        ServerDashboardViewModel viewModel = MakeViewModel();
        List<string?> raisedProperties = new();
        viewModel.PropertyChanged += (_, eventArguments) => raisedProperties.Add(eventArguments.PropertyName);

        viewModel.ApplyEvent(new PipelineEvent.Listening(TcpPort: 5000, UdpPort: 5001));

        Assert.Contains(nameof(ServerDashboardViewModel.ServerStatus), raisedProperties);
        Assert.Contains(nameof(ServerDashboardViewModel.TcpPort), raisedProperties);
        Assert.Contains(nameof(ServerDashboardViewModel.UdpPort), raisedProperties);
        Assert.Contains(nameof(ServerDashboardViewModel.ConnectedViewer), raisedProperties);
        Assert.Contains(nameof(ServerDashboardViewModel.ActiveStreamCount), raisedProperties);
        Assert.Contains(nameof(ServerDashboardViewModel.AvailableWindowCount), raisedProperties);
        Assert.Contains(nameof(ServerDashboardViewModel.State), raisedProperties);
    }

    // ── Recent events cap ─────────────────────────────────────────────────────

    [Fact]
    public void Recent_Events_Capped_At_200_Entries()
    {
        InAppDashboardSink sink = new(capacity: 210);
        for (int index = 0; index < 205; index++)
            sink.Emit(MakeLogEvent($"entry {index}"));

        ServerDashboardViewModel viewModel = MakeViewModel(sink);

        Assert.Equal(200, viewModel.RecentEvents.Count);
    }

    // ── StartServingAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task Start_Serving_Async_Completes_Normally_On_Cancellation()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();
        ServerDashboardViewModel viewModel = new(new CancellingSessionHostLauncher(), new InAppDashboardSink(capacity: 16));

        // Must not throw — OperationCanceledException is swallowed.
        await viewModel.StartServingAsync(cancellationTokenSource.Token);
    }

    [Fact]
    public async Task Start_Serving_Async_Swallows_General_Exception()
    {
        ServerDashboardViewModel viewModel = new(new ThrowingSessionHostLauncher("kaboom"), new InAppDashboardSink(capacity: 16));

        // Must not throw — Exception is caught and written to Debug.
        await viewModel.StartServingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Start_Serving_Async_Calls_Launcher_Launch_Async()
    {
        FakeSessionHostLauncher launcher = new();
        ServerDashboardViewModel viewModel = MakeViewModel(launcher: launcher);

        await viewModel.StartServingAsync(CancellationToken.None);

        Assert.True(launcher.Launched);
    }

    // ── LogEntryViewModel ─────────────────────────────────────────────────────

    [Fact]
    public void Log_Entry_View_Model_Formats_Timestamp()
    {
        DateTimeOffset timestamp = new DateTimeOffset(2026, 5, 17, 14, 30, 45, 123, TimeSpan.Zero);
        LogEntry entry = new(timestamp, WindowStream.Core.Observability.Severity.Info, "Log", null, "hello", null);
        LogEntryViewModel viewModel = new(entry);

        string local = timestamp.LocalDateTime.ToString("HH:mm:ss.fff");
        Assert.Equal(local, viewModel.Timestamp);
    }

    [Fact]
    public void Log_Entry_View_Model_Uppercases_Severity()
    {
        LogEntry entry = new(DateTimeOffset.UtcNow, WindowStream.Core.Observability.Severity.Warning, "Log", null, "msg", null);
        LogEntryViewModel viewModel = new(entry);

        Assert.Equal("WARNING", viewModel.Severity);
    }

    [Fact]
    public void Log_Entry_View_Model_Error_Severity_Color_Is_Red()
    {
        LogEntry entry = new(DateTimeOffset.UtcNow, WindowStream.Core.Observability.Severity.Error, "Log", null, "msg", null);
        LogEntryViewModel viewModel = new(entry);

        Assert.Equal("#FF6060", viewModel.SeverityColor);
    }

    [Fact]
    public void Log_Entry_View_Model_Warning_Severity_Color_Is_Amber()
    {
        LogEntry entry = new(DateTimeOffset.UtcNow, WindowStream.Core.Observability.Severity.Warning, "Log", null, "msg", null);
        LogEntryViewModel viewModel = new(entry);

        Assert.Equal("#FFC040", viewModel.SeverityColor);
    }

    [Fact]
    public void Log_Entry_View_Model_Info_Severity_Color_Is_Grey()
    {
        LogEntry entry = new(DateTimeOffset.UtcNow, WindowStream.Core.Observability.Severity.Info, "Log", null, "msg", null);
        LogEntryViewModel viewModel = new(entry);

        Assert.Equal("#C0C0C0", viewModel.SeverityColor);
    }

    [Fact]
    public void Log_Entry_View_Model_Exposes_Event_Type_And_Stream_Id()
    {
        LogEntry entry = new(DateTimeOffset.UtcNow, WindowStream.Core.Observability.Severity.Info, "CaptureStarted", 42, "msg", null);
        LogEntryViewModel viewModel = new(entry);

        Assert.Equal("CaptureStarted", viewModel.EventType);
        Assert.Equal(42, viewModel.StreamId);
    }

    [Fact]
    public void Log_Entry_View_Model_Exposes_Message()
    {
        LogEntry entry = new(DateTimeOffset.UtcNow, WindowStream.Core.Observability.Severity.Info, "Log", null, "hello world", null);
        LogEntryViewModel viewModel = new(entry);

        Assert.Equal("hello world", viewModel.Message);
    }

    // ── private test helpers ──────────────────────────────────────────────────

    private sealed class CancellingSessionHostLauncher : ISessionHostLauncher
    {
        public Task LaunchAsync(CancellationToken cancellationToken) =>
            Task.FromCanceled(cancellationToken.IsCancellationRequested
                ? cancellationToken
                : throw new InvalidOperationException("token must be cancelled"));
    }

    private sealed class ThrowingSessionHostLauncher : ISessionHostLauncher
    {
        private readonly string message;

        public ThrowingSessionHostLauncher(string message)
        {
            this.message = message;
        }

        public Task LaunchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }
}
