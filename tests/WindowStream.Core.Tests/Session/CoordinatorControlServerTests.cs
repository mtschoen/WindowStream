using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;
using WindowStream.Core.Protocol;
using WindowStream.Core.Session;
using WindowStream.Core.Session.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Session;

public sealed class CoordinatorControlServerTests
{
    private static readonly TimeSpan DefaultTestTimeout = TimeSpan.FromSeconds(10);

    private static EncoderOptions DefaultEncoder(int widthPixels = 1280, int heightPixels = 720)
        => new EncoderOptions(widthPixels, heightPixels, 60, 8_000_000, 30, 2);

    private static WindowDescriptor MakeWindow(ulong windowId, long hwnd = 0x100, int widthPixels = 1280, int heightPixels = 720)
        => new WindowDescriptor(
            WindowId: windowId,
            Hwnd: hwnd,
            ProcessId: 4242,
            ProcessName: "demo.exe",
            Title: $"Window {windowId}",
            PhysicalWidth: widthPixels,
            PhysicalHeight: heightPixels);

    private static async Task<TMessage> NextNonHeartbeatAsync<TMessage>(
        FakeViewerEndpoint viewer, CancellationToken cancellationToken)
        where TMessage : ControlMessage
    {
        while (true)
        {
            ControlMessage message = await viewer.ReceiveAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task PollUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
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
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        harness.Windows.Add(MakeWindow(1));
        harness.Windows.Add(MakeWindow(2));
        harness.UdpPort = 64500;

        await using FakeViewerEndpoint viewer = harness.ConnectViewer();
        await viewer.SendAsync(
            new HelloMessage(2, new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellation.Token);

        ServerHelloMessage helloResponse = await viewer.ReceiveAsync<ServerHelloMessage>(cancellation.Token);
        Assert.Equal(2, helloResponse.ServerVersion);
        Assert.Equal(64500, helloResponse.UdpPort);
        Assert.Equal(2, helloResponse.Windows.Length);
        Assert.Equal(1ul, helloResponse.Windows[0].WindowId);
        Assert.Equal(2ul, helloResponse.Windows[1].WindowId);
    }

    [Fact]
    public async Task ListWindows_TriggersWindowSnapshot()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        harness.Windows.Add(MakeWindow(7));

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new ListWindowsMessage(), cancellation.Token);

        WindowSnapshotMessage snapshot = await NextNonHeartbeatAsync<WindowSnapshotMessage>(viewer, cancellation.Token);
        Assert.Single(snapshot.Windows);
        Assert.Equal(7ul, snapshot.Windows[0].WindowId);
    }

