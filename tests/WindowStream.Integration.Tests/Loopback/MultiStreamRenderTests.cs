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

/// <summary>
/// Verifies that two streams launched on the same coordinator render independently:
/// NAL units injected via one worker's pipe arrive tagged with that stream's id
/// and do not cross-contaminate the other stream's reassembly channel.
/// </summary>
public class MultiStreamRenderTests
{
    // H.264 SPS NAL unit (0x67 = nal_unit_type 7, i.e. IDR-adjacent sequence
    // parameter set). Bytes are structurally valid for the fragmenter/UDP-sender
    // path but are not actually decodable — the test only checks routing, not
    // video quality.
    private static readonly byte[] FakeSpsNalUnit =
    {
        0x00, 0x00, 0x00, 0x01, 0x67,
        0x42, 0xC0, 0x1E, 0xDA, 0x05, 0x82, 0x68, 0x48
    };

    // H.264 IDR slice NAL unit (0x65 = nal_unit_type 5).
    private static readonly byte[] FakeIdrNalUnit =
    {
        0x00, 0x00, 0x00, 0x01, 0x65,
        0x88, 0x84, 0x00, 0x33, 0xFF
    };

    // H.264 non-IDR slice NAL unit (0x41 = nal_unit_type 1).
    private static readonly byte[] FakeNonIdrNalUnit =
    {
        0x00, 0x00, 0x00, 0x01, 0x41,
        0x9A, 0x6C, 0x00, 0x12, 0xFF
    };

    [DesktopAndNvidiaDriverFact]
    public async Task TwoStreams_RenderIndependently()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cancellationToken = cancellation.Token;

        FakeWorkerProcessLauncher launcher = new FakeWorkerProcessLauncher();

        await using CoordinatorLoopbackHarness harness = await CoordinatorLoopbackHarness.StartAsync(
            workerLauncher: launcher,
            cancellationToken: cancellationToken);

        // ------------------------------------------------------------------
        // Step 1: Connect viewer and complete the HELLO/SERVER_HELLO handshake.
        // ------------------------------------------------------------------
        await using FakeViewer viewer = await harness.ConnectViewerAsync(cancellationToken);

        await viewer.SendAsync(
            new HelloMessage(
                ViewerVersion: 2,
                DisplayCapabilities: new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellationToken);

        ControlMessage helloResponse = await viewer.ReceiveAsync(cancellationToken);
        ServerHelloMessage serverHello = Assert.IsType<ServerHelloMessage>(helloResponse);
        Assert.True(serverHello.UdpPort > 0);

        // Send VIEWER_READY so the coordinator knows where to deliver UDP video.
        await viewer.SendAsync(
            new ViewerReadyMessage(ViewerUdpPort: viewer.LocalUdpEndpoint.Port),
            cancellationToken);

        // ------------------------------------------------------------------
        // Step 2: Inject two fake windows so OPEN_STREAM has targets.
        // ------------------------------------------------------------------
        EncoderOptions encoderOptions = new EncoderOptions(
            widthPixels: 1920,
            heightPixels: 1080,
            framesPerSecond: 30,
            bitrateBitsPerSecond: 8_000_000,
            groupOfPicturesLength: 30,
            safetyKeyframeIntervalSeconds: 2);

        WindowDescriptor windowOne = new WindowDescriptor(
            WindowId: 1,
            Hwnd: 0x100,
            ProcessId: 1001,
            ProcessName: "fake-one.exe",
            Title: "Fake Window One",
            PhysicalWidth: 1920,
            PhysicalHeight: 1080);

        WindowDescriptor windowTwo = new WindowDescriptor(
            WindowId: 2,
            Hwnd: 0x200,
            ProcessId: 1002,
            ProcessName: "fake-two.exe",
            Title: "Fake Window Two",
            PhysicalWidth: 1920,
            PhysicalHeight: 1080);

        harness.InjectWindow(windowOne, hwnd: 0x100, encoderOptions);
        harness.InjectWindow(windowTwo, hwnd: 0x200, encoderOptions);

        // ------------------------------------------------------------------
        // Step 3: Drain the two WINDOW_ADDED messages the coordinator sends
        // upon injection.
        // ------------------------------------------------------------------
        ulong receivedWindowIdOne = 0;
        ulong receivedWindowIdTwo = 0;

        for (int i = 0; i < 2; i++)
        {
            ControlMessage windowMessage = await viewer.ReceiveAsync(cancellationToken);
            WindowAddedMessage windowAdded = Assert.IsType<WindowAddedMessage>(windowMessage);
            if (windowAdded.Window.WindowId == 1)
            {
                receivedWindowIdOne = windowAdded.Window.WindowId;
            }
            else if (windowAdded.Window.WindowId == 2)
            {
                receivedWindowIdTwo = windowAdded.Window.WindowId;
            }
        }

        Assert.Equal(1UL, receivedWindowIdOne);
        Assert.Equal(2UL, receivedWindowIdTwo);

        // ------------------------------------------------------------------
        // Step 4: Open both streams and verify STREAM_STARTED responses.
        // ------------------------------------------------------------------
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellationToken);
        ControlMessage streamStartedResponseOne = await viewer.ReceiveAsync(cancellationToken);
        StreamStartedMessage streamStartedOne = Assert.IsType<StreamStartedMessage>(streamStartedResponseOne);
        Assert.Equal(1UL, streamStartedOne.WindowId);
        int streamIdOne = streamStartedOne.StreamId;

