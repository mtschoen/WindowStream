#if WINDOWS
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
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
sealed class CoordinatorLoopbackHarness : IAsyncDisposable
{
    public const string Host = "127.0.0.1";

    readonly CancellationTokenSource _lifecycle;
    readonly ConcurrentDictionary<ulong, long> _windowIdToHwnd;
    readonly ConcurrentDictionary<ulong, WindowDescriptor> _windowIdToDescriptor;
    readonly ConcurrentDictionary<ulong, EncoderOptions> _windowIdToEncoderOptions;
    readonly UdpVideoSenderAdapter _udpSender;
    readonly TcpConnectionAcceptorAdapter _tcpAcceptor;
    readonly WorkerSupervisor _supervisor;
    readonly CoordinatorControlServer _controlServer;
    readonly Task _controlServerTask;
    readonly Task _shedderLoopTask;
    readonly Task _fragmenterLoopTask;
    readonly Task? _enumerationLoopTask;
    bool _disposed;

    CoordinatorLoopbackHarness(
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
        _lifecycle = lifecycle;
        _windowIdToHwnd = windowIdToHwnd;
        _windowIdToDescriptor = windowIdToDescriptor;
        _windowIdToEncoderOptions = windowIdToEncoderOptions;
        _udpSender = udpSender;
        _tcpAcceptor = tcpAcceptor;
        _supervisor = supervisor;
        _controlServer = controlServer;
        _controlServerTask = controlServerTask;
        _shedderLoopTask = shedderLoopTask;
        _fragmenterLoopTask = fragmenterLoopTask;
        _enumerationLoopTask = enumerationLoopTask;
    }

    public int TcpPort => _tcpAcceptor.LocalPort;

    public int UdpPort => _udpSender.LocalPort;

    public WorkerSupervisor Supervisor => _supervisor;

    public CoordinatorControlServer Server => _controlServer;

