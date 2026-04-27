using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;
using WindowStream.Core.Protocol;

namespace WindowStream.Core.Session;

/// <summary>
/// v2 coordinator control server. One viewer at a time multiplexes any number
/// of streams (subject to <see cref="CoordinatorOptions.MaximumConcurrentStreams"/>)
/// over a single TCP connection. The coordinator delegates encoder lifecycles to
/// the supplied <see cref="WorkerSupervisor"/>, focus to <see cref="Input.FocusRelay"/>,
/// and key injection to the caller-supplied action.
/// </summary>
public sealed class CoordinatorControlServer : IAsyncDisposable
{
    private readonly CoordinatorOptions options;
    private readonly ITcpConnectionAcceptor tcpAcceptor;
    private readonly WorkerSupervisor supervisor;
    private readonly Func<WindowDescriptor[]> getCurrentWindows;
    private readonly Func<ulong, long?> resolveWindowIdToHwnd;
    private readonly Func<ulong, EncoderOptions?> resolveWindowIdToEncoderOptions;
    private readonly Func<int> getUdpPort;
    private readonly Func<int, WorkerCommandTag, Task> sendWorkerCommand;
    private readonly Input.FocusRelay focusRelay;
    private readonly Action<int, KeyEventMessage> injectKeyForStream;
    private readonly TimeProvider timeProvider;

    private readonly object stateLock = new object();
    private readonly Dictionary<int, ulong> streamIdToWindowId = new Dictionary<int, ulong>();
    private CancellationTokenSource? lifecycleCancellation;
    private IControlChannel? activeChannel;
    private IPEndPoint? activeViewerEndpoint;
    private bool disposed;

    public int TcpPort => tcpAcceptor.LocalPort;

    public IPEndPoint? ActiveViewerEndpoint
    {
        get
        {
            lock (stateLock)
            {
                return activeViewerEndpoint;
            }
        }
    }

