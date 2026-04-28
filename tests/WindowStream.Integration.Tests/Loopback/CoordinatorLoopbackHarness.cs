#if WINDOWS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;
using WindowStream.Core.Protocol;
using WindowStream.Core.Session;
using WindowStream.Core.Session.Adapters;
using WindowStream.Core.Session.Input;
using WindowStream.Core.Transport;

namespace WindowStream.Integration.Tests.Loopback;

/// <summary>
/// In-process v2 coordinator wired up for integration tests. Composes the
/// production pieces — <see cref="WgcCaptureSource"/> (optional),
/// <see cref="WindowIdentityRegistry"/>, <see cref="WorkerSupervisor"/>,
/// <see cref="StreamRouter"/>, <see cref="LoadShedder"/>,
/// <see cref="UdpVideoSenderAdapter"/>, <see cref="TcpConnectionAcceptorAdapter"/>,
/// <see cref="FocusRelay"/>, and <see cref="CoordinatorControlServer"/> — into a
/// single async-disposable fixture that listens on ephemeral TCP and UDP ports.
/// Tests obtain a <see cref="FakeViewer"/> via <see cref="ConnectViewerAsync"/>
/// to drive the protocol from the viewer side.
/// </summary>
internal sealed class CoordinatorLoopbackHarness : IAsyncDisposable
{
    public const string Host = "127.0.0.1";

    private readonly CancellationTokenSource lifecycle;
    private readonly ConcurrentDictionary<ulong, long> windowIdToHwnd;
    private readonly ConcurrentDictionary<ulong, WindowDescriptor> windowIdToDescriptor;
    private readonly ConcurrentDictionary<ulong, EncoderOptions> windowIdToEncoderOptions;
    private readonly UdpVideoSenderAdapter udpSender;
    private readonly TcpConnectionAcceptorAdapter tcpAcceptor;
    private readonly WorkerSupervisor supervisor;
    private readonly CoordinatorControlServer controlServer;
    private readonly Task controlServerTask;
    private readonly Task shedderLoopTask;
    private readonly Task fragmenterLoopTask;
    private readonly Task? enumerationLoopTask;
    private bool disposed;

    private CoordinatorLoopbackHarness(
        CancellationTokenSource lifecycle,
        ConcurrentDictionary<ulong, long> windowIdToHwnd,
        ConcurrentDictionary<ulong, WindowDescriptor> windowIdToDescriptor,
        ConcurrentDictionary<ulong, EncoderOptions> windowIdToEncoderOptions,
        UdpVideoSenderAdapter udpSender,
        TcpConnectionAcceptorAdapter tcpAcceptor,
        WorkerSupervisor supervisor,
        CoordinatorControlServer controlServer,
        Task controlServerTask,
        Task shedderLoopTask,
        Task fragmenterLoopTask,
        Task? enumerationLoopTask)
    {
        this.lifecycle = lifecycle;
        this.windowIdToHwnd = windowIdToHwnd;
        this.windowIdToDescriptor = windowIdToDescriptor;
        this.windowIdToEncoderOptions = windowIdToEncoderOptions;
        this.udpSender = udpSender;
        this.tcpAcceptor = tcpAcceptor;
        this.supervisor = supervisor;
        this.controlServer = controlServer;
        this.controlServerTask = controlServerTask;
        this.shedderLoopTask = shedderLoopTask;
        this.fragmenterLoopTask = fragmenterLoopTask;
        this.enumerationLoopTask = enumerationLoopTask;
    }

    public int TcpPort => tcpAcceptor.LocalPort;

    public int UdpPort => udpSender.LocalPort;

    public WorkerSupervisor Supervisor => supervisor;

    public CoordinatorControlServer Server => controlServer;