    public static async Task<CoordinatorLoopbackHarness> StartAsync(
        int maximumConcurrentStreams = 8,
        IWorkerProcessLauncher? workerLauncher = null,
        bool useRealWgcEnumeration = false,
        IForegroundWindowApi? foregroundWindowApi = null,
        CancellationToken cancellationToken = default)
    {
        var lifecycle = new CancellationTokenSource();
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
#pragma warning disable CA1031 // best-effort: lifecycle CTS may already be disposed at shutdown
                try { lifecycle.Cancel(); } catch { /* already disposed */ }
#pragma warning restore CA1031
            });
        }

        ConcurrentDictionary<ulong, long> windowIdToHwnd = new();
        ConcurrentDictionary<ulong, WindowDescriptor> windowIdToDescriptor = new();
        ConcurrentDictionary<ulong, EncoderOptions> windowIdToEncoderOptions = new();
        ConcurrentDictionary<int, ulong> streamIdToWindowId = new();

        var launcher = workerLauncher ?? CreateRealLauncher();
        var supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams);

        var routerOutput = Channel.CreateUnbounded<TaggedChunk>();
        var shedderOutput = Channel.CreateBounded<TaggedChunk>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        var streamRouter = new StreamRouter(routerOutput);
        var loadShedder = new LoadShedder(routerOutput, shedderOutput, perStreamMaximumQueueDepth: 8);

        var udpSender = new UdpVideoSenderAdapter();
        await udpSender.BindAsync(new IPEndPoint(IPAddress.Loopback, 0), lifecycle.Token).ConfigureAwait(false);
        var tcpAcceptor = new TcpConnectionAcceptorAdapter(
            TimeProvider.System, IPAddress.Loopback);

        var focusRelay = new FocusRelay(foregroundWindowApi ?? new NoOpForegroundWindowApi());

        // Heartbeat timeout deliberately generous: tests using FakeViewer often pause
        // between protocol steps and we don't want the server tearing the connection
        // down during an unhurried assertion.
        var coordinatorOptions = new CoordinatorOptions(
            HeartbeatIntervalMilliseconds: 2000,
            HeartbeatTimeoutMilliseconds: 60000,
            ServerVersion: 2,
            MaximumConcurrentStreams: maximumConcurrentStreams);

        var controlServer = new CoordinatorControlServer(
            options: coordinatorOptions,
            tcpAcceptor: tcpAcceptor,
            supervisor: supervisor,
            getCurrentWindows: () => windowIdToDescriptor.Values.ToArray(),
            resolveWindowIdToHwnd: windowId =>
                windowIdToHwnd.TryGetValue(windowId, out var handle) ? handle : null,
            resolveWindowIdToEncoderOptions: windowId =>
                windowIdToEncoderOptions.TryGetValue(windowId, out var options) ? options : null,
            getUdpPort: () => udpSender.LocalPort,
            sendWorkerCommand: async (streamId, tag) =>
            {
                var pipe = supervisor.GetPipe(streamId);
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
            injectMouseForStream: (_, _) =>
            {
                // Tests don't exercise mouse injection by default.
            },
            timeProvider: TimeProvider.System);

        // Hook supervisor stream lifecycle for routing.
        supervisor.StreamStarted += (sender, args) =>
        {
            streamIdToWindowId[args.StreamId] = args.WindowId;
            _ = streamRouter.ReadFromPipeAsync(args.StreamId, args.Pipe, lifecycle.Token);
        };
        supervisor.StreamEnded += (_, args) =>
        {
            streamIdToWindowId.TryRemove(args.StreamId, out var _);
        };

        var shedderLoopTask = Task.Run(() => loadShedder.RunAsync(lifecycle.Token), lifecycle.Token);
        var fragmenterLoopTask = Task.Run(
            () => RunFragmenterLoopAsync(shedderOutput, udpSender, controlServer, lifecycle.Token),
            lifecycle.Token);

        Task? enumerationLoopTask = null;
        if (useRealWgcEnumeration)
        {
            var captureSource = new WgcCaptureSource();
            var registry = new WindowIdentityRegistry();
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

        var controlServerTask = controlServer.RunAsync(0, lifecycle.Token);

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
        _windowIdToHwnd[descriptor.WindowId] = hwnd;
        _windowIdToDescriptor[descriptor.WindowId] = descriptor;
        _windowIdToEncoderOptions[descriptor.WindowId] = encoderOptions;
    }

    public Task<FakeViewer> ConnectViewerAsync(CancellationToken cancellationToken)
        => FakeViewer.ConnectAsync(Host, TcpPort, cancellationToken);

#pragma warning disable CA1859 // CA1859: factory intentionally returns IWorkerProcessLauncher
    static IWorkerProcessLauncher CreateRealLauncher()
    {
        var executablePath = Process.GetCurrentProcess().MainModule?.FileName
                             ?? throw new InvalidOperationException("could not determine current executable path");
        return new WorkerProcessLauncher(executablePath);
    }
#pragma warning restore CA1859

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
            // normal shutdown
        }
    }

    static async Task RunEnumerationLoopAsync(
        WgcCaptureSource captureSource,
        WindowIdentityRegistry registry,
        CoordinatorControlServer controlServer,
        ConcurrentDictionary<ulong, long> windowIdToHwnd,
        ConcurrentDictionary<ulong, WindowDescriptor> windowIdToDescriptor,
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
#pragma warning disable CA1031 // intentional: WGC enumeration errors are non-fatal in the test harness loop
                catch (Exception)
#pragma warning restore CA1031
                {
                    continue;
                }

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
                            break;
                        case WindowDisappeared gone:
                            windowIdToHwnd.TryRemove(gone.WindowId, out var _);
                            windowIdToDescriptor.TryRemove(gone.WindowId, out var _);
                            controlServer.NotifyWindowDisappeared(gone.WindowId);
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
        if (_disposed) return;
        _disposed = true;

#pragma warning disable CA1031 // best-effort: lifecycle CTS may already be disposed at shutdown
        try { await _lifecycle.CancelAsync(); } catch { /* already disposed */ }
#pragma warning restore CA1031

        async Task SwallowAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
#pragma warning disable CA1031 // intentional: test fixture teardown swallows all task faults
#pragma warning disable RCS1075 // RCS1075: best-effort teardown; failures during cleanup are intentionally ignored
            catch (Exception) { /* test fixture is being torn down */ }
#pragma warning restore RCS1075
#pragma warning restore CA1031
        }

        await SwallowAsync(_controlServerTask).ConfigureAwait(false);
        await SwallowAsync(_shedderLoopTask).ConfigureAwait(false);
        await SwallowAsync(_fragmenterLoopTask).ConfigureAwait(false);
        if (_enumerationLoopTask is not null)
        {
            await SwallowAsync(_enumerationLoopTask).ConfigureAwait(false);
        }

        await _controlServer.DisposeAsync().ConfigureAwait(false);
        await _supervisor.DisposeAsync().ConfigureAwait(false);
        await _udpSender.DisposeAsync().ConfigureAwait(false);

        _lifecycle.Dispose();
    }

    /// <summary>
    /// FocusRelay implementation that does nothing — tests don't actually want
    /// the harness manipulating real desktop focus.
    /// </summary>
    sealed class NoOpForegroundWindowApi : IForegroundWindowApi
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
sealed class FakeViewer : IAsyncDisposable
{
    readonly TcpClient _tcpClient;
    readonly NetworkStream _tcpStream;
    readonly UdpClient _udpClient;

    readonly Channel<UdpPacketCapture> _rawUdpPackets =
        Channel.CreateUnbounded<UdpPacketCapture>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    readonly ConcurrentDictionary<int, Channel<ReassembledNalUnit>> _nalUnitsByStreamId = new();
    readonly CancellationTokenSource _pumpCancellation = new CancellationTokenSource();
    readonly Task _udpPumpTask;
    bool _disposed;

