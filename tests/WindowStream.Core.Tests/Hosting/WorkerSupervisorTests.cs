using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;
using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Hosting;

public class WorkerSupervisorTests
{
    static EncoderOptions DefaultEncoderOptions()
        => new EncoderOptions(800, 600, 30, 4_000_000, 30, 1);

    sealed class FakeWorkerHandle : IWorkerHandle
    {
        readonly TaskCompletionSource<int> _exitSource = new();

        public FakeWorkerHandle(Stream pipe)
        {
            Pipe = pipe;
        }

        public Stream Pipe { get; }

        public int ProcessId => 0;

        public Task<int> WaitForExitAsync() => _exitSource.Task;

        public void Kill() => _exitSource.TrySetResult(137);

        public ValueTask DisposeAsync()
        {
            Kill();
            return ValueTask.CompletedTask;
        }

        public void SimulateClean() => _exitSource.TrySetResult(0);

        public void SimulateEncoderFailure() => _exitSource.TrySetResult(1);
    }

    sealed class FakeLauncher : IWorkerProcessLauncher
    {
        public List<FakeWorkerHandle> Launched { get; } = new();

        public Task<IWorkerHandle> LaunchAsync(WorkerLaunchArguments arguments, CancellationToken cancellationToken)
        {
            var handle = new FakeWorkerHandle(new MemoryStream());
            Launched.Add(handle);
            return Task.FromResult<IWorkerHandle>(handle);
        }
    }

    [Fact]
    public async Task StartStream_AssignsMonotonicStreamId()
    {
        var launcher = new FakeLauncher();
        await using var supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 4);
        var a = await supervisor.StartStreamAsync(
            windowId: 1, hwnd: 0x100, DefaultEncoderOptions(), CancellationToken.None);
        var b = await supervisor.StartStreamAsync(
            windowId: 2, hwnd: 0x200, DefaultEncoderOptions(), CancellationToken.None);
        Assert.Equal(1, a.StreamId);
        Assert.Equal(2, b.StreamId);
    }

    [Fact]
    public async Task StartStream_RefusesPastCapacity()
    {
        var launcher = new FakeLauncher();
        await using var supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 1);
        await supervisor.StartStreamAsync(1, 0x100, DefaultEncoderOptions(), CancellationToken.None);
        await Assert.ThrowsAsync<EncoderCapacityException>(
            () => supervisor.StartStreamAsync(2, 0x200, DefaultEncoderOptions(), CancellationToken.None));
    }

    [Fact]
    public async Task UnexpectedExit_FiresStreamEnded_WithEncoderFailed()
    {
        var launcher = new FakeLauncher();
        await using var supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 4);
        TaskCompletionSource<StreamEndedEventArguments> ended = new();
        supervisor.StreamEnded += (_, arguments) => ended.TrySetResult(arguments);

        var handle = await supervisor.StartStreamAsync(
            1, 0x100, DefaultEncoderOptions(), CancellationToken.None);
        launcher.Launched[0].SimulateEncoderFailure();

        var observed = await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(handle.StreamId, observed.StreamId);
        Assert.Equal(StreamStoppedReason.EncoderFailed, observed.Reason);
    }

    [Fact]
    public async Task CleanExit_FiresStreamEnded_WithClosedByViewer()
    {
        var launcher = new FakeLauncher();
        await using var supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 4);
        TaskCompletionSource<StreamEndedEventArguments> ended = new();
        supervisor.StreamEnded += (_, arguments) => ended.TrySetResult(arguments);

        await supervisor.StartStreamAsync(1, 0x100, DefaultEncoderOptions(), CancellationToken.None);
        launcher.Launched[0].SimulateClean();

        var observed = await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(StreamStoppedReason.ClosedByViewer, observed.Reason);
    }

    [Fact]
    public async Task StopStream_KillsWorker_FiresEnded()
    {
        var launcher = new FakeLauncher();
        await using var supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 4);
        TaskCompletionSource<StreamEndedEventArguments> ended = new();
        supervisor.StreamEnded += (_, arguments) => ended.TrySetResult(arguments);

        var handle = await supervisor.StartStreamAsync(
            1, 0x100, DefaultEncoderOptions(), CancellationToken.None);
        await supervisor.StopStreamAsync(handle.StreamId);

        var observed = await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(handle.StreamId, observed.StreamId);
    }

    [Fact]
    public async Task GetPipe_KnownStreamId_ReturnsPipe()
    {
        var launcher = new FakeLauncher();
        await using var supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 4);
        var handle = await supervisor.StartStreamAsync(
            1, 0x100, DefaultEncoderOptions(), CancellationToken.None);

        var pipe = supervisor.GetPipe(handle.StreamId);

        Assert.NotNull(pipe);
        Assert.Same(launcher.Launched[0].Pipe, pipe);
    }

    [Fact]
    public async Task GetPipe_UnknownStreamId_ReturnsNull()
    {
        var launcher = new FakeLauncher();
        await using var supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 4);

        var pipe = supervisor.GetPipe(streamId: 9999);

        Assert.Null(pipe);
    }

    [Fact]
    public async Task StartStream_FiresStreamStartedEvent()
    {
        var launcher = new FakeLauncher();
        await using var supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 4);
        TaskCompletionSource<StreamStartedEventArguments> startedSource = new();
        supervisor.StreamStarted += (_, arguments) => startedSource.TrySetResult(arguments);

        await supervisor.StartStreamAsync(
            windowId: 42, hwnd: 0x100, DefaultEncoderOptions(), CancellationToken.None);

        var started = await startedSource.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, started.StreamId);
        Assert.Equal(42UL, started.WindowId);
        Assert.NotNull(started.Pipe);
    }
}
