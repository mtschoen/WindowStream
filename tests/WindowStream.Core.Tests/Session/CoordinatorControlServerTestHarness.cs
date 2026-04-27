using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;
using WindowStream.Core.Protocol;
using WindowStream.Core.Session;
using WindowStream.Core.Session.Input;
using WindowStream.Core.Session.Testing;

namespace WindowStream.Core.Tests.Session;

/// <summary>
/// IAsyncDisposable harness composing the fakes used by
/// <see cref="CoordinatorControlServer"/> tests. Captures every invocation of
/// the callback parameters into thread-safe collections so tests can assert
/// against them after a short settle interval.
/// </summary>
internal sealed class CoordinatorControlServerTestHarness : IAsyncDisposable
{
    public CoordinatorControlServer Server { get; }
    public FakeTcpConnectionAcceptor TcpAcceptor { get; }
    public WorkerSupervisor Supervisor { get; }
    public FakeWorkerLauncher Launcher { get; }
    public FocusRelay FocusRelay { get; }
    public FakeForegroundApi ForegroundApi { get; }
    public List<WindowDescriptor> Windows { get; } = new List<WindowDescriptor>();
    public Dictionary<ulong, long> WindowToHwnd { get; } = new Dictionary<ulong, long>();
    public Dictionary<ulong, EncoderOptions> WindowToEncoder { get; } = new Dictionary<ulong, EncoderOptions>();
    public int UdpPort { get; set; } = 64500;
    public ConcurrentQueue<(int StreamId, WorkerCommandTag Tag)> WorkerCommands { get; } = new();
    public ConcurrentQueue<(int StreamId, KeyEventMessage Message)> KeyInjections { get; } = new();
    public Task RunTask { get; }

    private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
    private bool disposed;

    private CoordinatorControlServerTestHarness(
        CoordinatorControlServer server,
        FakeTcpConnectionAcceptor tcpAcceptor,
        WorkerSupervisor supervisor,
        FakeWorkerLauncher launcher,
        FocusRelay focusRelay,
        FakeForegroundApi foregroundApi,
        Task runTask)
    {
        Server = server;
        TcpAcceptor = tcpAcceptor;
        Supervisor = supervisor;
        Launcher = launcher;
        FocusRelay = focusRelay;
        ForegroundApi = foregroundApi;
        RunTask = runTask;
    }

    public static CoordinatorControlServerTestHarness Start(
        int maximumConcurrentStreams = 4,
        int heartbeatIntervalMilliseconds = 5000,
        int heartbeatTimeoutMilliseconds = 30_000,
        int serverVersion = 2)
    {
        FakeTcpConnectionAcceptor tcpAcceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);
        FakeWorkerLauncher launcher = new FakeWorkerLauncher();
        WorkerSupervisor supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams);

        FakeForegroundApi foregroundApi = new FakeForegroundApi();
        FocusRelay focusRelay = new FocusRelay(foregroundApi);

        CoordinatorOptions options = new CoordinatorOptions(
            HeartbeatIntervalMilliseconds: heartbeatIntervalMilliseconds,
            HeartbeatTimeoutMilliseconds: heartbeatTimeoutMilliseconds,
            ServerVersion: serverVersion,
            MaximumConcurrentStreams: maximumConcurrentStreams);

        CoordinatorControlServerTestHarness? harnessReference = null;
        Func<WindowDescriptor[]> getCurrentWindows = () =>
            harnessReference!.Windows.ToArray();
        Func<ulong, long?> resolveWindowIdToHwnd = windowId =>
            harnessReference!.WindowToHwnd.TryGetValue(windowId, out long hwnd) ? hwnd : null;
        Func<ulong, EncoderOptions?> resolveWindowIdToEncoderOptions = windowId =>
            harnessReference!.WindowToEncoder.TryGetValue(windowId, out EncoderOptions? options) ? options : null;
        Func<int> getUdpPort = () => harnessReference!.UdpPort;
        Func<int, WorkerCommandTag, Task> sendWorkerCommand = (streamId, tag) =>
        {
            harnessReference!.WorkerCommands.Enqueue((streamId, tag));
            return Task.CompletedTask;
        };
        Action<int, KeyEventMessage> injectKeyForStream = (streamId, message) =>
        {
            harnessReference!.KeyInjections.Enqueue((streamId, message));
        };

        CoordinatorControlServer server = new CoordinatorControlServer(
            options,
            tcpAcceptor,
            supervisor,
            getCurrentWindows,
            resolveWindowIdToHwnd,
            resolveWindowIdToEncoderOptions,
            getUdpPort,
            sendWorkerCommand,
            focusRelay,
            injectKeyForStream,
            TimeProvider.System);

        Task runTask = Task.Run(() => server.RunAsync(0, CancellationToken.None));

        CoordinatorControlServerTestHarness instance = new CoordinatorControlServerTestHarness(
            server, tcpAcceptor, supervisor, launcher, focusRelay, foregroundApi, runTask);
        harnessReference = instance;
        return instance;
    }

    public FakeViewerEndpoint ConnectViewer(IPAddress? remoteIpAddress = null)
    {
        return TcpAcceptor.EnqueueIncomingConnection(remoteIpAddress);
    }

    public async Task<FakeViewerEndpoint> ConnectAndHandshakeAsync(
        CancellationToken cancellationToken,
        IPAddress? remoteIpAddress = null)
    {
        FakeViewerEndpoint viewer = ConnectViewer(remoteIpAddress);
        await viewer.SendAsync(
            new HelloMessage(ViewerVersion: 2, DisplayCapabilities: new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellationToken).ConfigureAwait(false);
        _ = await viewer.ReceiveAsync<ServerHelloMessage>(cancellationToken).ConfigureAwait(false);
        return viewer;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Cancel();
        await Server.DisposeAsync().ConfigureAwait(false);
        await Supervisor.DisposeAsync().ConfigureAwait(false);
        cancellation.Dispose();
    }

    /// <summary>
    /// Test-only worker launcher that records and exposes every spawned handle.
    /// </summary>
    public sealed class FakeWorkerLauncher : IWorkerProcessLauncher
    {
        public List<FakeWorkerHandle> Launched { get; } = new();

        public Task<IWorkerHandle> LaunchAsync(WorkerLaunchArguments arguments, CancellationToken cancellationToken)
        {
            FakeWorkerHandle handle = new FakeWorkerHandle();
            Launched.Add(handle);
            return Task.FromResult<IWorkerHandle>(handle);
        }
    }

    public sealed class FakeWorkerHandle : IWorkerHandle
    {
        private readonly TaskCompletionSource<int> exitSource = new();

        public Stream Pipe { get; } = new MemoryStream();

        public Task<int> WaitForExitAsync() => exitSource.Task;

        public void Kill() => exitSource.TrySetResult(137);

        public void SimulateEncoderFailure() => exitSource.TrySetResult(1);

        public ValueTask DisposeAsync()
        {
            Kill();
            return ValueTask.CompletedTask;
        }
    }

    public sealed class FakeForegroundApi : IForegroundWindowApi
    {
        public long Foreground { get; set; }
        public List<long> SetForegroundCalls { get; } = new();

        public long GetForegroundWindow() => Foreground;

        public uint GetWindowThreadProcessId(long hwnd) => 1;

        public bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach) => true;

        public bool SetForegroundWindow(long hwnd)
        {
            SetForegroundCalls.Add(hwnd);
            Foreground = hwnd;
            return true;
        }

        public uint CurrentThreadId() => 99;
    }
}