    public CoordinatorControlServer(
        CoordinatorOptions options,
        ITcpConnectionAcceptor tcpAcceptor,
        WorkerSupervisor supervisor,
        Func<WindowDescriptor[]> getCurrentWindows,
        Func<ulong, long?> resolveWindowIdToHwnd,
        Func<ulong, EncoderOptions?> resolveWindowIdToEncoderOptions,
        Func<int> getUdpPort,
        Func<int, WorkerCommandTag, Task> sendWorkerCommand,
        Input.FocusRelay focusRelay,
        Action<int, KeyEventMessage> injectKeyForStream,
        TimeProvider timeProvider)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.tcpAcceptor = tcpAcceptor ?? throw new ArgumentNullException(nameof(tcpAcceptor));
        this.supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        this.getCurrentWindows = getCurrentWindows ?? throw new ArgumentNullException(nameof(getCurrentWindows));
        this.resolveWindowIdToHwnd = resolveWindowIdToHwnd ?? throw new ArgumentNullException(nameof(resolveWindowIdToHwnd));
        this.resolveWindowIdToEncoderOptions = resolveWindowIdToEncoderOptions ?? throw new ArgumentNullException(nameof(resolveWindowIdToEncoderOptions));
        this.getUdpPort = getUdpPort ?? throw new ArgumentNullException(nameof(getUdpPort));
        this.sendWorkerCommand = sendWorkerCommand ?? throw new ArgumentNullException(nameof(sendWorkerCommand));
        this.focusRelay = focusRelay ?? throw new ArgumentNullException(nameof(focusRelay));
        this.injectKeyForStream = injectKeyForStream ?? throw new ArgumentNullException(nameof(injectKeyForStream));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        this.supervisor.StreamEnded += OnStreamEnded;
    }

    public Task RunAsync(int requestedTcpPort, CancellationToken cancellationToken)
    {
        lifecycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        tcpAcceptor.StartListening(requestedTcpPort);
        return RunAcceptLoopAsync(lifecycleCancellation.Token);
    }

    private async Task RunAcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IControlChannel channel = await tcpAcceptor.AcceptAsync(cancellationToken).ConfigureAwait(false);

                bool busy;
                lock (stateLock)
                {
                    busy = activeChannel is not null;
                    if (!busy)
                    {
                        activeChannel = channel;
                    }
                }

                if (busy)
                {
                    await SendViewerBusyAndCloseAsync(channel, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _ = ServeViewerAsync(channel, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Lifecycle cancelled — normal shutdown.
        }
    }

    private static async Task SendViewerBusyAndCloseAsync(IControlChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource shortTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shortTimeout.Token);
            await channel.SendAsync(
                new ErrorMessage(ProtocolErrorCode.ViewerBusy, "a viewer is already connected"),
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best effort.
        }
        finally
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ServeViewerAsync(IControlChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            ControlMessage firstMessage = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (firstMessage is not HelloMessage)
            {
                await channel.SendAsync(
                    new ErrorMessage(ProtocolErrorCode.MalformedMessage, "expected HELLO"),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await channel.SendAsync(
                new ServerHelloMessage(
                    ServerVersion: options.ServerVersion,
                    UdpPort: getUdpPort(),
                    Windows: getCurrentWindows()),
                cancellationToken).ConfigureAwait(false);

            using CancellationTokenSource viewerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task heartbeatTask = RunHeartbeatAsync(channel, viewerCancellation.Token);

            try
            {
                await RunViewerReceiveLoopAsync(channel, viewerCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                viewerCancellation.Cancel();
                try { await heartbeatTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException)
        {
            // Viewer or lifecycle cancelled.
        }
        catch (System.IO.EndOfStreamException)
        {
            // Viewer disconnected.
        }
        finally
        {
            lock (stateLock)
            {
                activeViewerEndpoint = null;
                activeChannel = null;
            }
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunViewerReceiveLoopAsync(IControlChannel channel, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ControlMessage message = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            switch (message)
            {
                case ListWindowsMessage:
                    await channel.SendAsync(
                        new WindowSnapshotMessage(getCurrentWindows()),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case ViewerReadyMessage viewerReady:
                    IPAddress? viewerAddress = channel.RemoteIpAddress;
                    if (viewerAddress is not null)
                    {
                        lock (stateLock)
                        {
                            activeViewerEndpoint = new IPEndPoint(viewerAddress, viewerReady.ViewerUdpPort);
                        }
                    }
                    break;
                case OpenStreamMessage openStream:
                    await HandleOpenStreamAsync(channel, openStream, cancellationToken).ConfigureAwait(false);
                    break;
                case CloseStreamMessage closeStream:
                    await supervisor.StopStreamAsync(closeStream.StreamId).ConfigureAwait(false);
                    break;
                case PauseStreamMessage pauseStream:
                    await sendWorkerCommand(pauseStream.StreamId, WorkerCommandTag.Pause).ConfigureAwait(false);
                    break;
                case ResumeStreamMessage resumeStream:
                    await sendWorkerCommand(resumeStream.StreamId, WorkerCommandTag.Resume).ConfigureAwait(false);
                    break;
                case RequestKeyframeMessage requestKeyframe:
                    await sendWorkerCommand(requestKeyframe.StreamId, WorkerCommandTag.RequestKeyframe).ConfigureAwait(false);
                    break;
                case FocusWindowMessage focusWindow:
                    HandleFocusWindow(focusWindow);
                    break;
                case KeyEventMessage keyEvent:
                    injectKeyForStream(keyEvent.StreamId, keyEvent);
                    break;
                case HeartbeatMessage:
                    channel.NotifyHeartbeatReceived();
                    break;
            }
        }
    }

    private async Task HandleOpenStreamAsync(
        IControlChannel channel,
        OpenStreamMessage openStream,
        CancellationToken cancellationToken)
    {
        long? hwnd = resolveWindowIdToHwnd(openStream.WindowId);
        if (hwnd is null)
        {
            await channel.SendAsync(
                new ErrorMessage(
                    ProtocolErrorCode.WindowNotFound,
                    $"window {openStream.WindowId} not found"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        EncoderOptions? encoderOptions = resolveWindowIdToEncoderOptions(openStream.WindowId);
        if (encoderOptions is null)
        {
            await channel.SendAsync(
                new ErrorMessage(
                    ProtocolErrorCode.WindowNotFound,
                    $"window {openStream.WindowId} encoder options unavailable"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        StreamHandle handle;
        try
        {
            handle = await supervisor.StartStreamAsync(
                openStream.WindowId, hwnd.Value, encoderOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (EncoderCapacityException exception)
        {
            await channel.SendAsync(
                new ErrorMessage(ProtocolErrorCode.EncoderCapacity, exception.Message),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        lock (stateLock)
        {
            streamIdToWindowId[handle.StreamId] = openStream.WindowId;
        }

        await channel.SendAsync(
            new StreamStartedMessage(
                StreamId: handle.StreamId,
                WindowId: openStream.WindowId,
                Codec: "h264",
                Width: encoderOptions.widthPixels,
                Height: encoderOptions.heightPixels,
                FramesPerSecond: encoderOptions.framesPerSecond),
            cancellationToken).ConfigureAwait(false);
    }

    private void HandleFocusWindow(FocusWindowMessage focusWindow)
    {
        ulong windowId;
        lock (stateLock)
        {
            if (!streamIdToWindowId.TryGetValue(focusWindow.StreamId, out windowId))
            {
                return;
            }
        }

        long? hwnd = resolveWindowIdToHwnd(windowId);
        if (hwnd is null)
        {
            return;
        }

        focusRelay.BringToForeground(hwnd.Value);
    }

    private async Task RunHeartbeatAsync(IControlChannel channel, CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromMilliseconds(options.HeartbeatIntervalMilliseconds);
        TimeSpan timeout = TimeSpan.FromMilliseconds(options.HeartbeatTimeoutMilliseconds);
        using PeriodicTimer timer = new PeriodicTimer(interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await channel.SendAsync(HeartbeatMessage.Instance, cancellationToken).ConfigureAwait(false);
                if (timeProvider.GetUtcNow() - channel.LastHeartbeatReceived > timeout)
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Heartbeat loop cancelled.
        }
    }

    private void OnStreamEnded(object? sender, StreamEndedEventArguments eventArguments)
    {
        IControlChannel? channel;
        lock (stateLock)
        {
            streamIdToWindowId.Remove(eventArguments.StreamId);
            channel = activeChannel;
        }
        if (channel is null)
        {
            return;
        }
        _ = SendOnActiveChannelAsync(
            channel,
            new StreamStoppedMessage(eventArguments.StreamId, eventArguments.Reason));
    }

    public void NotifyWindowAppeared(WindowDescriptor window)
    {
        IControlChannel? channel = SnapshotActiveChannel();
        if (channel is null)
        {
            return;
        }
        _ = SendOnActiveChannelAsync(channel, new WindowAddedMessage(window));
    }

    public void NotifyWindowDisappeared(ulong windowId)
    {
        IControlChannel? channel = SnapshotActiveChannel();
        if (channel is null)
        {
            return;
        }
        _ = SendOnActiveChannelAsync(channel, new WindowRemovedMessage(windowId));
    }

    public void NotifyWindowChanged(ulong windowId, string? newTitle, int? newWidthPixels, int? newHeightPixels)
    {
        IControlChannel? channel = SnapshotActiveChannel();
        if (channel is null)
        {
            return;
        }
        _ = SendOnActiveChannelAsync(
            channel,
            new WindowUpdatedMessage(windowId, newTitle, newWidthPixels, newHeightPixels));
    }

    private IControlChannel? SnapshotActiveChannel()
    {
        lock (stateLock)
        {
            return activeChannel;
        }
    }

    private async Task SendOnActiveChannelAsync(IControlChannel channel, ControlMessage message)
    {
        CancellationToken token = lifecycleCancellation?.Token ?? CancellationToken.None;
        try
        {
            await channel.SendAsync(message, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown raced the send.
        }
        catch (System.IO.EndOfStreamException)
        {
            // Viewer disconnected mid-send.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;

        supervisor.StreamEnded -= OnStreamEnded;

        lifecycleCancellation?.Cancel();

        IControlChannel? channelToClose;
        lock (stateLock)
        {
            channelToClose = activeChannel;
            activeChannel = null;
            activeViewerEndpoint = null;
        }
        if (channelToClose is not null)
        {
            await channelToClose.DisposeAsync().ConfigureAwait(false);
        }

        await tcpAcceptor.DisposeAsync().ConfigureAwait(false);
        lifecycleCancellation?.Dispose();
    }
}