        await viewer.SendAsync(new OpenStreamMessage(WindowId: 2), cancellationToken);
        ControlMessage streamStartedResponseTwo = await viewer.ReceiveAsync(cancellationToken);
        StreamStartedMessage streamStartedTwo = Assert.IsType<StreamStartedMessage>(streamStartedResponseTwo);
        Assert.Equal(2UL, streamStartedTwo.WindowId);
        int streamIdTwo = streamStartedTwo.StreamId;

        Assert.NotEqual(streamIdOne, streamIdTwo);

        // ------------------------------------------------------------------
        // Step 5: Retrieve each fake worker and inject 3 NAL units each.
        // We use a short polling loop because the worker is launched
        // asynchronously by the supervisor.
        // ------------------------------------------------------------------
        FakeWorkerHandle? workerHandleOne = null;
        FakeWorkerHandle? workerHandleTwo = null;

        using CancellationTokenSource pollCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pollCancellation.CancelAfter(TimeSpan.FromSeconds(5));

        while (workerHandleOne is null || workerHandleTwo is null)
        {
            pollCancellation.Token.ThrowIfCancellationRequested();
            workerHandleOne ??= launcher.GetFakeWorker(streamIdOne);
            workerHandleTwo ??= launcher.GetFakeWorker(streamIdTwo);
            if (workerHandleOne is null || workerHandleTwo is null)
            {
                await Task.Delay(10, pollCancellation.Token);
            }
        }

        // Inject: IDR + two non-IDR = 3 NAL units per stream.
        // Stream one: SPS (keyframe=true), IDR (keyframe=true), non-IDR (keyframe=false)
        await WorkerChunkPipe.WriteChunkAsync(
            workerHandleOne.WorkerSidePipe,
            new WorkerChunkFrame(PresentationTimestampMicroseconds: 1_000_000UL, IsKeyframe: true, Payload: FakeSpsNalUnit),
            cancellationToken);

        await WorkerChunkPipe.WriteChunkAsync(
            workerHandleOne.WorkerSidePipe,
            new WorkerChunkFrame(PresentationTimestampMicroseconds: 2_000_000UL, IsKeyframe: true, Payload: FakeIdrNalUnit),
            cancellationToken);

        await WorkerChunkPipe.WriteChunkAsync(
            workerHandleOne.WorkerSidePipe,
            new WorkerChunkFrame(PresentationTimestampMicroseconds: 3_000_000UL, IsKeyframe: false, Payload: FakeNonIdrNalUnit),
            cancellationToken);

        // Stream two: same NAL unit patterns, distinct timestamps.
        await WorkerChunkPipe.WriteChunkAsync(
            workerHandleTwo.WorkerSidePipe,
            new WorkerChunkFrame(PresentationTimestampMicroseconds: 11_000_000UL, IsKeyframe: true, Payload: FakeSpsNalUnit),
            cancellationToken);

        await WorkerChunkPipe.WriteChunkAsync(
            workerHandleTwo.WorkerSidePipe,
            new WorkerChunkFrame(PresentationTimestampMicroseconds: 12_000_000UL, IsKeyframe: true, Payload: FakeIdrNalUnit),
            cancellationToken);

        await WorkerChunkPipe.WriteChunkAsync(
            workerHandleTwo.WorkerSidePipe,
            new WorkerChunkFrame(PresentationTimestampMicroseconds: 13_000_000UL, IsKeyframe: false, Payload: FakeNonIdrNalUnit),
            cancellationToken);

        // ------------------------------------------------------------------
        // Step 6: Assert both streams independently deliver 3 NAL units each,
        // tagged with the correct stream id.
        // ------------------------------------------------------------------
        for (int unitIndex = 0; unitIndex < 3; unitIndex++)
        {
            ReassembledNalUnit unitFromStreamOne =
                await viewer.ReceiveNalUnitAsync(streamIdOne, cancellationToken);
            Assert.Equal((uint)streamIdOne, unitFromStreamOne.StreamId);
        }

        for (int unitIndex = 0; unitIndex < 3; unitIndex++)
        {
            ReassembledNalUnit unitFromStreamTwo =
                await viewer.ReceiveNalUnitAsync(streamIdTwo, cancellationToken);
            Assert.Equal((uint)streamIdTwo, unitFromStreamTwo.StreamId);
        }
    }
}
#endif
