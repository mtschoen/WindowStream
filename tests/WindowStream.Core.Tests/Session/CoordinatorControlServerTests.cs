using System.Net;
using WindowStream.Core.Capture.Detection;
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;
using WindowStream.Core.Protocol;
using WindowStream.Core.Session;
using WindowStream.Core.Session.Input;
using WindowStream.Core.Session.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Session;

public sealed class CoordinatorControlServerTests
{
    static readonly TimeSpan DefaultTestTimeout = TimeSpan.FromSeconds(10);

    static EncoderOptions DefaultEncoder(int widthPixels = 1280, int heightPixels = 720)
        => new EncoderOptions(widthPixels, heightPixels, 60, 8_000_000, 30, 2);

    static WindowDescriptor MakeWindow(ulong windowId, long hwnd = 0x100, int widthPixels = 1280, int heightPixels = 720)
        => new WindowDescriptor(
            WindowId: windowId,
            Hwnd: hwnd,
            ProcessId: 4242,
            ProcessName: "demo.exe",
            Title: $"Window {windowId}",
            PhysicalWidth: widthPixels,
            PhysicalHeight: heightPixels);

    static async Task<TMessage> NextNonHeartbeatAsync<TMessage>(
        FakeViewerEndpoint viewer, CancellationToken cancellationToken)
        where TMessage : ControlMessage
    {
        while (true)
        {
            var message = await viewer.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (message is HeartbeatMessage)
            {
                continue;
            }
            if (message is TMessage typed)
            {
                return typed;
            }
            throw new InvalidOperationException(
                $"Expected {typeof(TMessage).Name} but received {message.GetType().Name}");
        }
    }

    static async Task PollUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Hello_TriggersServerHelloWithWindowsSnapshot()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        harness.Windows.Add(MakeWindow(1));
        harness.Windows.Add(MakeWindow(2));
        harness.UdpPort = 64500;

        await using var viewer = harness.ConnectViewer();
        await viewer.SendAsync(
            new HelloMessage(2, new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellation.Token);

        var helloResponse = await viewer.ReceiveAsync<ServerHelloMessage>(cancellation.Token);
        Assert.Equal(2, helloResponse.ServerVersion);
        Assert.Equal(64500, helloResponse.UdpPort);
        Assert.Equal(2, helloResponse.Windows.Length);
        Assert.Equal(1ul, helloResponse.Windows[0].WindowId);
        Assert.Equal(2ul, helloResponse.Windows[1].WindowId);
    }

