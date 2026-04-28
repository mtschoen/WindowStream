#if WINDOWS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Discovery;
using WindowStream.Core.Encode;
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
    private readonly int tcpPort;
    private readonly TextWriter output;

    public CoordinatorLauncher(int tcpPort, TextWriter output)
    {
        this.tcpPort = tcpPort;
        this.output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public async Task LaunchAsync(CancellationToken cancellationToken)
    {
        WgcCaptureSource captureSource = new WgcCaptureSource();
        WindowIdentityRegistry registry = new WindowIdentityRegistry();

        ConcurrentDictionary<ulong, long> windowIdToHwnd = new();
        ConcurrentDictionary<ulong, WindowDescriptor> windowIdToDescriptor = new();
        ConcurrentDictionary<int, ulong> streamIdToWindowId = new();

        string executablePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("could not determine current executable path");
        WorkerProcessLauncher workerLauncher = new WorkerProcessLauncher(executablePath);
        await using WorkerSupervisor supervisor = new WorkerSupervisor(
            workerLauncher, maximumConcurrentStreams: 8);

        Channel<TaggedChunk> routerOutput = Channel.CreateUnbounded<TaggedChunk>();
        Channel<TaggedChunk> shedderOutput = Channel.CreateBounded<TaggedChunk>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        StreamRouter router = new StreamRouter(routerOutput);
        LoadShedder shedder = new LoadShedder(routerOutput, shedderOutput, perStreamMaximumQueueDepth: 8);

        await using UdpVideoSenderAdapter udpSender = new UdpVideoSenderAdapter();
        await udpSender.BindAsync(new IPEndPoint(IPAddress.Any, 0), cancellationToken)
            .ConfigureAwait(false);
        TcpConnectionAcceptorAdapter tcpAcceptor = new TcpConnectionAcceptorAdapter(TimeProvider.System);

        ForegroundWindowApi foregroundApi = new ForegroundWindowApi();
        FocusRelay focusRelay = new FocusRelay(foregroundApi);

        CoordinatorOptions coordinatorOptions = new CoordinatorOptions(
            HeartbeatIntervalMilliseconds: 2000,
            HeartbeatTimeoutMilliseconds: 10000,
            ServerVersion: 2,
            MaximumConcurrentStreams: 8);

        Func<ulong, long?> resolveHwnd = windowId =>
            windowIdToHwnd.TryGetValue(windowId, out long handle) ? handle : null;

        Func<ulong, EncoderOptions?> resolveEncoderOptions = windowId =>
            ResolveEncoderOptions(windowId, resolveHwnd, cancellationToken);

        await using CoordinatorControlServer controlServer = new CoordinatorControlServer(
            options: coordinatorOptions,
            tcpAcceptor: tcpAcceptor,
            supervisor: supervisor,
            getCurrentWindows: () => windowIdToDescriptor.Values.ToArray(),
            resolveWindowIdToHwnd: resolveHwnd,
            resolveWindowIdToEncoderOptions: resolveEncoderOptions,
            getUdpPort: () => udpSender.LocalPort,
            sendWorkerCommand: async (streamId, tag) =>
            {
                Stream? pipe = supervisor.GetPipe(streamId);
                if (pipe is not null)
                {
                    await WorkerChunkPipe.WriteCommandAsync(
                        pipe, new WorkerCommandFrame(tag), cancellationToken).ConfigureAwait(false);
                }
            },
            focusRelay: focusRelay,
            injectKeyForStream: (streamId, message) =>
            {
                if (streamIdToWindowId.TryGetValue(streamId, out ulong windowId))
                {
                    long? hwnd = resolveHwnd(windowId);
                    if (hwnd is not null)
                    {
                        focusRelay.BringToForeground(hwnd.Value);
                    }
                }
                Win32InputInjector.InjectKey(message.KeyCode, message.IsUnicode, message.IsDown);
            },
            timeProvider: TimeProvider.System);

        // Hook supervisor stream lifecycle for routing.
        supervisor.StreamStarted += (_, args) =>
        {
            streamIdToWindowId[args.StreamId] = args.WindowId;
            _ = router.ReadFromPipeAsync(args.StreamId, args.Pipe, cancellationToken);
        };
        supervisor.StreamEnded += (_, args) =>
        {
            streamIdToWindowId.TryRemove(args.StreamId, out ulong _);
        };

        // Spin up loops: load shedder, fragmenter+UDP sender, window enumerator.
        Task shedderLoop = Task.Run(() => shedder.RunAsync(cancellationToken), cancellationToken);
        Task fragmenterLoop = Task.Run(
            () => RunFragmenterLoopAsync(shedderOutput, udpSender, controlServer, cancellationToken),
            cancellationToken);
        Task enumerationLoop = Task.Run(
            () => RunEnumerationLoopAsync(
                captureSource, registry, controlServer, windowIdToHwnd, windowIdToDescriptor, cancellationToken),
            cancellationToken);

        // mDNS advertise — instance name = MachineName, version=2 per spec.
        AdvertisementOptions advertisementOptions = new AdvertisementOptions(
            hostname: Environment.MachineName,
            protocolMajorVersion: 2,
            protocolRevision: 0);
        await using ServerAdvertiser advertiser = new ServerAdvertiser(new MakaretuMulticastServiceHost());

        Task controlServerTask = controlServer.RunAsync(tcpPort, cancellationToken);
        // The acceptor is bound after RunAsync triggers StartListening — wait one
        // turn for that to settle, then read the assigned port.
        // (RunAsync calls tcpAcceptor.StartListening synchronously before its
        // async accept loop, so TcpPort is valid immediately.)
        await advertiser.StartAsync(advertisementOptions, controlServer.TcpPort, cancellationToken)
            .ConfigureAwait(false);

        output.WriteLine($"windowstream: serving on TCP {controlServer.TcpPort}, UDP {udpSender.LocalPort}");
        output.WriteLine($"  mDNS: _windowstream._tcp as '{Environment.MachineName}' (v2)");
        output.WriteLine("  Press Ctrl-C to stop.");

        try
        {
            await controlServerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        // Drain background loops so cancellation propagates cleanly.
        try { await shedderLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await fragmenterLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await enumerationLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private static EncoderOptions? ResolveEncoderOptions(
        ulong windowId,
        Func<ulong, long?> resolveHwnd,
        CancellationToken cancellationToken)
    {
        long? hwnd = resolveHwnd(windowId);
        if (hwnd is null)
        {
            return null;
        }

        WindowHandle handle = new WindowHandle(hwnd.Value);
        (int probeWidth, int probeHeight)? probed;
        try
        {
            probed = ProbeCaptureSizeAsync(handle, cancellationToken).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }

        if (probed is null)
        {
            return null;
        }

        // NV12 requires even dimensions — round DOWN.
        int physicalWidth = probed.Value.probeWidth - (probed.Value.probeWidth % 2);
        int physicalHeight = probed.Value.probeHeight - (probed.Value.probeHeight % 2);
        if (physicalWidth <= 0 || physicalHeight <= 0)
        {
            return null;
        }

        int gopLength = 30;
        string? gopOverride = Environment.GetEnvironmentVariable("WINDOWSTREAM_NVENC_GOP");
        if (gopOverride is not null && int.TryParse(gopOverride, out int parsedGop) && parsedGop >= 1)
        {
            gopLength = parsedGop;
        }

        int framesPerSecond = 60;
        string? fpsOverride = Environment.GetEnvironmentVariable("WINDOWSTREAM_NVENC_FPS");
        if (fpsOverride is not null && int.TryParse(fpsOverride, out int parsedFps) && parsedFps >= 1)
        {
            framesPerSecond = parsedFps;
        }
        int bitrateBitsPerSecond = 6_000_000 * framesPerSecond / 30;

        return new EncoderOptions(
            widthPixels: physicalWidth,
            heightPixels: physicalHeight,
            framesPerSecond: framesPerSecond,
            bitrateBitsPerSecond: bitrateBitsPerSecond,
            groupOfPicturesLength: gopLength,
            safetyKeyframeIntervalSeconds: 1);
    }

    private static async Task<(int probeWidth, int probeHeight)?> ProbeCaptureSizeAsync(
        WindowHandle handle,
        CancellationToken cancellationToken)
    {
        WgcCaptureSource probeSource = new WgcCaptureSource();
        using CancellationTokenSource probeTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using IWindowCapture probe = probeSource.Start(
                handle, new CaptureOptions(30, false), probeTimeout.Token);
            await foreach (CapturedFrame frame in probe.Frames.WithCancellation(probeTimeout.Token))
            {
                return (frame.widthPixels, frame.heightPixels);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        return null;
    }

    private static async Task RunFragmenterLoopAsync(
        Channel<TaggedChunk> shedderOutput,
        UdpVideoSenderAdapter udpSender,
        CoordinatorControlServer controlServer,
        CancellationToken cancellationToken)
    {
        NalFragmenter fragmenter = new NalFragmenter();
        int sequence = 0;
        try
        {
            await foreach (TaggedChunk chunk in shedderOutput.Reader.ReadAllAsync(cancellationToken))
            {
                IPEndPoint? destination = controlServer.ActiveViewerEndpoint;
                if (destination is null)
                {
                    continue;
                }
                int currentSequence = Interlocked.Increment(ref sequence) - 1;
                foreach (FragmentedPacket packet in fragmenter.Fragment(
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

    private static async Task RunEnumerationLoopAsync(
        WgcCaptureSource captureSource,
        WindowIdentityRegistry registry,
        CoordinatorControlServer controlServer,
        ConcurrentDictionary<ulong, long> windowIdToHwnd,
        ConcurrentDictionary<ulong, WindowDescriptor> windowIdToDescriptor,
        CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                List<WindowInformation> snapshot;
                try
                {
                    snapshot = captureSource.ListWindows().ToList();
                }
                catch (Exception)
                {
                    // Enumeration failure is transient — try again next tick.
                    continue;
                }

                foreach (WindowEnumerationEvent enumerationEvent in registry.Diff(snapshot))
                {
                    switch (enumerationEvent)
                    {
                        case WindowAppeared appeared:
                            windowIdToHwnd[appeared.WindowId] = appeared.Information.handle.value;
                            WindowDescriptor descriptor = new WindowDescriptor(
                                WindowId: appeared.WindowId,
                                Hwnd: appeared.Information.handle.value,
                                ProcessId: 0,
                                ProcessName: appeared.Information.processName,
                                Title: appeared.Information.title,
                                PhysicalWidth: appeared.Information.widthPixels,
                                PhysicalHeight: appeared.Information.heightPixels);
                            windowIdToDescriptor[appeared.WindowId] = descriptor;
                            controlServer.NotifyWindowAppeared(descriptor);
                            break;
                        case WindowDisappeared gone:
                            windowIdToHwnd.TryRemove(gone.WindowId, out long _);
                            windowIdToDescriptor.TryRemove(gone.WindowId, out WindowDescriptor? _);
                            controlServer.NotifyWindowDisappeared(gone.WindowId);
                            break;
                        case WindowChanged changed:
                            if (windowIdToDescriptor.TryGetValue(changed.WindowId, out WindowDescriptor? existing))
                            {
                                WindowDescriptor updated = existing with
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