    public static async Task<CoordinatorLoopbackHarness> StartAsync(
        int maximumConcurrentStreams = 8,
        IWorkerProcessLauncher? workerLauncher = null,
        bool useRealWgcEnumeration = false,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource lifecycle = new CancellationTokenSource();
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() => { try { lifecycle.Cancel(); } catch { /* already disposed */ } });
        }

        ConcurrentDictionary<ulong, long> windowIdToHwnd = new();
        ConcurrentDictionary<ulong, WindowDescriptor> windowIdToDescriptor = new();
        ConcurrentDictionary<ulong, EncoderOptions> windowIdToEncoderOptions = new();
        ConcurrentDictionary<int, ulong> streamIdToWindowId = new();

        IWorkerProcessLauncher launcher = workerLauncher ?? CreateRealLauncher();
        WorkerSupervisor supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams);

        Channel<TaggedChunk> routerOutput = Channel.CreateUnbounded<TaggedChunk>();
        Channel<TaggedChunk> shedderOutput = Channel.CreateBounded<TaggedChunk>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        StreamRouter streamRouter = new StreamRouter(routerOutput);
        LoadShedder loadShedder = new LoadShedder(routerOutput, shedderOutput, perStreamMaximumQueueDepth: 8);

        UdpVideoSenderAdapter udpSender = new UdpVideoSenderAdapter();
        await udpSender.BindAsync(new IPEndPoint(IPAddress.Loopback, 0), lifecycle.Token).ConfigureAwait(false);
        TcpConnectionAcceptorAdapter tcpAcceptor = new TcpConnectionAcceptorAdapter(
            TimeProvider.System, IPAddress.Loopback);

        FocusRelay focusRelay = new FocusRelay(new NoOpForegroundWindowApi());

        // Heartbeat timeout deliberately generous: tests using FakeViewer often pause
        // between protocol steps and we don't want the server tearing the connection
        // down during an unhurried assertion.
        CoordinatorOptions coordinatorOptions = new CoordinatorOptions(
            HeartbeatIntervalMilliseconds: 2000,
            HeartbeatTimeoutMilliseconds: 60000,
            ServerVersion: 2,
            MaximumConcurrentStreams: maximumConcurrentStreams);

        CoordinatorControlServer controlServer = new CoordinatorControlServer(
            options: coordinatorOptions,
            tcpAcceptor: tcpAcceptor,
            supervisor: supervisor,
            getCurrentWindows: () => windowIdToDescriptor.Values.ToArray(),
            resolveWindowIdToHwnd: windowId =>
                windowIdToHwnd.TryGetValue(windowId, out long handle) ? handle : null,
            resolveWindowIdToEncoderOptions: windowId =>
                windowIdToEncoderOptions.TryGetValue(windowId, out EncoderOptions? options) ? options : null,
            getUdpPort: () => udpSender.LocalPort,
            sendWorkerCommand: async (streamId, tag) =>
            {
                Stream? pipe = supervisor.GetPipe(streamId);
                if (pipe is not null)
                {
                    await WorkerChunkPipe.WriteCommandAsync(
                        pipe, new WorkerCommandFrame(tag), lifecycle.Token).ConfigureAwait(false);
                }
            },
            focusRelay: focusRelay,
            injectKeyForStream: (_, _) =>
            {
                // Tests don't exercise key injection by default; FocusRelay uses a
                // no-op API and Win32 input is out of scope here.
            },
            timeProvider: TimeProvider.System);

        // Hook supervisor stream lifecycle for routing.
        supervisor.StreamStarted += (_, args) =>
        {
            streamIdToWindowId[args.StreamId] = args.WindowId;
            _ = streamRouter.ReadFromPipeAsync(args.StreamId, args.Pipe, lifecycle.Token);
        };
        supervisor.StreamEnded += (_, args) =>
        {
            streamIdToWindowId.TryRemove(args.StreamId, out ulong _);
        };

        Task shedderLoopTask = Task.Run(() => loadShedder.RunAsync(lifecycle.Token), lifecycle.Token);
        Task fragmenterLoopTask = Task.Run(
            () => RunFragmenterLoopAsync(shedderOutput, udpSender, controlServer, lifecycle.Token),
            lifecycle.Token);

        Task? enumerationLoopTask = null;
        if (useRealWgcEnumeration)
        {
            WgcCaptureSource captureSource = new WgcCaptureSource();
            WindowIdentityRegistry registry = new WindowIdentityRegistry();
            enumerationLoopTask = Task.Run(
                () => RunEnumerationLoopAsync(
                    captureSource,
                    registry,
                    controlServer,
                    windowIdToHwnd,
                    windowIdToDescriptor,
                    lifecycle.Token),
                lifecycle.Token);
        }

        Task controlServerTask = controlServer.RunAsync(0, lifecycle.Token);

        return new CoordinatorLoopbackHarness(
            lifecycle,
            windowIdToHwnd,
            windowIdToDescriptor,
            windowIdToEncoderOptions,
            udpSender,
            tcpAcceptor,
            supervisor,
            controlServer,
            controlServerTask,
            shedderLoopTask,
            fragmenterLoopTask,
            enumerationLoopTask);
    }

    /// <summary>
    /// Registers a fake window with the coordinator so OPEN_STREAM can resolve it.
    /// Does NOT push WINDOW_ADDED to the active viewer — call
    /// <c>harness.Server.NotifyWindowAppeared(descriptor)</c> explicitly when the
    /// test needs to exercise the push path. Keeping the registration silent here
    /// prevents WINDOW_ADDED notifications from racing ahead of expected
    /// STREAM_STARTED / ERROR responses in the viewer's TCP receive queue.
    /// </summary>
    public void InjectWindow(WindowDescriptor descriptor, long hwnd, EncoderOptions encoderOptions)
    {
        windowIdToHwnd[descriptor.WindowId] = hwnd;
        windowIdToDescriptor[descriptor.WindowId] = descriptor;
        windowIdToEncoderOptions[descriptor.WindowId] = encoderOptions;
    }

    public Task<FakeViewer> ConnectViewerAsync(CancellationToken cancellationToken)
        => FakeViewer.ConnectAsync(Host, TcpPort, cancellationToken);

    private static IWorkerProcessLauncher CreateRealLauncher()
    {
        string executablePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("could not determine current executable path");
        return new WorkerProcessLauncher(executablePath);
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
            // normal shutdown
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
            // normal shutdown
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;

        try { lifecycle.Cancel(); } catch { /* already disposed */ }

        async Task SwallowAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { /* test fixture is being torn down */ }
        }

        await SwallowAsync(controlServerTask).ConfigureAwait(false);
        await SwallowAsync(shedderLoopTask).ConfigureAwait(false);
        await SwallowAsync(fragmenterLoopTask).ConfigureAwait(false);
        if (enumerationLoopTask is not null)
        {
            await SwallowAsync(enumerationLoopTask).ConfigureAwait(false);
        }

        await controlServer.DisposeAsync().ConfigureAwait(false);
        await supervisor.DisposeAsync().ConfigureAwait(false);
        await udpSender.DisposeAsync().ConfigureAwait(false);

        lifecycle.Dispose();
    }

    /// <summary>
    /// FocusRelay implementation that does nothing — tests don't actually want
    /// the harness manipulating real desktop focus.
    /// </summary>
    private sealed class NoOpForegroundWindowApi : IForegroundWindowApi
    {
        public long GetForegroundWindow() => 0;
        public uint GetWindowThreadProcessId(long hwnd) => 0;
        public bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach) => true;
        public bool SetForegroundWindow(long hwnd) => true;
        public uint CurrentThreadId() => 0;
    }
}

