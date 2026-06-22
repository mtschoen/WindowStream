using System.Net;
using WindowStream.Core.Capture.Detection;
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;
using WindowStream.Core.Protocol;
using WindowStream.Core.Session.Input;

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
    readonly CoordinatorOptions _options;
    readonly ITcpConnectionAcceptor _tcpAcceptor;
    readonly WorkerSupervisor _supervisor;
    readonly Func<WindowDescriptor[]> _getCurrentWindows;
    readonly Func<ulong, long?> _resolveWindowIdToHwnd;
    readonly Func<ulong, EncoderOptions?> _resolveWindowIdToEncoderOptions;
    readonly Func<int> _getUdpPort;
    readonly Func<int, WorkerCommandTag, Task> _sendWorkerCommand;
    readonly FocusRelay _focusRelay;
    readonly Action<int, KeyEventMessage> _injectKeyForStream;
    readonly TimeProvider _timeProvider;

    readonly object _stateLock = new object();
    readonly Dictionary<int, ulong> _streamIdToWindowId = new Dictionary<int, ulong>();
    CancellationTokenSource? _lifecycleCancellation;
    IControlChannel? _activeChannel;
    IPEndPoint? _activeViewerEndpoint;
    bool _disposed;

    public event EventHandler<ViewerConnectedEventArguments>? ViewerConnected;

    public event EventHandler<ViewerDisconnectedEventArguments>? ViewerDisconnected;

    public int TcpPort => _tcpAcceptor.LocalPort;

    public IPEndPoint? ActiveViewerEndpoint
    {
        get
        {
            lock (_stateLock)
            {
                return _activeViewerEndpoint;
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
        FocusRelay focusRelay,
        Action<int, KeyEventMessage> injectKeyForStream,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tcpAcceptor = tcpAcceptor ?? throw new ArgumentNullException(nameof(tcpAcceptor));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _getCurrentWindows = getCurrentWindows ?? throw new ArgumentNullException(nameof(getCurrentWindows));
        _resolveWindowIdToHwnd = resolveWindowIdToHwnd ?? throw new ArgumentNullException(nameof(resolveWindowIdToHwnd));
        _resolveWindowIdToEncoderOptions = resolveWindowIdToEncoderOptions ?? throw new ArgumentNullException(nameof(resolveWindowIdToEncoderOptions));
        _getUdpPort = getUdpPort ?? throw new ArgumentNullException(nameof(getUdpPort));
        _sendWorkerCommand = sendWorkerCommand ?? throw new ArgumentNullException(nameof(sendWorkerCommand));
        _focusRelay = focusRelay ?? throw new ArgumentNullException(nameof(focusRelay));
        _injectKeyForStream = injectKeyForStream ?? throw new ArgumentNullException(nameof(injectKeyForStream));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _supervisor.StreamEnded += OnStreamEnded;
    }

    public Task RunAsync(int requestedTcpPort, CancellationToken cancellationToken)
    {
        _lifecycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _tcpAcceptor.StartListening(requestedTcpPort);
        return RunAcceptLoopAsync(_lifecycleCancellation.Token);
    }

    async Task RunAcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var channel = await _tcpAcceptor.AcceptAsync(cancellationToken).ConfigureAwait(false);

                bool busy;
                lock (_stateLock)
                {
                    busy = _activeChannel is not null;
                    if (!busy)
                    {
                        _activeChannel = channel;
                    }
                }

                if (busy)
                {
                    await SendViewerBusyAndCloseAsync(channel, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _ = ServeViewerAsync(channel, cancellationToken).ContinueWith(faulted =>
                {
                    if (faulted.Exception is not null)
                    {
                        Console.Error.WriteLine($"[serve] ServeViewerAsync faulted: {faulted.Exception}");
                    }
                }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
            // Lifecycle cancelled — normal shutdown.
        }
    }

    static async Task SendViewerBusyAndCloseAsync(IControlChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            using var shortTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shortTimeout.Token);
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

    async Task ServeViewerAsync(IControlChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            var firstMessage = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (firstMessage is not HelloMessage)
            {
                await channel.SendAsync(
                    new ErrorMessage(ProtocolErrorCode.MalformedMessage, "expected HELLO"),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await channel.SendAsync(
                new ServerHelloMessage(
                    ServerVersion: _options.ServerVersion,
                    UdpPort: _getUdpPort(),
                    Windows: _getCurrentWindows()),
                cancellationToken).ConfigureAwait(false);

            using var viewerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeatTask = RunHeartbeatAsync(channel, viewerCancellation.Token);

            try
            {
                await RunViewerReceiveLoopAsync(channel, viewerCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                await viewerCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await heartbeatTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Normal shutdown - heartbeat is cancelled when viewer disconnects.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Viewer or lifecycle cancelled.
        }
        catch (EndOfStreamException)
        {
            // Viewer disconnected.
        }
        finally
        {
            IPEndPoint? disconnectedEndpoint;
            lock (_stateLock)
            {
                disconnectedEndpoint = _activeViewerEndpoint;
                _activeViewerEndpoint = null;
                _activeChannel = null;
            }
            if (disconnectedEndpoint is not null)
            {
                ViewerDisconnected?.Invoke(this, new ViewerDisconnectedEventArguments(disconnectedEndpoint.ToString()));
            }
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    async Task RunViewerReceiveLoopAsync(IControlChannel channel, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            switch (message)
            {
                case ListWindowsMessage:
                    await channel.SendAsync(
                        new WindowSnapshotMessage(_getCurrentWindows()),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case ViewerReadyMessage viewerReady:
                    var viewerAddress = channel.RemoteIpAddress;
                    if (viewerAddress is not null)
                    {
                        var endpoint = new IPEndPoint(viewerAddress, viewerReady.ViewerUdpPort);
                        lock (_stateLock)
                        {
                            _activeViewerEndpoint = endpoint;
                        }
                        ViewerConnected?.Invoke(this, new ViewerConnectedEventArguments(endpoint.ToString()));
                    }
                    break;
                case OpenStreamMessage openStream:
                    await HandleOpenStreamAsync(channel, openStream, cancellationToken).ConfigureAwait(false);
                    break;
                case CloseStreamMessage closeStream:
                    await _supervisor.StopStreamAsync(closeStream.StreamId).ConfigureAwait(false);
                    break;
                case PauseStreamMessage pauseStream:
                    await _sendWorkerCommand(pauseStream.StreamId, WorkerCommandTag.Pause).ConfigureAwait(false);
                    break;
                case ResumeStreamMessage resumeStream:
                    await _sendWorkerCommand(resumeStream.StreamId, WorkerCommandTag.Resume).ConfigureAwait(false);
                    break;
                case RequestKeyframeMessage requestKeyframe:
                    await _sendWorkerCommand(requestKeyframe.StreamId, WorkerCommandTag.RequestKeyframe).ConfigureAwait(false);
                    break;
                case FocusWindowMessage focusWindow:
                    HandleFocusWindow(focusWindow);
                    break;
                case KeyEventMessage keyEvent:
                    _injectKeyForStream(keyEvent.StreamId, keyEvent);
                    break;
                case HeartbeatMessage:
                    channel.NotifyHeartbeatReceived();
                    break;
            }
        }
    }

    async Task HandleOpenStreamAsync(
        IControlChannel channel,
        OpenStreamMessage openStream,
        CancellationToken cancellationToken)
    {
        await Console.Error.WriteLineAsync($"[openstream] handling windowId={openStream.WindowId}").ConfigureAwait(false);
        var hwnd = _resolveWindowIdToHwnd(openStream.WindowId);
        if (hwnd is null)
        {
            await Console.Error.WriteLineAsync($"[openstream] hwnd resolution returned null for windowId={openStream.WindowId}").ConfigureAwait(false);
            await channel.SendAsync(
                new ErrorMessage(
                    ProtocolErrorCode.WindowNotFound,
                    $"window {openStream.WindowId} not found"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await Console.Error.WriteLineAsync($"[openstream] resolved windowId={openStream.WindowId} -> hwnd={hwnd.Value}; resolving encoder options").ConfigureAwait(false);
        var encoderOptions = _resolveWindowIdToEncoderOptions(openStream.WindowId);
        if (encoderOptions is null)
        {
            await Console.Error.WriteLineAsync($"[openstream] encoder options NULL for windowId={openStream.WindowId} hwnd={hwnd.Value}").ConfigureAwait(false);
            await channel.SendAsync(
                new ErrorMessage(
                    ProtocolErrorCode.WindowNotFound,
                    $"window {openStream.WindowId} encoder options unavailable"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await Console.Error.WriteLineAsync($"[openstream] encoder options resolved; calling supervisor.StartStreamAsync (hwnd={hwnd.Value})").ConfigureAwait(false);
        StreamHandle handle;
        try
        {
            handle = await _supervisor.StartStreamAsync(
                openStream.WindowId, hwnd.Value, encoderOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (EncoderCapacityException exception)
        {
            await Console.Error.WriteLineAsync($"[openstream] encoder capacity exception: {exception.Message}").ConfigureAwait(false);
            await channel.SendAsync(
                new ErrorMessage(ProtocolErrorCode.EncoderCapacity, exception.Message),
                cancellationToken).ConfigureAwait(false);
            return;
        }
        await Console.Error.WriteLineAsync($"[openstream] supervisor returned handle; sending StreamStarted (streamId={handle.StreamId})").ConfigureAwait(false);

        lock (_stateLock)
        {
            _streamIdToWindowId[handle.StreamId] = openStream.WindowId;
        }

        await channel.SendAsync(
            new StreamStartedMessage(
                StreamId: handle.StreamId,
                WindowId: openStream.WindowId,
                Codec: "h264",
                Width: encoderOptions.WidthPixels,
                Height: encoderOptions.HeightPixels,
                FramesPerSecond: encoderOptions.FramesPerSecond),
            cancellationToken).ConfigureAwait(false);
    }

    void HandleFocusWindow(FocusWindowMessage focusWindow)
    {
        ulong windowId;
        lock (_stateLock)
        {
            if (!_streamIdToWindowId.TryGetValue(focusWindow.StreamId, out windowId))
            {
                return;
            }
        }

        var hwnd = _resolveWindowIdToHwnd(windowId);
        if (hwnd is null)
        {
            return;
        }

        _focusRelay.BringToForeground(hwnd.Value);
    }

    async Task RunHeartbeatAsync(IControlChannel channel, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(_options.HeartbeatIntervalMilliseconds);
        var timeout = TimeSpan.FromMilliseconds(_options.HeartbeatTimeoutMilliseconds);
        using var timer = new PeriodicTimer(interval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await channel.SendAsync(HeartbeatMessage.Instance, cancellationToken).ConfigureAwait(false);
                if (_timeProvider.GetUtcNow() - channel.LastHeartbeatReceived > timeout)
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

    void OnStreamEnded(object? sender, StreamEndedEventArguments eventArguments)
    {
        IControlChannel? channel;
        lock (_stateLock)
        {
            _streamIdToWindowId.Remove(eventArguments.StreamId);
            channel = _activeChannel;
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
        var channel = SnapshotActiveChannel();
        if (channel is null)
        {
            return;
        }
        _ = SendOnActiveChannelAsync(channel, new WindowAddedMessage(window));
    }

    public void NotifyWindowDisappeared(ulong windowId)
    {
        var channel = SnapshotActiveChannel();
        if (channel is null)
        {
            return;
        }
        _ = SendOnActiveChannelAsync(channel, new WindowRemovedMessage(windowId));
    }

    public void NotifyWindowChanged(ulong windowId, string? newTitle, int? newWidthPixels, int? newHeightPixels)
    {
        var channel = SnapshotActiveChannel();
        if (channel is null)
        {
            return;
        }
        _ = SendOnActiveChannelAsync(
            channel,
            new WindowUpdatedMessage(windowId, newTitle, newWidthPixels, newHeightPixels));
    }

    public void NotifyStreamStalled(int streamId, StallCause cause)
    {
        var channel = SnapshotActiveChannel();
        if (channel is null)
        {
            return;
        }
        _ = SendOnActiveChannelAsync(channel, new StreamStalledMessage(streamId, cause));
    }

    public void NotifyStreamResumed(int streamId)
    {
        var channel = SnapshotActiveChannel();
        if (channel is null)
        {
            return;
        }
        _ = SendOnActiveChannelAsync(channel, new StreamResumedMessage(streamId));
    }

    IControlChannel? SnapshotActiveChannel()
    {
        lock (_stateLock)
        {
            return _activeChannel;
        }
    }

    async Task SendOnActiveChannelAsync(IControlChannel channel, ControlMessage message)
    {
        var token = _lifecycleCancellation?.Token ?? CancellationToken.None;
        try
        {
            await channel.SendAsync(message, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown raced the send.
        }
        catch (EndOfStreamException)
        {
            // Viewer disconnected mid-send.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _supervisor.StreamEnded -= OnStreamEnded;

        if (_lifecycleCancellation is not null) await _lifecycleCancellation.CancelAsync().ConfigureAwait(false);

        IControlChannel? channelToClose;
        lock (_stateLock)
        {
            channelToClose = _activeChannel;
            _activeChannel = null;
            _activeViewerEndpoint = null;
        }
        if (channelToClose is not null)
        {
            await channelToClose.DisposeAsync().ConfigureAwait(false);
        }

        await _tcpAcceptor.DisposeAsync().ConfigureAwait(false);
        _lifecycleCancellation?.Dispose();
    }
}
