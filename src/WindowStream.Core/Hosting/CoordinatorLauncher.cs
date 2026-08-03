#if WINDOWS
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading.Channels;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Discovery;
using WindowStream.Core.Encode;
using WindowStream.Core.Observability;
using WindowStream.Core.Protocol;
using WindowStream.Core.Session;
using WindowStream.Core.Session.Adapters;
using WindowStream.Core.Session.Input;
using WindowStream.Core.Transport;

namespace WindowStream.Core.Hosting;

/// <summary>
/// Production wiring for the v2 coordinator. Composes
/// <see cref="WindowEnumerator"/>, <see cref="WindowIdentityRegistry"/>,
/// <see cref="WorkerSupervisor"/>, <see cref="StreamRouter"/>,
/// <see cref="LoadShedder"/>, <see cref="FocusRelay"/>, the
/// <see cref="CoordinatorControlServer"/>, and the UDP fragmenter into a
/// single launch-and-serve entry point. Replaces the v1 SessionHost wiring.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Production composition; exercised by Phase 4 integration tests.")]
public sealed class CoordinatorLauncher : ISessionHostLauncher
{
    readonly int _tcpPort;
    readonly Diagnostics _diagnostics;

    public CoordinatorLauncher(int tcpPort, Diagnostics diagnostics)
    {
        _tcpPort = tcpPort;
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public async Task LaunchAsync(CancellationToken cancellationToken)
    {
        var captureSource = new WgcCaptureSource();
        var registry = new WindowIdentityRegistry();

        ConcurrentDictionary<ulong, long> windowIdToHwnd = new();
        ConcurrentDictionary<ulong, WindowDescriptor> windowIdToDescriptor = new();
        ConcurrentDictionary<int, ulong> streamIdToWindowId = new();

        var executablePath = Process.GetCurrentProcess().MainModule?.FileName
                             ?? throw new InvalidOperationException("could not determine current executable path");
        var workerLauncher = new WorkerProcessLauncher(executablePath);
        await using var supervisor = new WorkerSupervisor(
            workerLauncher, maximumConcurrentStreams: 8);

        var routerOutput = Channel.CreateUnbounded<TaggedChunk>();
        var shedderOutput = Channel.CreateBounded<TaggedChunk>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        var shedder = new LoadShedder(routerOutput, shedderOutput, perStreamMaximumQueueDepth: 8);

        await using var udpSender = new UdpVideoSenderAdapter();
        await udpSender.BindAsync(new IPEndPoint(IPAddress.Any, 0), cancellationToken)
            .ConfigureAwait(false);
        var tcpAcceptor = new TcpConnectionAcceptorAdapter(TimeProvider.System);

        var foregroundApi = new ForegroundWindowApi();
        var focusRelay = new FocusRelay(foregroundApi);

        var coordinatorOptions = new CoordinatorOptions(
            HeartbeatIntervalMilliseconds: 2000,
            HeartbeatTimeoutMilliseconds: 10000,
            ServerVersion: 2,
            MaximumConcurrentStreams: 8);

        Func<ulong, long?> resolveHwnd = windowId =>
            windowIdToHwnd.TryGetValue(windowId, out var handle) ? handle : null;

        Func<ulong, EncoderOptions?> resolveEncoderOptions = windowId =>
            ResolveEncoderOptionsFromDescriptor(windowId, windowIdToDescriptor);

        // udpSender/supervisor are await-using disposables shared with these control-server
        // delegates (and the Task.Run loops below); all run during the server's active phase
        // and are disposed only after the loops drain at scope exit. The analyzer cannot
        // prove this; per feedback_inspections_refactor_over_suppress (sanctioned Task.Run case).
        // ReSharper disable AccessToDisposedClosure
        await using var controlServer = new CoordinatorControlServer(
            options: coordinatorOptions,
            tcpAcceptor: tcpAcceptor,
            supervisor: supervisor,
            getCurrentWindows: () => windowIdToDescriptor.Values.ToArray(),
            resolveWindowIdToHwnd: resolveHwnd,
            resolveWindowIdToEncoderOptions: resolveEncoderOptions,
            getUdpPort: () => udpSender.LocalPort,
            sendWorkerCommand: async (streamId, tag) =>
            {
                var pipe = supervisor.GetPipe(streamId);
                if (pipe is not null)
                {
                    await WorkerChunkPipe.WriteCommandAsync(
                        pipe, new WorkerCommandFrame(tag), cancellationToken).ConfigureAwait(false);
                }
            },
            focusRelay: focusRelay,
            injectKeyForStream: (streamId, message) =>
            {
                if (streamIdToWindowId.TryGetValue(streamId, out var windowId))
                {
                    var hwnd = resolveHwnd(windowId);
                    if (hwnd is not null)
                    {
                        focusRelay.BringToForeground(hwnd.Value);
                    }
                }
                Win32InputInjector.InjectKey(message.KeyCode, message.IsUnicode, message.IsDown);
            },
            injectMouseForStream: (streamId, message) =>
            {
                if (streamIdToWindowId.TryGetValue(streamId, out var windowId))
                {
                    var hwnd = resolveHwnd(windowId);
                    if (hwnd is not null)
                    {
                        focusRelay.BringToForeground(hwnd.Value);
                    }
                }
                // Convert normalized [0,1] coordinates to Win32 absolute [0, 65535].
                var absoluteX = (int)(message.NormalizedX * 65535);
                var absoluteY = (int)(message.NormalizedY * 65535);
                Win32InputInjector.InjectMouse(absoluteX, absoluteY, message.EventType, message.ButtonFlags, message.ScrollDelta);
            },
            timeProvider: TimeProvider.System);

        var router = new StreamRouter(
            routerOutput,
            onStatus: (streamId, status) =>
            {
                switch (status.Kind)
                {
                    case WorkerStatusKind.SourceStalled:
                        _diagnostics.Report(new PipelineEvent.SourceStalled(streamId, status.Cause, status.LastFrameAgeMilliseconds));
                        controlServer.NotifyStreamStalled(streamId, status.Cause);
                        break;
                    case WorkerStatusKind.SourceResumed:
                        _diagnostics.Report(new PipelineEvent.SourceResumed(streamId, 0));
                        controlServer.NotifyStreamResumed(streamId);
                        break;
                    case WorkerStatusKind.CaptureError:
                        _diagnostics.Report(new PipelineEvent.CaptureErrorReported(streamId, status.Message));
                        break;
                    case WorkerStatusKind.EncodeError:
                        _diagnostics.Report(new PipelineEvent.EncodeErrorReported(streamId, status.Message));
                        break;
                }
            },
            onWatchdogStalled: (streamId, cause) =>
            {
                _diagnostics.Report(new PipelineEvent.SourceStalled(streamId, cause, 0));
                controlServer.NotifyStreamStalled(streamId, cause);
            },
            onWatchdogResumed: streamId =>
            {
                _diagnostics.Report(new PipelineEvent.SourceResumed(streamId, 0));
                controlServer.NotifyStreamResumed(streamId);
            },
            timeProvider: TimeProvider.System);

        // Hook supervisor stream lifecycle for routing and diagnostics.
        supervisor.StreamStarted += (sender, arguments) =>
        {
            streamIdToWindowId[arguments.StreamId] = arguments.WindowId;
            _ = router.ReadFromPipeAsync(arguments.StreamId, arguments.Pipe, cancellationToken);
            _diagnostics.Report(new PipelineEvent.WorkerSpawned(arguments.StreamId, arguments.WorkerProcessId));
        };
        supervisor.StreamEnded += (_, arguments) =>
        {
            streamIdToWindowId.TryRemove(arguments.StreamId, out var _);
            _diagnostics.Report(new PipelineEvent.StreamStopped(arguments.StreamId, arguments.Reason.ToString()));
        };

        // Subscribe to viewer connect/disconnect events from the control server.
        controlServer.ViewerConnected += (_, arguments) =>
        {
            _diagnostics.Report(new PipelineEvent.ViewerAccepted(arguments.Endpoint));
        };
        controlServer.ViewerDisconnected += (_, arguments) =>
        {
            _diagnostics.Report(new PipelineEvent.ViewerDisconnected(arguments.Endpoint, arguments.Reason));
        };

        // Spin up loops: load shedder, fragmenter+UDP sender, window enumerator, watchdog ticker.
        var shedderLoop = Task.Run(() => shedder.RunAsync(cancellationToken), cancellationToken);
        var fragmenterLoop = Task.Run(
            () => RunFragmenterLoopAsync(shedderOutput, udpSender, controlServer, cancellationToken),
            cancellationToken);
        var enumerationLoop = Task.Run(
            () => RunEnumerationLoopAsync(
                captureSource, registry, controlServer, windowIdToHwnd, windowIdToDescriptor,
                _diagnostics, cancellationToken),
            cancellationToken);
        var watchdogLoop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    router.EvaluateWatchdogs();
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown - cancellation is the expected exit path for this loop.
            }
        }, cancellationToken);
        // ReSharper restore AccessToDisposedClosure

        // mDNS advertise — instance name = MachineName, version=2 per spec.
        var advertisementOptions = new AdvertisementOptions(
            Hostname: Environment.MachineName,
            ProtocolMajorVersion: 2,
            ProtocolRevision: 0);
