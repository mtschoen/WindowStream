using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;
using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Hosting;

public class WorkerSupervisorTests
{
    private static EncoderOptions DefaultEncoderOptions()
        => new EncoderOptions(800, 600, 30, 4_000_000, 30, 1);

    private sealed class FakeWorkerHandle : IWorkerHandle
    {
        private readonly TaskCompletionSource<int> exitSource = new();

        public FakeWorkerHandle(Stream pipe)
        {
            Pipe = pipe;
        }

        public Stream Pipe { get; }

        public Task<int> WaitForExitAsync() => exitSource.Task;

        public void Kill() => exitSource.TrySetResult(137);

        public ValueTask DisposeAsync()
        {
            Kill();
            return ValueTask.CompletedTask;
        }

        public void SimulateClean() => exitSource.TrySetResult(0);

        public void SimulateEncoderFailure() => exitSource.TrySetResult(1);
    }

    private sealed class FakeLauncher : IWorkerProcessLauncher
    {
        public List<FakeWorkerHandle> Launched { get; } = new();

        public Task<IWorkerHandle> LaunchAsync(WorkerLaunchArguments arguments, CancellationToken cancellationToken)
        {
            FakeWorkerHandle handle = new FakeWorkerHandle(new MemoryStream());
            Launched.Add(handle);
            return Task.FromResult<IWorkerHandle>(handle);
        }
    }

    [Fact]
    public async Task StartStream_AssignsMonotonicStreamId()
    {
        FakeLauncher launcher = new FakeLauncher();
        WorkerSupervisor supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 4);
        StreamHandle a = await supervisor.StartStreamAsync(
            windowId: 1, hwnd: 0x100, DefaultEncoderOptions(), CancellationToken.None);
        StreamHandle b = await supervisor.StartStreamAsync(
            windowId: 2, hwnd: 0x200, DefaultEncoderOptions(), CancellationToken.None);
        Assert.Equal(1, a.StreamId);
        Assert.Equal(2, b.StreamId);
    }

    [Fact]
    public async Task StartStream_RefusesPastCapacity()
    {
        FakeLauncher launcher = new FakeLauncher();
        WorkerSupervisor supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 1);
        await supervisor.StartStreamAsync(1, 0x100, DefaultEncoderOptions(), CancellationToken.None);
        await Assert.ThrowsAsync<EncoderCapacityException>(
            () => supervisor.StartStreamAsync(2, 0x200, DefaultEncoderOptions(), CancellationToken.None));
    }

    [Fact]
    public async Task UnexpectedExit_FiresStreamEnded_WithEncoderFailed()
    {
        FakeLauncher launcher = new FakeLauncher();
        WorkerSupervisor supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 4);
        TaskCompletionSource<StreamEndedEventArguments> ended = new();
        supervisor.StreamEnded += (_, arguments) => ended.TrySetResult(arguments);

        StreamHandle handle = await supervisor.StartStreamAsync(
            1, 0x100, DefaultEncoderOptions(), CancellationToken.None);
        launcher.Launched[0].SimulateEncoderFailure();

        StreamEndedEventArguments observed = await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(handle.StreamId, observed.StreamId);
        Assert.Equal(StreamStoppedReason.EncoderFailed, observed.Reason);
    }

    [Fact]
    public async Task CleanExit_FiresStreamEnded_WithClosedByViewer()
    {
        FakeLauncher launcher = new FakeLauncher();
        WorkerSupervisor supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 4);
        TaskCompletionSource<StreamEndedEventArguments> ended = new();
        supervisor.StreamEnded += (_, arguments) => ended.TrySetResult(arguments);

        await supervisor.StartStreamAsync(1, 0x100, DefaultEncoderOptions(), CancellationToken.None);
        launcher.Launched[0].SimulateClean();

        StreamEndedEventArguments observed = await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(StreamStoppedReason.ClosedByViewer, observed.Reason);
    }

    [Fact]
    public async Task StopStream_KillsWorker_FiresEnded()
    {
        FakeLauncher launcher = new FakeLauncher();
        WorkerSupervisor supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 4);
        TaskCompletionSource<StreamEndedEventArguments> ended = new();
        supervisor.StreamEnded += (_, arguments) => ended.TrySetResult(arguments);

        StreamHandle handle = await supervisor.StartStreamAsync(
            1, 0x100, DefaultEncoderOptions(), CancellationToken.None);
        await supervisor.StopStreamAsync(handle.StreamId);

        StreamEndedEventArguments observed = await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(handle.StreamId, observed.StreamId);
    }
}