/// <summary>
/// Test-side viewer used to drive the v2 coordinator over loopback. Owns a TCP
/// control connection and a UDP receiver, parses incoming UDP packets,
/// reassembles per-stream NAL units, and exposes the assembled units via
/// <see cref="ReceiveNalUnitAsync"/>. Sends and receives JSON control messages
/// via <see cref="LengthPrefixFraming"/>.
/// </summary>
internal sealed class FakeViewer : IAsyncDisposable
{
    private readonly TcpClient tcpClient;
    private readonly NetworkStream tcpStream;
    private readonly UdpClient udpClient;
    private readonly Channel<UdpPacketCapture> rawUdpPackets =
        Channel.CreateUnbounded<UdpPacketCapture>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly ConcurrentDictionary<int, Channel<ReassembledNalUnit>> nalUnitsByStreamId = new();
    private readonly CancellationTokenSource pumpCancellation = new CancellationTokenSource();
    private readonly Task udpPumpTask;
    private bool disposed;

    private FakeViewer(TcpClient tcpClient, UdpClient udpClient)
    {
        this.tcpClient = tcpClient;
        this.tcpStream = tcpClient.GetStream();
        this.udpClient = udpClient;
        LocalUdpEndpoint = (IPEndPoint)udpClient.Client.LocalEndPoint!;
        udpPumpTask = Task.Run(() => RunUdpPumpAsync(pumpCancellation.Token));
    }

    public IPEndPoint LocalUdpEndpoint { get; }

    public static async Task<FakeViewer> ConnectAsync(string host, int tcpPort, CancellationToken cancellationToken)
    {
        TcpClient tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Parse(host), tcpPort, cancellationToken).ConfigureAwait(false);

