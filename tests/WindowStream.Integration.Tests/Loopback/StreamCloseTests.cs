#if WINDOWS
using System;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;
using WindowStream.Core.Protocol;
using WindowStream.Core.Transport;
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
        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken cancellationToken = cancellation.Token;

        FakeWorkerProcessLauncher fakeWorkerLauncher = new FakeWorkerProcessLauncher();

        await using CoordinatorLoopbackHarness harness = await CoordinatorLoopbackHarness.StartAsync(
            workerLauncher: fakeWorkerLauncher,
            cancellationToken: cancellationToken);

        // --- Register two fake windows for OPEN_STREAM to resolve. ---
        EncoderOptions encoderOptions = new EncoderOptions(
            widthPixels: 320,
            heightPixels: 240,
            framesPerSecond: 30,
            bitrateBitsPerSecond: 1_000_000,
            groupOfPicturesLength: 30,
            safetyKeyframeIntervalSeconds: 2);

        WindowDescriptor windowOne = new WindowDescriptor(
            WindowId: 1,
            Hwnd: 1001,
            ProcessId: 0,
            ProcessName: "fake1",
            Title: "Fake Window 1",
            PhysicalWidth: 320,
            PhysicalHeight: 240);

        WindowDescriptor windowTwo = new WindowDescriptor(
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
        await using FakeViewer viewer = await harness.ConnectViewerAsync(cancellationToken);

        await viewer.SendAsync(
            new HelloMessage(
                ViewerVersion: 2,
                DisplayCapabilities: new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellationToken);

        ControlMessage helloResponse = await viewer.ReceiveAsync(cancellationToken);
        Assert.IsType<ServerHelloMessage>(helloResponse);

        await viewer.SendAsync(
            new ViewerReadyMessage(viewer.LocalUdpEndpoint.Port),
            cancellationToken);

        // --- Open stream for window 1 ---
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellationToken);
        ControlMessage streamOneStartResponse = await viewer.ReceiveAsync(cancellationToken);
        StreamStartedMessage streamOneStarted = Assert.IsType<StreamStartedMessage>(streamOneStartResponse);
        int streamIdOne = streamOneStarted.StreamId;

        // --- Open stream for window 2 ---
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 2), cancellationToken);
        ControlMessage streamTwoStartResponse = await viewer.ReceiveAsync(cancellationToken);
        StreamStartedMessage streamTwoStarted = Assert.IsType<StreamStartedMessage>(streamTwoStartResponse);
        int streamIdTwo = streamTwoStarted.StreamId;

        Assert.NotEqual(streamIdOne, streamIdTwo);

        // --- Both workers emit one NAL unit each; verify both arrive ---
        FakeWorkerHandle workerOne = fakeWorkerLauncher.GetFakeWorker(streamIdOne)!;
        FakeWorkerHandle workerTwo = fakeWorkerLauncher.GetFakeWorker(streamIdTwo)!;

        byte[] payloadOne = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x41 };
        byte[] payloadTwo = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65 };

        await WorkerChunkPipe.WriteChunkAsync(
            workerOne.WorkerSidePipe,
            new WorkerChunkFrame(PresentationTimestampMicroseconds: 1000, IsKeyframe: true, Payload: payloadOne),
            cancellationToken);

        await WorkerChunkPipe.WriteChunkAsync(
            workerTwo.WorkerSidePipe,
            new WorkerChunkFrame(PresentationTimestampMicroseconds: 2000, IsKeyframe: true, Payload: payloadTwo),
            cancellationToken);

        ReassembledNalUnit nalOne = await viewer.ReceiveNalUnitAsync(streamIdOne, cancellationToken);
        ReassembledNalUnit nalTwo = await viewer.ReceiveNalUnitAsync(streamIdTwo, cancellationToken);
        Assert.Equal((uint)streamIdOne, nalOne.StreamId);
        Assert.Equal((uint)streamIdTwo, nalTwo.StreamId);

        // --- Close stream 1 ---
        await viewer.SendAsync(new CloseStreamMessage(StreamId: streamIdOne), cancellationToken);

        // Assert: STREAM_STOPPED(streamId=1, reason=ClosedByViewer) arrives
        ControlMessage stoppedMessage = await viewer.ReceiveAsync(cancellationToken);
        StreamStoppedMessage streamStopped = Assert.IsType<StreamStoppedMessage>(stoppedMessage);
        Assert.Equal(streamIdOne, streamStopped.StreamId);
        Assert.Equal(StreamStoppedReason.ClosedByViewer, streamStopped.Reason);

        // Assert: fake worker for stream 1 was killed (WaitForExitAsync completes)
        using CancellationTokenSource workerExitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int exitCode = await workerOne.WaitForExitAsync().WaitAsync(workerExitTimeout.Token);
        Assert.Equal(0, exitCode);

        // --- Worker 2 emits another NAL unit; verify it arrives (sibling unaffected) ---
        byte[] payloadTwoContinued = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x41 };

        await WorkerChunkPipe.WriteChunkAsync(
            workerTwo.WorkerSidePipe,
            new WorkerChunkFrame(PresentationTimestampMicroseconds: 3000, IsKeyframe: false, Payload: payloadTwoContinued),
            cancellationToken);

        ReassembledNalUnit siblingNal = await viewer.ReceiveNalUnitAsync(streamIdTwo, cancellationToken);
        Assert.Equal((uint)streamIdTwo, siblingNal.StreamId);
    }
}
#endif
