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
/// Verifies that a worker crash (non-zero exit) delivers a
/// <see cref="StreamStoppedMessage"/> to the viewer for the affected stream while
/// leaving sibling streams fully operational.
/// </summary>
public sealed class WorkerCrashRecoveryTests
{
    /// <summary>
    /// A fake NAL unit payload — arbitrary bytes that the coordinator will
    /// fragment and forward. The test only needs to confirm that the bytes
    /// reach the viewer; their contents do not matter.
    /// </summary>
    private static readonly byte[] FakeNalUnitPayload = new byte[] { 0x00, 0x00, 0x00, 0x01, 0xAB, 0xCD };

    private static EncoderOptions DefaultEncoderOptions(int width = 320, int height = 240)
        => new EncoderOptions(
            widthPixels: width,
            heightPixels: height,
            framesPerSecond: 30,
            bitrateBitsPerSecond: 1_000_000,
            groupOfPicturesLength: 30,
            safetyKeyframeIntervalSeconds: 1);

    /// <summary>
    /// Boots the harness, opens two streams, crashes worker 1, and asserts:
    /// <list type="bullet">
    ///   <item>The viewer receives <c>STREAM_STOPPED{EncoderFailed}</c> for stream 1.</item>
    ///   <item>Stream 2's worker remains live and can inject a NAL unit that the viewer receives.</item>
    ///   <item>The supervisor assigns stream 3 (not 1) when a new stream is opened next.</item>
    /// </list>
    /// </summary>
    [DesktopAndNvidiaDriverFact]
    public async Task WorkerCrash_EmitsStreamStopped_SiblingUnaffected()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        FakeWorkerProcessLauncher launcher = new FakeWorkerProcessLauncher();

        await using CoordinatorLoopbackHarness harness = await CoordinatorLoopbackHarness.StartAsync(
            workerLauncher: launcher,
            cancellationToken: cancellation.Token);

        await using FakeViewer viewer = await harness.ConnectViewerAsync(cancellation.Token);

        // ── Handshake ────────────────────────────────────────────────────────

        await viewer.SendAsync(
            new HelloMessage(
                ViewerVersion: 2,
                DisplayCapabilities: new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellation.Token);

        ServerHelloMessage serverHello =
            Assert.IsType<ServerHelloMessage>(await viewer.ReceiveAsync(cancellation.Token));
        Assert.True(serverHello.UdpPort > 0);

        await viewer.SendAsync(
            new ViewerReadyMessage(viewer.LocalUdpEndpoint.Port),
            cancellation.Token);

        // ── Inject two fake windows ──────────────────────────────────────────

        WindowDescriptor windowOne = new WindowDescriptor(
            WindowId: 101UL,
            Hwnd: 0x1001,
            ProcessId: 0,
            ProcessName: "fake",
            Title: "Window One",
            PhysicalWidth: 320,
            PhysicalHeight: 240);

        WindowDescriptor windowTwo = new WindowDescriptor(
            WindowId: 102UL,
            Hwnd: 0x1002,
            ProcessId: 0,
            ProcessName: "fake",
            Title: "Window Two",
            PhysicalWidth: 320,
            PhysicalHeight: 240);

        harness.InjectWindow(windowOne, hwnd: 0x1001, DefaultEncoderOptions());
        harness.InjectWindow(windowTwo, hwnd: 0x1002, DefaultEncoderOptions());

        // Drain the WINDOW_ADDED notifications so they don't interfere with later
        // ReceiveAsync calls that expect STREAM_STARTED / STREAM_STOPPED.
        ControlMessage addedOne = await viewer.ReceiveAsync(cancellation.Token);
        Assert.IsType<WindowAddedMessage>(addedOne);
        ControlMessage addedTwo = await viewer.ReceiveAsync(cancellation.Token);
        Assert.IsType<WindowAddedMessage>(addedTwo);

        // ── Open both streams ────────────────────────────────────────────────

        await viewer.SendAsync(new OpenStreamMessage(WindowId: 101UL), cancellation.Token);
        StreamStartedMessage startedOne =
            Assert.IsType<StreamStartedMessage>(await viewer.ReceiveAsync(cancellation.Token));
        int streamIdOne = startedOne.StreamId;
        Assert.True(streamIdOne > 0);

        await viewer.SendAsync(new OpenStreamMessage(WindowId: 102UL), cancellation.Token);
        StreamStartedMessage startedTwo =
            Assert.IsType<StreamStartedMessage>(await viewer.ReceiveAsync(cancellation.Token));
        int streamIdTwo = startedTwo.StreamId;
        Assert.True(streamIdTwo > 0);
        Assert.NotEqual(streamIdOne, streamIdTwo);

        // ── Crash worker 1 (exit code 1 → EncoderFailed) ────────────────────

        FakeWorkerHandle workerOne =
            launcher.GetFakeWorker(streamIdOne)
            ?? throw new InvalidOperationException($"No fake worker registered for stream {streamIdOne}.");

        workerOne.SimulateEncoderFailure();

        // ── Assert STREAM_STOPPED arrives for stream 1 ───────────────────────

        StreamStoppedMessage stoppedMessage =
            Assert.IsType<StreamStoppedMessage>(await viewer.ReceiveAsync(cancellation.Token));
        Assert.Equal(streamIdOne, stoppedMessage.StreamId);
        Assert.Equal(StreamStoppedReason.EncoderFailed, stoppedMessage.Reason);

        // ── Assert sibling stream 2 is unaffected ────────────────────────────

        FakeWorkerHandle workerTwo =
            launcher.GetFakeWorker(streamIdTwo)
            ?? throw new InvalidOperationException($"No fake worker registered for stream {streamIdTwo}.");

        WorkerChunkFrame siblingFrame = new WorkerChunkFrame(
            PresentationTimestampMicroseconds: 1_000_000UL,
            IsKeyframe: true,
            Payload: FakeNalUnitPayload);

        await WorkerChunkPipe.WriteChunkAsync(
            workerTwo.WorkerSidePipe, siblingFrame, cancellation.Token);

        ReassembledNalUnit siblingNalUnit =
            await viewer.ReceiveNalUnitAsync(streamIdTwo, cancellation.Token);
        Assert.Equal((uint)streamIdTwo, siblingNalUnit.StreamId);
        Assert.Equal(FakeNalUnitPayload, siblingNalUnit.NalUnit);

        // ── Assert supervisor recycles stream IDs monotonically ──────────────
        // A new OPEN_STREAM should get a stream id greater than both prior ids,
        // confirming stream 1's slot was freed and not reused.

        await viewer.SendAsync(new OpenStreamMessage(WindowId: 101UL), cancellation.Token);
        StreamStartedMessage startedThree =
            Assert.IsType<StreamStartedMessage>(await viewer.ReceiveAsync(cancellation.Token));
        int streamIdThree = startedThree.StreamId;
        Assert.True(streamIdThree > streamIdTwo, $"Expected stream id > {streamIdTwo}, got {streamIdThree}");
        Assert.NotEqual(streamIdOne, streamIdThree);
    }
}
#endif
