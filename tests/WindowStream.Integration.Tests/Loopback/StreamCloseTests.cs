#if WINDOWS
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;
using WindowStream.Core.Protocol;
using WindowStream.Integration.Tests.Infrastructure;
using Xunit;

namespace WindowStream.Integration.Tests.Loopback;

public class StreamCloseTests
{
    /// <summary>
    /// Verifies that sending CLOSE_STREAM for one active stream stops that stream
    /// (STREAM_STOPPED arrives, fake worker is killed) without disrupting a sibling
    /// stream that continues to produce NAL units.
    /// </summary>
    [DesktopAndNvidiaDriverFact]
    public async Task ClosingOneStream_LeavesSimultaneousSiblingUnaffected()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellationToken = cancellation.Token;

        var fakeWorkerLauncher = new FakeWorkerProcessLauncher();

        await using var harness = await CoordinatorLoopbackHarness.StartAsync(
            workerLauncher: fakeWorkerLauncher,
            cancellationToken: cancellationToken);

        // --- Register two fake windows for OPEN_STREAM to resolve. ---
        var encoderOptions = new EncoderOptions(
            widthPixels: 320,
            heightPixels: 240,
            framesPerSecond: 30,
            bitrateBitsPerSecond: 1_000_000,
            groupOfPicturesLength: 30,
            safetyKeyframeIntervalSeconds: 2);

        var windowOne = new WindowDescriptor(
            WindowId: 1,
            Hwnd: 1001,
            ProcessId: 0,
            ProcessName: "fake1",
            Title: "Fake Window 1",
            PhysicalWidth: 320,
            PhysicalHeight: 240);

        var windowTwo = new WindowDescriptor(
            WindowId: 2,
            Hwnd: 1002,
            ProcessId: 0,
            ProcessName: "fake2",
            Title: "Fake Window 2",
            PhysicalWidth: 320,
            PhysicalHeight: 240);

        harness.InjectWindow(windowOne, hwnd: 1001, encoderOptions);
        harness.InjectWindow(windowTwo, hwnd: 1002, encoderOptions);

        // --- Connect viewer and complete handshake ---
        await using var viewer = await harness.ConnectViewerAsync(cancellationToken);

        await viewer.SendAsync(
            new HelloMessage(
                ViewerVersion: 2,
                DisplayCapabilities: new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellationToken);

        var helloResponse = await viewer.ReceiveAsync(cancellationToken);
        Assert.IsType<ServerHelloMessage>(helloResponse);

        await viewer.SendAsync(
            new ViewerReadyMessage(viewer.LocalUdpEndpoint.Port),
            cancellationToken);

        // --- Open stream for window 1 ---
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellationToken);
        var streamOneStartResponse = await viewer.ReceiveAsync(cancellationToken);
        var streamOneStarted = Assert.IsType<StreamStartedMessage>(streamOneStartResponse);
        var streamIdOne = streamOneStarted.StreamId;

        // --- Open stream for window 2 ---
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 2), cancellationToken);
        var streamTwoStartResponse = await viewer.ReceiveAsync(cancellationToken);
        var streamTwoStarted = Assert.IsType<StreamStartedMessage>(streamTwoStartResponse);
        var streamIdTwo = streamTwoStarted.StreamId;

        Assert.NotEqual(streamIdOne, streamIdTwo);

        // --- Both workers emit one NAL unit each; verify both arrive ---
        var workerOne = fakeWorkerLauncher.GetFakeWorker(streamIdOne)!;
        var workerTwo = fakeWorkerLauncher.GetFakeWorker(streamIdTwo)!;

        var payloadOne = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x41 };
        var payloadTwo = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65 };

        await WorkerChunkPipe.WriteChunkAsync(
            workerOne.WorkerSidePipe,
            new WorkerChunkFrame(PresentationTimestampMicroseconds: 1000, IsKeyframe: true, Payload: payloadOne),
            cancellationToken);

        await WorkerChunkPipe.WriteChunkAsync(
            workerTwo.WorkerSidePipe,
            new WorkerChunkFrame(PresentationTimestampMicroseconds: 2000, IsKeyframe: true, Payload: payloadTwo),
            cancellationToken);

        var nalOne = await viewer.ReceiveNalUnitAsync(streamIdOne, cancellationToken);
        var nalTwo = await viewer.ReceiveNalUnitAsync(streamIdTwo, cancellationToken);
        Assert.Equal((uint)streamIdOne, nalOne.StreamId);
        Assert.Equal((uint)streamIdTwo, nalTwo.StreamId);

        // --- Close stream 1 ---
        await viewer.SendAsync(new CloseStreamMessage(StreamId: streamIdOne), cancellationToken);

        // Assert: STREAM_STOPPED(streamId=1, reason=ClosedByViewer) arrives
        var stoppedMessage = await viewer.ReceiveAsync(cancellationToken);
        var streamStopped = Assert.IsType<StreamStoppedMessage>(stoppedMessage);
        Assert.Equal(streamIdOne, streamStopped.StreamId);
        Assert.Equal(StreamStoppedReason.ClosedByViewer, streamStopped.Reason);

        // Assert: fake worker for stream 1 was killed (WaitForExitAsync completes)
        using var workerExitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exitCode = await workerOne.WaitForExitAsync().WaitAsync(workerExitTimeout.Token);
        Assert.Equal(0, exitCode);

        // --- Worker 2 emits another NAL unit; verify it arrives (sibling unaffected) ---
        var payloadTwoContinued = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x41 };

        await WorkerChunkPipe.WriteChunkAsync(
            workerTwo.WorkerSidePipe,
            new WorkerChunkFrame(PresentationTimestampMicroseconds: 3000, IsKeyframe: false, Payload: payloadTwoContinued),
            cancellationToken);

        var siblingNal = await viewer.ReceiveNalUnitAsync(streamIdTwo, cancellationToken);
        Assert.Equal((uint)streamIdTwo, siblingNal.StreamId);
    }
}
#endif