#pragma warning disable CA2000 // ownership of MakaretuMulticastServiceHost transfers to ServerAdvertiser which is await-using
        await using var advertiser = new ServerAdvertiser(new MakaretuMulticastServiceHost());
#pragma warning restore CA2000

        var controlServerTask = controlServer.RunAsync(_tcpPort, cancellationToken);
        // The acceptor is bound after RunAsync triggers StartListening — wait one
        // turn for that to settle, then read the assigned port.
        // (RunAsync calls tcpAcceptor.StartListening synchronously before its
        // async accept loop, so TcpPort is valid immediately.)
        await advertiser.StartAsync(advertisementOptions, controlServer.TcpPort, cancellationToken)
            .ConfigureAwait(false);

        _diagnostics.Report(new PipelineEvent.Listening(controlServer.TcpPort, udpSender.LocalPort));

        try
        {
            await controlServerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        // Drain background loops so cancellation propagates cleanly.
        try
        {
            await shedderLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        try
        {
            await fragmenterLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        try
        {
            await enumerationLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        try
        {
            await watchdogLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Fast path: use the dimensions already known from window enumeration instead of
    /// doing a WGC capture probe. A probe-based approach blocks the control server's TCP
    /// reader thread for up to 5 seconds per window, causing OPEN_STREAM requests to pile
    /// up and hang the viewer.
    /// </summary>
    static EncoderOptions? ResolveEncoderOptionsFromDescriptor(
        ulong windowId,
        ConcurrentDictionary<ulong, WindowDescriptor> windowIdToDescriptor)
    {
        if (!windowIdToDescriptor.TryGetValue(windowId, out var descriptor))
        {
            return null;
        }

        // NV12 requires even dimensions — round DOWN.
        var physicalWidth = descriptor.PhysicalWidth - (descriptor.PhysicalWidth % 2);
        var physicalHeight = descriptor.PhysicalHeight - (descriptor.PhysicalHeight % 2);
        if (physicalWidth <= 0 || physicalHeight <= 0)
        {
            return null;
        }

        var environmentSettings = EncoderEnvironmentSettings.Load(
            Environment.GetEnvironmentVariable);
        var framesPerSecond = environmentSettings.FramesPerSecond;
        var bitrateBitsPerSecond = 6_000_000 * framesPerSecond / 30;

        return new EncoderOptions(
            widthPixels: physicalWidth,
            heightPixels: physicalHeight,
            framesPerSecond: framesPerSecond,
            bitrateBitsPerSecond: bitrateBitsPerSecond,
            groupOfPicturesLength: environmentSettings.GroupOfPicturesLength,
            safetyKeyframeIntervalSeconds: 1);
    }

    static async Task RunFragmenterLoopAsync(
        Channel<TaggedChunk> shedderOutput,
        UdpVideoSenderAdapter udpSender,
        CoordinatorControlServer controlServer,
        CancellationToken cancellationToken)
    {
        var sequence = 0;
        try
        {
            await foreach (var chunk in shedderOutput.Reader.ReadAllAsync(cancellationToken))
            {
                var destination = controlServer.ActiveViewerEndpoint;
                if (destination is null)
                {
                    continue;
                }
                var currentSequence = Interlocked.Increment(ref sequence) - 1;
                foreach (var packet in NalFragmenter.Fragment(
                    streamId: chunk.StreamId,
                    sequence: currentSequence,
                    presentationTimestampMicroseconds: (long)chunk.Frame.PresentationTimestampMicroseconds,
                    isIdrFrame: chunk.Frame.IsKeyframe,
                    nalUnit: chunk.Frame.Payload))
                {
                    await udpSender.SendPacketAsync(packet, destination, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    static async Task RunEnumerationLoopAsync(
        WgcCaptureSource captureSource,
        WindowIdentityRegistry registry,
        CoordinatorControlServer controlServer,
        ConcurrentDictionary<ulong, long> windowIdToHwnd,
        ConcurrentDictionary<ulong, WindowDescriptor> windowIdToDescriptor,
        Diagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                List<WindowInformation> snapshot;
                try
                {
                    snapshot = captureSource.ListWindows().ToList();
                }
#pragma warning disable CA1031 // intentional catch-all in enumeration loop; exception is reported and loop continues
                catch (Exception enumerationException)
                {
                    diagnostics.Report(new PipelineEvent.EnumerationFailed(enumerationException));
                    continue;
                }
#pragma warning restore CA1031

                foreach (var enumerationEvent in registry.Diff(snapshot))
                {
                    switch (enumerationEvent)
                    {
                        case WindowAppeared appeared:
                            windowIdToHwnd[appeared.WindowId] = appeared.Information.Handle.Value;
                            var descriptor = new WindowDescriptor(
                                WindowId: appeared.WindowId,
                                Hwnd: appeared.Information.Handle.Value,
                                ProcessId: 0,
                                ProcessName: appeared.Information.ProcessName,
                                Title: appeared.Information.Title,
                                PhysicalWidth: appeared.Information.WidthPixels,
                                PhysicalHeight: appeared.Information.HeightPixels);
                            windowIdToDescriptor[appeared.WindowId] = descriptor;
                            controlServer.NotifyWindowAppeared(descriptor);
                            diagnostics.Report(new PipelineEvent.WindowAppeared(
                                appeared.WindowId,
                                appeared.Information.Title,
                                appeared.Information.ProcessName,
                                appeared.Information.WidthPixels,
                                appeared.Information.HeightPixels));
                            break;
                        case WindowDisappeared gone:
                            windowIdToHwnd.TryRemove(gone.WindowId, out var _);
                            windowIdToDescriptor.TryRemove(gone.WindowId, out var _);
                            controlServer.NotifyWindowDisappeared(gone.WindowId);
                            diagnostics.Report(new PipelineEvent.WindowDisappeared(gone.WindowId));
                            break;
                        case WindowChanged changed:
                            if (windowIdToDescriptor.TryGetValue(changed.WindowId, out var existing))
                            {
                                var updated = existing with
                                {
                                    Title = changed.NewTitle ?? existing.Title,
                                    PhysicalWidth = changed.NewWidthPixels ?? existing.PhysicalWidth,
                                    PhysicalHeight = changed.NewHeightPixels ?? existing.PhysicalHeight
                                };
                                windowIdToDescriptor[changed.WindowId] = updated;
                            }
                            controlServer.NotifyWindowChanged(
                                changed.WindowId,
                                changed.NewTitle,
                                changed.NewWidthPixels,
                                changed.NewHeightPixels);
                            diagnostics.Report(new PipelineEvent.WindowChanged(
                                changed.WindowId,
                                changed.NewTitle,
                                changed.NewWidthPixels,
                                changed.NewHeightPixels));
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
#endif