    [Fact]
    public async Task OpenStream_HappyPath_StartsAndEmitsStreamStarted()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[42] = 0xABCD;
        harness.WindowToEncoder[42] = DefaultEncoder(1920, 1080);

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 42), cancellation.Token);

        StreamStartedMessage started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);
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
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        // Note: nothing in WindowToHwnd for windowId=99.

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 99), cancellation.Token);

        ErrorMessage error = await NextNonHeartbeatAsync<ErrorMessage>(viewer, cancellation.Token);
        Assert.Equal(ProtocolErrorCode.WindowNotFound, error.Code);
        Assert.Empty(harness.Launcher.Launched);
    }

    [Fact]
    public async Task OpenStream_NoEncoderOptions_EmitsWindowNotFoundError()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[55] = 0x500;
        // No entry in WindowToEncoder.

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 55), cancellation.Token);

        ErrorMessage error = await NextNonHeartbeatAsync<ErrorMessage>(viewer, cancellation.Token);
        Assert.Equal(ProtocolErrorCode.WindowNotFound, error.Code);
    }

    [Fact]
    public async Task OpenStream_AtCapacity_EmitsEncoderCapacityError()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start(maximumConcurrentStreams: 1);
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToHwnd[2] = 0x200;
        harness.WindowToEncoder[1] = DefaultEncoder();
        harness.WindowToEncoder[2] = DefaultEncoder();

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);

        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        _ = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        await viewer.SendAsync(new OpenStreamMessage(WindowId: 2), cancellation.Token);
        ErrorMessage error = await NextNonHeartbeatAsync<ErrorMessage>(viewer, cancellation.Token);
        Assert.Equal(ProtocolErrorCode.EncoderCapacity, error.Code);
    }

    [Fact]
    public async Task CloseStream_StopsViaSupervisor_AndEmitsStreamStopped()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToEncoder[1] = DefaultEncoder();

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);

        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        StreamStartedMessage started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        await viewer.SendAsync(new CloseStreamMessage(StreamId: started.StreamId), cancellation.Token);

        StreamStoppedMessage stopped = await NextNonHeartbeatAsync<StreamStoppedMessage>(viewer, cancellation.Token);
        Assert.Equal(started.StreamId, stopped.StreamId);
        Assert.Equal(StreamStoppedReason.ClosedByViewer, stopped.Reason);
    }

    [Fact]
    public async Task PauseStream_SendsPauseToWorker()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToEncoder[1] = DefaultEncoder();

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        StreamStartedMessage started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        await viewer.SendAsync(new PauseStreamMessage(StreamId: started.StreamId), cancellation.Token);

        await PollUntilAsync(() => !harness.WorkerCommands.IsEmpty, cancellation.Token);
        Assert.True(harness.WorkerCommands.TryDequeue(out (int StreamId, WorkerCommandTag Tag) entry));
        Assert.Equal(started.StreamId, entry.StreamId);
        Assert.Equal(WorkerCommandTag.Pause, entry.Tag);
    }

    [Fact]
    public async Task ResumeStream_SendsResumeToWorker()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToEncoder[1] = DefaultEncoder();

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        StreamStartedMessage started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        await viewer.SendAsync(new ResumeStreamMessage(StreamId: started.StreamId), cancellation.Token);

        await PollUntilAsync(() => !harness.WorkerCommands.IsEmpty, cancellation.Token);
        Assert.True(harness.WorkerCommands.TryDequeue(out (int StreamId, WorkerCommandTag Tag) entry));
        Assert.Equal(started.StreamId, entry.StreamId);
        Assert.Equal(WorkerCommandTag.Resume, entry.Tag);
    }

    [Fact]
    public async Task RequestKeyframe_SendsRequestKeyframeToWorker()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToEncoder[1] = DefaultEncoder();

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        StreamStartedMessage started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        await viewer.SendAsync(new RequestKeyframeMessage(StreamId: started.StreamId), cancellation.Token);

        await PollUntilAsync(() => !harness.WorkerCommands.IsEmpty, cancellation.Token);
        Assert.True(harness.WorkerCommands.TryDequeue(out (int StreamId, WorkerCommandTag Tag) entry));
        Assert.Equal(started.StreamId, entry.StreamId);
        Assert.Equal(WorkerCommandTag.RequestKeyframe, entry.Tag);
    }

    [Fact]
    public async Task FocusWindow_CallsFocusRelay()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[42] = 0x100;
        harness.WindowToEncoder[42] = DefaultEncoder();
        harness.ForegroundApi.Foreground = 0x999;

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 42), cancellation.Token);
        StreamStartedMessage started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        await viewer.SendAsync(new FocusWindowMessage(StreamId: started.StreamId), cancellation.Token);

        await PollUntilAsync(() => harness.ForegroundApi.SetForegroundCalls.Count > 0, cancellation.Token);
        Assert.Contains(0x100L, harness.ForegroundApi.SetForegroundCalls);
    }

    [Fact]
    public async Task FocusWindow_UnknownStreamId_IsIgnored()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        harness.Windows.Add(MakeWindow(1));

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new FocusWindowMessage(StreamId: 9999), cancellation.Token);

        // Round-trip a LIST_WINDOWS to confirm the server kept processing messages.
        await viewer.SendAsync(new ListWindowsMessage(), cancellation.Token);
        WindowSnapshotMessage snapshot = await NextNonHeartbeatAsync<WindowSnapshotMessage>(viewer, cancellation.Token);
        Assert.Single(snapshot.Windows);
        Assert.Empty(harness.ForegroundApi.SetForegroundCalls);
    }

    [Fact]
    public async Task FocusWindow_HwndResolvesNull_IsIgnored()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[42] = 0x100;
        harness.WindowToEncoder[42] = DefaultEncoder();

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 42), cancellation.Token);
        StreamStartedMessage started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

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
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToEncoder[1] = DefaultEncoder();

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        StreamStartedMessage started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        KeyEventMessage keyEvent = new KeyEventMessage(
            StreamId: started.StreamId, KeyCode: 0x41, IsUnicode: false, IsDown: true);
        await viewer.SendAsync(keyEvent, cancellation.Token);

        await PollUntilAsync(() => !harness.KeyInjections.IsEmpty, cancellation.Token);
        Assert.True(harness.KeyInjections.TryDequeue(out (int StreamId, KeyEventMessage Message) entry));
        Assert.Equal(started.StreamId, entry.StreamId);
        Assert.Equal(0x41, entry.Message.KeyCode);
        Assert.False(entry.Message.IsUnicode);
        Assert.True(entry.Message.IsDown);
    }

    [Fact]
    public async Task ViewerReady_RegistersUdpEndpoint()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();

        IPAddress remote = IPAddress.Parse("10.0.0.42");
        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token, remote);
        await viewer.SendAsync(new ViewerReadyMessage(ViewerUdpPort: 55555), cancellation.Token);

        await PollUntilAsync(() => harness.Server.ActiveViewerEndpoint is not null, cancellation.Token);
        IPEndPoint endpoint = harness.Server.ActiveViewerEndpoint!;
        Assert.Equal(remote, endpoint.Address);
        Assert.Equal(55555, endpoint.Port);
    }

    [Fact]
    public async Task ViewerReady_WithoutRemoteAddress_IsIgnored()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new ViewerReadyMessage(ViewerUdpPort: 55555), cancellation.Token);

        // Round-trip a follow-up message to confirm the loop stayed alive.
        await viewer.SendAsync(new ListWindowsMessage(), cancellation.Token);
        _ = await NextNonHeartbeatAsync<WindowSnapshotMessage>(viewer, cancellation.Token);
        Assert.Null(harness.Server.ActiveViewerEndpoint);
    }

    [Fact]
    public async Task WindowAppeared_PushesWindowAddedToActiveChannel()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        WindowDescriptor descriptor = MakeWindow(99, 0x999);
        harness.Server.NotifyWindowAppeared(descriptor);

        WindowAddedMessage added = await NextNonHeartbeatAsync<WindowAddedMessage>(viewer, cancellation.Token);
        Assert.Equal(99ul, added.Window.WindowId);
        Assert.Equal(0x999, added.Window.Hwnd);
    }

    [Fact]
    public async Task WindowDisappeared_PushesWindowRemovedToActiveChannel()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        harness.Server.NotifyWindowDisappeared(99);

        WindowRemovedMessage removed = await NextNonHeartbeatAsync<WindowRemovedMessage>(viewer, cancellation.Token);
        Assert.Equal(99ul, removed.WindowId);
    }

    [Fact]
    public async Task WindowChanged_PushesWindowUpdatedToActiveChannel()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        harness.Server.NotifyWindowChanged(7, "new title", 1280, 720);

        WindowUpdatedMessage updated = await NextNonHeartbeatAsync<WindowUpdatedMessage>(viewer, cancellation.Token);
        Assert.Equal(7ul, updated.WindowId);
        Assert.Equal("new title", updated.Title);
        Assert.Equal(1280, updated.PhysicalWidth);
        Assert.Equal(720, updated.PhysicalHeight);
    }

    [Fact]
    public async Task NotifyWindowMethods_NoOpWhenNoViewerConnected()
    {
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        // No viewer connected at all — these must not throw.
        harness.Server.NotifyWindowAppeared(MakeWindow(1));
        harness.Server.NotifyWindowDisappeared(2);
        harness.Server.NotifyWindowChanged(3, "t", 100, 200);
        await Task.Delay(20);
    }

    [Fact]
    public async Task StreamEnded_PushesStreamStoppedToActiveChannel_WhenWorkerExits()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToEncoder[1] = DefaultEncoder();

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        StreamStartedMessage started = await NextNonHeartbeatAsync<StreamStartedMessage>(viewer, cancellation.Token);

        // Simulate worker process crashing with encoder failure.
        harness.Launcher.Launched.Single().SimulateEncoderFailure();

        StreamStoppedMessage stopped = await NextNonHeartbeatAsync<StreamStoppedMessage>(viewer, cancellation.Token);
        Assert.Equal(started.StreamId, stopped.StreamId);
        Assert.Equal(StreamStoppedReason.EncoderFailed, stopped.Reason);
    }

    [Fact]
    public async Task StreamEnded_NoActiveChannel_IsNoOp()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        harness.WindowToHwnd[1] = 0x100;
        harness.WindowToEncoder[1] = DefaultEncoder();

        // Open a stream as a viewer, then drop the viewer before the stream exits.
        FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
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
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();

        await using FakeViewerEndpoint firstViewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);

        await using FakeViewerEndpoint secondViewer = harness.ConnectViewer();
        ControlMessage response = await secondViewer.ReceiveAsync(cancellation.Token);
        ErrorMessage error = Assert.IsType<ErrorMessage>(response);
        Assert.Equal(ProtocolErrorCode.ViewerBusy, error.Code);

        // Second viewer's channel should be closed by the server.
        await Assert.ThrowsAsync<System.IO.EndOfStreamException>(
            () => secondViewer.ReceiveAsync(cancellation.Token));
    }

    [Fact]
    public async Task NonHelloFirstMessage_SendsMalformedMessageError()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();

        await using FakeViewerEndpoint viewer = harness.ConnectViewer();
        await viewer.SendAsync(new ListWindowsMessage(), cancellation.Token);

        ControlMessage response = await viewer.ReceiveAsync(cancellation.Token);
        ErrorMessage error = Assert.IsType<ErrorMessage>(response);
        Assert.Equal(ProtocolErrorCode.MalformedMessage, error.Code);
    }

    [Fact]
    public async Task Heartbeat_RoundTripsAndUpdatesLastReceived()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start(
            heartbeatIntervalMilliseconds: 50, heartbeatTimeoutMilliseconds: 10_000);

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);

        // Server emits heartbeats; receive at least one.
        HeartbeatMessage first = await viewer.ReceiveAsync<HeartbeatMessage>(cancellation.Token);
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
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start(
            heartbeatIntervalMilliseconds: 30, heartbeatTimeoutMilliseconds: 100);

        await using FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);

        await Assert.ThrowsAsync<System.IO.EndOfStreamException>(async () =>
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
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        FakeViewerEndpoint viewer = await harness.ConnectAndHandshakeAsync(cancellation.Token);
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
        CoordinatorControlServerTestHarness harness = CoordinatorControlServerTestHarness.Start();
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task ArgumentNullExceptions_InConstructor()
    {
        FakeTcpConnectionAcceptor tcpAcceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        WorkerSupervisor supervisor = new WorkerSupervisor(
            new CoordinatorControlServerTestHarness.FakeWorkerLauncher(), 1);
        WindowStream.Core.Session.Input.FocusRelay focusRelay =
            new WindowStream.Core.Session.Input.FocusRelay(new CoordinatorControlServerTestHarness.FakeForegroundApi());
        CoordinatorOptions options = new CoordinatorOptions(2000, 6000, 2, 4);
        Func<WindowDescriptor[]> windows = () => Array.Empty<WindowDescriptor>();
        Func<ulong, long?> hwnd = _ => null;
        Func<ulong, EncoderOptions?> encoder = _ => null;
        Func<int> udpPort = () => 0;
        Func<int, WorkerCommandTag, Task> sendWorkerCommand = (_, _) => Task.CompletedTask;
        Action<int, KeyEventMessage> injectKey = (_, _) => { };

        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            null!, tcpAcceptor, supervisor, windows, hwnd, encoder, udpPort, sendWorkerCommand, focusRelay, injectKey, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, null!, supervisor, windows, hwnd, encoder, udpPort, sendWorkerCommand, focusRelay, injectKey, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, null!, windows, hwnd, encoder, udpPort, sendWorkerCommand, focusRelay, injectKey, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, null!, hwnd, encoder, udpPort, sendWorkerCommand, focusRelay, injectKey, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, windows, null!, encoder, udpPort, sendWorkerCommand, focusRelay, injectKey, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, windows, hwnd, null!, udpPort, sendWorkerCommand, focusRelay, injectKey, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, windows, hwnd, encoder, null!, sendWorkerCommand, focusRelay, injectKey, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, windows, hwnd, encoder, udpPort, null!, focusRelay, injectKey, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, windows, hwnd, encoder, udpPort, sendWorkerCommand, null!, injectKey, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, windows, hwnd, encoder, udpPort, sendWorkerCommand, focusRelay, null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new CoordinatorControlServer(
            options, tcpAcceptor, supervisor, windows, hwnd, encoder, udpPort, sendWorkerCommand, focusRelay, injectKey, null!));

        await tcpAcceptor.DisposeAsync();
        await supervisor.DisposeAsync();
    }

    [Fact]
    public void TcpPort_DelegatesToAcceptor()
    {
        FakeTcpConnectionAcceptor tcpAcceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        tcpAcceptor.StartListening(7777);
        WorkerSupervisor supervisor = new WorkerSupervisor(
            new CoordinatorControlServerTestHarness.FakeWorkerLauncher(), 1);
        WindowStream.Core.Session.Input.FocusRelay focusRelay =
            new WindowStream.Core.Session.Input.FocusRelay(new CoordinatorControlServerTestHarness.FakeForegroundApi());
        CoordinatorOptions options = new CoordinatorOptions(2000, 6000, 2, 4);

        CoordinatorControlServer server = new CoordinatorControlServer(
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
            TimeProvider.System);

        Assert.Equal(7777, server.TcpPort);
    }
}