    FakeViewer(TcpClient tcpClient, UdpClient udpClient)
    {
        _tcpClient = tcpClient;
        _tcpStream = tcpClient.GetStream();
        _udpClient = udpClient;
        LocalUdpEndpoint = (IPEndPoint)udpClient.Client.LocalEndPoint!;
        _udpPumpTask = Task.Run(() => RunUdpPumpAsync(_pumpCancellation.Token));
    }

    public IPEndPoint LocalUdpEndpoint { get; }

    public static async Task<FakeViewer> ConnectAsync(string host, int tcpPort, CancellationToken cancellationToken)
    {
        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Parse(host), tcpPort, cancellationToken).ConfigureAwait(false);

        var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return new FakeViewer(tcpClient, udpClient);
    }

    public async Task SendAsync(ControlMessage message, CancellationToken cancellationToken)
    {
        var json = ControlMessageSerialization.Serialize(message);
        var payload = Encoding.UTF8.GetBytes(json);
        await LengthPrefixFraming.WriteFrameAsync(_tcpStream, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        var payload = await LengthPrefixFraming.ReadFrameAsync(_tcpStream, cancellationToken).ConfigureAwait(false);
        var json = Encoding.UTF8.GetString(payload);
        return ControlMessageSerialization.Deserialize(json);
    }

    /// <summary>
    /// Reads one UDP datagram from the wire, parses the WindowStream header, and
    /// surfaces it as a <see cref="UdpPacketCapture"/>. Bypasses NAL reassembly,
    /// so callers see every fragment exactly as the wire delivered it.
    /// </summary>
    public Task<UdpPacketCapture> ReceiveUdpPacketAsync(CancellationToken cancellationToken)
        => _rawUdpPackets.Reader.ReadAsync(cancellationToken).AsTask();

    /// <summary>
    /// Reads one fully-reassembled NAL unit for the supplied stream id. Out-of-order
    /// fragments are buffered internally; the call only returns when every fragment
    /// of a NAL unit has arrived.
    /// </summary>
    public Task<ReassembledNalUnit> ReceiveNalUnitAsync(int streamId, CancellationToken cancellationToken)
    {
        var channel = _nalUnitsByStreamId.GetOrAdd(
            streamId, _ => Channel.CreateUnbounded<ReassembledNalUnit>());
        return channel.Reader.ReadAsync(cancellationToken).AsTask();
    }

    async Task RunUdpPumpAsync(CancellationToken cancellationToken)
    {
        // One reassembler keyed on (streamId, sequence) demultiplexes fragments
        // from any number of streams without cross-talk.
        var reassembler = new NalReassembler(SystemClock.Instance, TimeSpan.FromSeconds(2));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                var datagram = result.Buffer;
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

                var payload = new byte[datagram.Length - PacketHeader.HeaderByteLength];
                Array.Copy(datagram, PacketHeader.HeaderByteLength, payload, 0, payload.Length);

                var capture = new UdpPacketCapture(
                    StreamId: (int)header.StreamId,
                    Sequence: (int)header.Sequence,
                    PtsUs: (long)header.PresentationTimestampMicroseconds,
                    IsIdr: header.IsIdrFrame,
                    FragmentIndex: header.FragmentIndex,
                    FragmentTotal: header.FragmentTotal,
                    Payload: payload);
                await _rawUdpPackets.Writer.WriteAsync(capture, cancellationToken).ConfigureAwait(false);

                var completed = reassembler.Offer(header, payload);
                if (completed is null) continue;

                var unit = completed.Value;
                var streamId = (int)unit.StreamId;
                var channel = _nalUnitsByStreamId.GetOrAdd(
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
            _rawUdpPackets.Writer.TryComplete();
            foreach (var entry in _nalUnitsByStreamId)
            {
                entry.Value.Writer.TryComplete();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

#pragma warning disable CA1031 // best-effort: pump CTS may already be disposed
        try { await _pumpCancellation.CancelAsync(); } catch { /* best-effort */ }
        try { _tcpStream.Dispose(); } catch { /* best-effort */ }
        try { _tcpClient.Dispose(); } catch { /* best-effort */ }
        try { _udpClient.Dispose(); } catch { /* best-effort */ }
#pragma warning restore CA1031
        try { await _udpPumpTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
#pragma warning disable CA1031 // intentional: test fixture teardown swallows all pump faults
#pragma warning disable RCS1075 // RCS1075: best-effort teardown; failures during cleanup are intentionally ignored
        catch (Exception) { /* fixture teardown */ }
#pragma warning restore RCS1075
#pragma warning restore CA1031
        _pumpCancellation.Dispose();
    }
}

/// <summary>
/// Snapshot of one UDP packet observed by <see cref="FakeViewer"/> on the wire.
/// Carries the parsed WindowStream header fields and the post-header payload
/// bytes so tests can assert per-packet shape (fragment index/total, IDR flag,
/// stream demultiplexing, etc.).
/// </summary>
sealed record UdpPacketCapture(
    int StreamId,
    int Sequence,
    long PtsUs,
    bool IsIdr,
    byte FragmentIndex,
    byte FragmentTotal,
    byte[] Payload);

#endif