    [Fact]
    public async Task ListWindows_TriggersWindowSnapshot()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        harness.Windows.Add(MakeWindow(7));

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new ListWindowsMessage(), cancellation.Token);

        var snapshot = await NextNonHeartbeatAsync<WindowSnapshotMessage>(viewer, cancellation.Token);
        Assert.Single(snapshot.Windows);
        Assert.Equal(7ul, snapshot.Windows[0].WindowId);
    }

    [Fact]
    public async Task OpenStream_HappyPath_StartsAndEmitsStreamStarted()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[42] = 0xABCD;
        harness.WindowToEncoder[42] = DefaultEncoder(1920, 1080);

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 42), cancellation.Token);

        var started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);
        Assert.Equal(42ul, started.WindowId);
        Assert.Equal("h264", started.Codec);
        Assert.Equal(1920, started.Width);
        Assert.Equal(1080, started.Height);
        Assert.Equal(60, started.FramesPerSecond);
        Assert.Single(harness.Launcher.Launched);
    }

    [Fact]
    public async Task OpenStream_UnknownWindowId_EmitsError()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        // Note: nothing in WindowToHwnd for windowId=99.

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 99), cancellation.Token);

        var error = await NextNonHeartbeatAsync<ErrorMessage>(viewer, cancellation.Token);
        Assert.Equal(ProtocolErrorCode.WindowNotFound, error.Code);
        Assert.Empty(harness.Launcher.Launched);
    }

    [Fact]
    public async Task OpenStream_NoEncoderOptions_EmitsWindowNotFoundError()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[55] = 0x500;
        // No entry in WindowToEncoder.

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 55), cancellation.Token);

        var error = await NextNonHeartbeatAsync<ErrorMessage>(viewer, cancellation.Token);
        Assert.Equal(ProtocolErrorCode.WindowNotFound, error.Code);
    }

    [Fact]
    public async Task OpenStream_AtCapacity_EmitsEncoderCapacityError()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start(maximumConcurrentStreams: 1);
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToHwnd[2] = 0x200;
        harness.WindowToEncoder[1] = DefaultEncoder();
        harness.WindowToEncoder[2] = DefaultEncoder();

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);

        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        _ = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        await viewer.SendAsync(new OpenStreamMessage(WindowId: 2), cancellation.Token);
        var error = await NextNonHeartbeatAsync<ErrorMessage>(viewer, cancellation.Token);
        Assert.Equal(ProtocolErrorCode.EncoderCapacity, error.Code);
    }

    [Fact]
    public async Task CloseStream_StopsViaSupervisor_AndEmitsStreamStopped()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToEncoder[1] = DefaultEncoder();

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);

        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        var started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        await viewer.SendAsync(new CloseStreamMessage(StreamId: started.StreamId), cancellation.Token);

        var stopped = await NextNonHeartbeatAsync<StreamStoppedMessage>(viewer, cancellation.Token);
        Assert.Equal(started.StreamId, stopped.StreamId);
        Assert.Equal(StreamStoppedReason.ClosedByViewer, stopped.Reason);
    }

    [Fact]
    public async Task PauseStream_SendsPauseToWorker()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToEncoder[1] = DefaultEncoder();

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        var started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        await viewer.SendAsync(new PauseStreamMessage(StreamId: started.StreamId), cancellation.Token);

        await PollUntilAsync(() => !harness.WorkerCommands.IsEmpty, cancellation.Token);
        Assert.True(harness.WorkerCommands.TryDequeue(out var entry));
        Assert.Equal(started.StreamId, entry.StreamId);
        Assert.Equal(WorkerCommandTag.Pause, entry.Tag);
    }

    [Fact]
    public async Task ResumeStream_SendsResumeToWorker()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToEncoder[1] = DefaultEncoder();

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        var started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        await viewer.SendAsync(new ResumeStreamMessage(StreamId: started.StreamId), cancellation.Token);

        await PollUntilAsync(() => !harness.WorkerCommands.IsEmpty, cancellation.Token);
        Assert.True(harness.WorkerCommands.TryDequeue(out var entry));
        Assert.Equal(started.StreamId, entry.StreamId);
        Assert.Equal(WorkerCommandTag.Resume, entry.Tag);
    }

    [Fact]
    public async Task RequestKeyframe_SendsRequestKeyframeToWorker()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToEncoder[1] = DefaultEncoder();

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        var started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        await viewer.SendAsync(new RequestKeyframeMessage(StreamId: started.StreamId), cancellation.Token);

        await PollUntilAsync(() => !harness.WorkerCommands.IsEmpty, cancellation.Token);
        Assert.True(harness.WorkerCommands.TryDequeue(out var entry));
        Assert.Equal(started.StreamId, entry.StreamId);
        Assert.Equal(WorkerCommandTag.RequestKeyframe, entry.Tag);
    }

    [Fact]
    public async Task FocusWindow_CallsFocusRelay()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[42] = 0x100;
        harness.WindowToEncoder[42] = DefaultEncoder();
        harness.ForegroundApi.Foreground = 0x999;

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 42), cancellation.Token);
        var started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        await viewer.SendAsync(new FocusWindowMessage(StreamId: started.StreamId), cancellation.Token);

        await PollUntilAsync(() => harness.ForegroundApi.SetForegroundCalls.Count > 0, cancellation.Token);
        Assert.Contains(0x100L, harness.ForegroundApi.SetForegroundCalls);
    }

    [Fact]
    public async Task FocusWindow_UnknownStreamId_IsIgnored()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        harness.Windows.Add(MakeWindow(1));

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new FocusWindowMessage(StreamId: 9999), cancellation.Token);

        // Round-trip a LIST_WINDOWS to confirm the server kept processing messages.
        await viewer.SendAsync(new ListWindowsMessage(), cancellation.Token);
        var snapshot = await NextNonHeartbeatAsync<WindowSnapshotMessage>(viewer, cancellation.Token);
        Assert.Single(snapshot.Windows);
        Assert.Empty(harness.ForegroundApi.SetForegroundCalls);
    }

    [Fact]
    public async Task FocusWindow_HwndResolvesNull_IsIgnored()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[42] = 0x100;
        harness.WindowToEncoder[42] = DefaultEncoder();

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 42), cancellation.Token);
        var started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        // Window disappeared between OPEN_STREAM and FOCUS_WINDOW.
        harness.WindowToHwnd.Remove(42);

        await viewer.SendAsync(new FocusWindowMessage(StreamId: started.StreamId), cancellation.Token);

        // Round-trip another message to confirm receive loop continues.
        await viewer.SendAsync(new ListWindowsMessage(), cancellation.Token);
        _ = await NextNonHeartbeatAsync<WindowSnapshotMessage>(viewer, cancellation.Token);
        Assert.Empty(harness.ForegroundApi.SetForegroundCalls);
    }

    [Fact]
    public async Task KeyEvent_RoutesToInjectionAction()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToEncoder[1] = DefaultEncoder();

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        var started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        var keyEvent = new KeyEventMessage(
            StreamId: started.StreamId, KeyCode: 0x41, IsUnicode: false, IsDown: true);
        await viewer.SendAsync(keyEvent, cancellation.Token);

        await PollUntilAsync(() => !harness.KeyInjections.IsEmpty, cancellation.Token);
        Assert.True(harness.KeyInjections.TryDequeue(out var entry));
        Assert.Equal(started.StreamId, entry.StreamId);
        Assert.Equal(0x41, entry.Message.KeyCode);
        Assert.False(entry.Message.IsUnicode);
        Assert.True(entry.Message.IsDown);
    }

    [Fact]
    public async Task MouseEvent_RoutesToInjectionAction()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToEncoder[1] = DefaultEncoder();

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        var started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        var mouseEvent = new MouseEventMessage(
            StreamId: started.StreamId, NormalizedX: 0.5f, NormalizedY: 0.25f,
            EventType: MouseEventType.ButtonDown, ButtonFlags: MouseButton.Left, ScrollDelta: 0);
        await viewer.SendAsync(mouseEvent, cancellation.Token);

        await PollUntilAsync(() => !harness.MouseInjections.IsEmpty, cancellation.Token);
        Assert.True(harness.MouseInjections.TryDequeue(out var entry));
        Assert.Equal(started.StreamId, entry.StreamId);
        Assert.Equal(0.5f, entry.Message.NormalizedX);
        Assert.Equal(0.25f, entry.Message.NormalizedY);
        Assert.Equal(MouseEventType.ButtonDown, entry.Message.EventType);
        Assert.Equal(MouseButton.Left, entry.Message.ButtonFlags);
    }

    [Fact]
    public async Task ViewerReady_RegistersUdpEndpoint()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();

        var remote = IPAddress.Parse("10.0.0.42");
        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token, remote);
        await viewer.SendAsync(new ViewerReadyMessage(ViewerUdpPort: 55555), cancellation.Token);

        await PollUntilAsync(() => harness.Server.ActiveViewerEndpoint is not null, cancellation.Token);
        var endpoint = harness.Server.ActiveViewerEndpoint!;
        Assert.Equal(remote, endpoint.Address);
        Assert.Equal(55555, endpoint.Port);
    }

    [Fact]
    public async Task ViewerReady_FiresViewerConnectedEvent_WithEndpoint()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();

        var remote = IPAddress.Parse("10.0.0.42");
        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token, remote);

        var eventCapture =
            new TaskCompletionSource<ViewerConnectedEventArguments>();
        harness.Server.ViewerConnected += (_, eventArguments) =>
            eventCapture.TrySetResult(eventArguments);

        await viewer.SendAsync(new ViewerReadyMessage(ViewerUdpPort: 55555), cancellation.Token);

        var captured =
            await eventCapture.Task.WaitAsync(cancellation.Token);
        Assert.Equal("10.0.0.42:55555", captured.Endpoint);
    }

    [Fact]
    public async Task ViewerDisconnect_FiresViewerDisconnectedEvent_WithEndpoint()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();

        var remote = IPAddress.Parse("10.0.0.42");
        var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token, remote);

        var connectedCapture =
            new TaskCompletionSource<ViewerConnectedEventArguments>();
        harness.Server.ViewerConnected += (_, eventArguments) =>
            connectedCapture.TrySetResult(eventArguments);

        var disconnectedCapture =
            new TaskCompletionSource<ViewerDisconnectedEventArguments>();
        harness.Server.ViewerDisconnected += (_, eventArguments) =>
            disconnectedCapture.TrySetResult(eventArguments);

        await viewer.SendAsync(new ViewerReadyMessage(ViewerUdpPort: 55555), cancellation.Token);
        _ = await connectedCapture.Task.WaitAsync(cancellation.Token);

        await viewer.DisposeAsync();

        var captured =
            await disconnectedCapture.Task.WaitAsync(cancellation.Token);
        Assert.Equal("10.0.0.42:55555", captured.Endpoint);
    }

    [Fact]
    public async Task ViewerReady_WithoutRemoteAddress_IsIgnored()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new ViewerReadyMessage(ViewerUdpPort: 55555), cancellation.Token);

        // Round-trip a follow-up message to confirm the loop stayed alive.
        await viewer.SendAsync(new ListWindowsMessage(), cancellation.Token);
        _ = await NextNonHeartbeatAsync<WindowSnapshotMessage>(viewer, cancellation.Token);
        Assert.Null(harness.Server.ActiveViewerEndpoint);
    }

    [Fact]
    public async Task WindowAppeared_PushesWindowAddedToActiveChannel()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        var descriptor = MakeWindow(99, 0x999);
        harness.Server.NotifyWindowAppeared(descriptor);

        var added = await NextNonHeartbeatAsync<WindowAddedMessage>(viewer, cancellation.Token);
        Assert.Equal(99ul, added.Window.WindowId);
        Assert.Equal(0x999, added.Window.Hwnd);
    }

    [Fact]
    public async Task WindowDisappeared_PushesWindowRemovedToActiveChannel()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        harness.Server.NotifyWindowDisappeared(99);

        var removed = await NextNonHeartbeatAsync<WindowRemovedMessage>(viewer, cancellation.Token);
        Assert.Equal(99ul, removed.WindowId);
    }

    [Fact]
    public async Task WindowChanged_PushesWindowUpdatedToActiveChannel()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        harness.Server.NotifyWindowChanged(7, "new title", 1280, 720);

        var updated = await NextNonHeartbeatAsync<WindowUpdatedMessage>(viewer, cancellation.Token);
        Assert.Equal(7ul, updated.WindowId);
        Assert.Equal("new title", updated.Title);
        Assert.Equal(1280, updated.PhysicalWidth);
        Assert.Equal(720, updated.PhysicalHeight);
    }

    [Fact]
    public async Task NotifyWindowMethods_NoOpWhenNoViewerConnected()
    {
        await using var harness = CoordinatorControlServerTestHarness.Start();
        // No viewer connected at all — these must not throw.
        harness.Server.NotifyWindowAppeared(MakeWindow(1));
        harness.Server.NotifyWindowDisappeared(2);
        harness.Server.NotifyWindowChanged(3, "t", 100, 200);
        await Task.Delay(20);
    }

    [Fact]
    public async Task NotifyStreamStalled_PushesStreamStalledToActiveChannel()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        harness.Server.NotifyStreamStalled(streamId: 7, cause: StallCause.SourceStalled);

        var stalled = await NextNonHeartbeatAsync<StreamStalledMessage>(viewer, cancellation.Token);
        Assert.Equal(7, stalled.StreamId);
        Assert.Equal(StallCause.SourceStalled, stalled.Cause);
    }

    [Fact]
    public async Task NotifyStreamResumed_PushesStreamResumedToActiveChannel()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        harness.Server.NotifyStreamResumed(streamId: 7);

        var resumed = await NextNonHeartbeatAsync<StreamResumedMessage>(viewer, cancellation.Token);
        Assert.Equal(7, resumed.StreamId);
    }

    [Fact]
    public async Task NotifyStreamMethods_NoOpWhenNoViewerConnected()
    {
        await using var harness = CoordinatorControlServerTestHarness.Start();
        // No viewer connected at all — these must not throw.
        harness.Server.NotifyStreamStalled(streamId: 1, cause: StallCause.WorkerSilent);
        harness.Server.NotifyStreamResumed(streamId: 1);
        await Task.Delay(20);
    }

    [Fact]
    public async Task StreamEnded_PushesStreamStoppedToActiveChannel_WhenWorkerExits()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToEncoder[1] = DefaultEncoder();

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        var started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        // Simulate worker process crashing with encoder failure.
        harness.Launcher.Launched.Single().SimulateEncoderFailure();

        var stopped = await NextNonHeartbeatAsync<StreamStoppedMessage>(viewer, cancellation.Token);
        Assert.Equal(started.StreamId, stopped.StreamId);
        Assert.Equal(StreamStoppedReason.EncoderFailed, stopped.Reason);
    }

    [Fact]
    public async Task StreamEnded_NoActiveChannel_IsNoOp()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToEncoder[1] = DefaultEncoder();

        // Open a stream as a viewer, then drop the viewer before the stream exits.
        var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        _ = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);
        await viewer.DisposeAsync();

        // Wait for the server to observe the disconnect.
        await PollUntilAsync(() => harness.Server.ActiveViewerEndpoint is null, cancellation.Token);
        await Task.Delay(50, cancellation.Token);

        // Now fire StreamEnded — must not throw, no channel to write to.
        harness.Launcher.Launched.Single().SimulateEncoderFailure();
        await Task.Delay(50, cancellation.Token);
    }

    [Fact]
    public async Task SecondViewer_GetsViewerBusy()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();

        await using var firstViewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);

        await using var secondViewer = harness.ConnectViewer();
        var response = await secondViewer.ReceiveAsync(cancellation.Token);
        var error = Assert.IsType<ErrorMessage>(response);
        Assert.Equal(ProtocolErrorCode.ViewerBusy, error.Code);

        // Second viewer's channel should be closed by the server.
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => secondViewer.ReceiveAsync(cancellation.Token));
    }

    [Fact]
    public async Task NonHelloFirstMessage_SendsMalformedMessageError()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start();

        await using var viewer = harness.ConnectViewer();
        await viewer.SendAsync(new ListWindowsMessage(), cancellation.Token);

        var response = await viewer.ReceiveAsync(cancellation.Token);
        var error = Assert.IsType<ErrorMessage>(response);
        Assert.Equal(ProtocolErrorCode.MalformedMessage, error.Code);
    }

    [Fact]
    public async Task Heartbeat_RoundTripsAndUpdatesLastReceived()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start(
            heartbeatIntervalMilliseconds: 50, heartbeatTimeoutMilliseconds: 10_000);

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);

        // Server emits heartbeats; receive at least one.
        var first = await viewer.ReceiveAsync<HeartbeatMessage>(cancellation.Token);
        Assert.NotNull(first);

        // Viewer responds with its own heartbeat — server should accept without error.
        await viewer.SendAsync(HeartbeatMessage.Instance, cancellation.Token);

        // Round-trip a list to confirm the receive loop continues post-heartbeat.
        await viewer.SendAsync(new ListWindowsMessage(), cancellation.Token);
        _ = await NextNonHeartbeatAsync<WindowSnapshotMessage>(viewer, cancellation.Token);
    }

    [Fact]
    public async Task HeartbeatTimeout_DisconnectsViewer()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using var harness = CoordinatorControlServerTestHarness.Start(
            heartbeatIntervalMilliseconds: 30, heartbeatTimeoutMilliseconds: 100);

        await using var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);

        // Intentional receive loop: exits only via the EndOfStreamException asserted above.
        // ReSharper disable once FunctionNeverReturns
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
        {
            while (true)
            {
                await viewer.ReceiveAsync(cancellation.Token);
            }
        });
    }

    [Fact]
    public async Task DisposeAsync_ClosesActiveChannelAndIsIdempotent()
    {
        using var cancellation = new CancellationTokenSource(DefaultTestTimeout);
        var harness = CoordinatorControlServerTestHarness.Start();
        var viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        try
        {
            await harness.DisposeAsync();
            // Second dispose must not throw.
            await harness.DisposeAsync();
        }
        finally
        {
            await viewer.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_WithNoActiveChannel_IsClean()
    {
        var harness = CoordinatorControlServerTestHarness.Start();
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task ArgumentNullExceptions_InConstructor()
    {
        var tcpAcceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        var supervisor = new WorkerSupervisor(
            new CoordinatorControlServerTestHarness.FakeWorkerLauncher(), 1);
        var focusRelay =
            new FocusRelay(new CoordinatorControlServerTestHarness.FakeForegroundApi());
        var options = new CoordinatorOptions(2000, 6000, 2, 4);
        var windows = () => Array.Empty<WindowDescriptor>();
        Func<ulong, long?> hwnd = _ => null;
        Func<ulong, EncoderOptions?> encoder = _ => null;
        var udpPort = () => 0;
        Func<int, WorkerCommandTag, Task> sendWorkerCommand = (_, _) => Task.CompletedTask;
        Action<int, KeyEventMessage> injectKey = (_, _) => { };
        Action<int, MouseEventMessage> injectMouse = (_, _) => { };

        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            null!, tcpAcceptor, supervisor, windows, hwnd, encoder, udpPort, sendWorkerCommand, focusRelay, injectKey, injectMouse, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, null!, supervisor, windows, hwnd, encoder, udpPort, sendWorkerCommand, focusRelay, injectKey, injectMouse, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, null!, windows, hwnd, encoder, udpPort, sendWorkerCommand, focusRelay, injectKey, injectMouse, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, null!, hwnd, encoder, udpPort, sendWorkerCommand, focusRelay, injectKey, injectMouse, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, windows, null!, encoder, udpPort, sendWorkerCommand, focusRelay, injectKey, injectMouse, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, windows, hwnd, null!, udpPort, sendWorkerCommand, focusRelay, injectKey, injectMouse, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, windows, hwnd, encoder, null!, sendWorkerCommand, focusRelay, injectKey, injectMouse, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, windows, hwnd, encoder, udpPort, null!, focusRelay, injectKey, injectMouse, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, windows, hwnd, encoder, udpPort, sendWorkerCommand, null!, injectKey, injectMouse, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, windows, hwnd, encoder, udpPort, sendWorkerCommand, focusRelay, null!, injectMouse, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, windows, hwnd, encoder, udpPort, sendWorkerCommand, focusRelay, injectKey, null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, windows, hwnd, encoder, udpPort, sendWorkerCommand, focusRelay, injectKey, injectMouse, null!));

        await tcpAcceptor.DisposeAsync();
        await supervisor.DisposeAsync();
    }

    [Fact]
    public async Task TcpPort_DelegatesToAcceptor()
    {
        await using var tcpAcceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        tcpAcceptor.StartListening(7777);
        await using var supervisor = new WorkerSupervisor(
            new CoordinatorControlServerTestHarness.FakeWorkerLauncher(), 1);
        var focusRelay =
            new FocusRelay(new CoordinatorControlServerTestHarness.FakeForegroundApi());
        var options = new CoordinatorOptions(2000, 6000, 2, 4);

        await using var server = new CoordinatorControlServer(
            options,
            tcpAcceptor,
            supervisor,
            () => Array.Empty<WindowDescriptor>(),
            _ => null,
            _ => null,
            () => 0,
            (_, _) => Task.CompletedTask,
            focusRelay,
            (_, _) => { },
            (_, _) => { },
            TimeProvider.System);

        Assert.Equal(7777, server.TcpPort);
    }
}