        UdpClient udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return new FakeViewer(tcpClient, udpClient);
    }

    public async Task SendAsync(ControlMessage message, CancellationToken cancellationToken)
    {
        string json = ControlMessageSerialization.Serialize(message);
        byte[] payload = Encoding.UTF8.GetBytes(json);
        await LengthPrefixFraming.WriteFrameAsync(tcpStream, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[] payload = await LengthPrefixFraming.ReadFrameAsync(tcpStream, cancellationToken).ConfigureAwait(false);
        string json = Encoding.UTF8.GetString(payload);
        return ControlMessageSerialization.Deserialize(json);
    }

    /// <summary>
    /// Reads one UDP datagram from the wire, parses the WindowStream header, and
    /// surfaces it as a <see cref="UdpPacketCapture"/>. Bypasses NAL reassembly,
    /// so callers see every fragment exactly as the wire delivered it.
    /// </summary>
    public Task<UdpPacketCapture> ReceiveUdpPacketAsync(CancellationToken cancellationToken)
        => rawUdpPackets.Reader.ReadAsync(cancellationToken).AsTask();

    /// <summary>
    /// Reads one fully-reassembled NAL unit for the supplied stream id. Out-of-order
    /// fragments are buffered internally; the call only returns when every fragment
    /// of a NAL unit has arrived.
    /// </summary>
    public Task<ReassembledNalUnit> ReceiveNalUnitAsync(int streamId, CancellationToken cancellationToken)
    {
        Channel<ReassembledNalUnit> channel = nalUnitsByStreamId.GetOrAdd(
            streamId, _ => Channel.CreateUnbounded<ReassembledNalUnit>());
        return channel.Reader.ReadAsync(cancellationToken).AsTask();
    }

    private async Task RunUdpPumpAsync(CancellationToken cancellationToken)
    {
        // One reassembler keyed on (streamId, sequence) demultiplexes fragments
        // from any number of streams without cross-talk.
        NalReassembler reassembler = new NalReassembler(SystemClock.Instance, TimeSpan.FromSeconds(2));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result = await udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                byte[] datagram = result.Buffer;
                if (datagram.Length < PacketHeader.HeaderByteLength)
                {
                    continue;
                }

                PacketHeader header;
                try
                {
                    header = PacketHeader.Parse(datagram.AsSpan(0, PacketHeader.HeaderByteLength));
                }
                catch (MalformedPacketException)
                {
                    continue;
                }

                byte[] payload = new byte[datagram.Length - PacketHeader.HeaderByteLength];
                Array.Copy(datagram, PacketHeader.HeaderByteLength, payload, 0, payload.Length);

                UdpPacketCapture capture = new UdpPacketCapture(
                    StreamId: (int)header.StreamId,
                    Sequence: (int)header.Sequence,
                    PtsUs: (long)header.PresentationTimestampMicroseconds,
                    IsIdr: header.IsIdrFrame,
                    FragmentIndex: header.FragmentIndex,
                    FragmentTotal: header.FragmentTotal,
                    Payload: payload);
                await rawUdpPackets.Writer.WriteAsync(capture, cancellationToken).ConfigureAwait(false);

                ReassembledNalUnit? completed = reassembler.Offer(header, payload);
                if (completed is null) continue;

                ReassembledNalUnit unit = completed.Value;
                int streamId = (int)unit.StreamId;
                Channel<ReassembledNalUnit> channel = nalUnitsByStreamId.GetOrAdd(
                    streamId, _ => Channel.CreateUnbounded<ReassembledNalUnit>());
                await channel.Writer.WriteAsync(unit, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (ObjectDisposedException)
        {
            // udp client disposed
        }
        catch (SocketException)
        {
            // udp client closed
        }
        finally
        {
            rawUdpPackets.Writer.TryComplete();
            foreach (KeyValuePair<int, Channel<ReassembledNalUnit>> entry in nalUnitsByStreamId)
            {
                entry.Value.Writer.TryComplete();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;

        try { pumpCancellation.Cancel(); } catch { /* best-effort */ }
        try { tcpClient.Dispose(); } catch { /* best-effort */ }
        try { udpClient.Dispose(); } catch { /* best-effort */ }
        try { await udpPumpTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* fixture teardown */ }
        pumpCancellation.Dispose();
    }
}

/// <summary>
/// Snapshot of one UDP packet observed by <see cref="FakeViewer"/> on the wire.
/// Carries the parsed WindowStream header fields and the post-header payload
/// bytes so tests can assert per-packet shape (fragment index/total, IDR flag,
/// stream demultiplexing, etc.).
/// </summary>
internal sealed record UdpPacketCapture(
    int StreamId,
    int Sequence,
    long PtsUs,
    bool IsIdr,
    byte FragmentIndex,
    byte FragmentTotal,
    byte[] Payload);

#endif
