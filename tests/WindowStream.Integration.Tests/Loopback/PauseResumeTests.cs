#if WINDOWS
using System;
using System.IO;
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
/// Verifies that a PAUSE_STREAM control message causes the coordinator to forward
/// <see cref="WorkerCommandTag.Pause"/> to the worker pipe, and that a subsequent
/// RESUME_STREAM message forwards <see cref="WorkerCommandTag.Resume"/>. Also
/// verifies that a post-resume keyframe chunk injected by the fake worker arrives
/// at the viewer with <see cref="ReassembledNalUnit.IsIdrFrame"/> set to
/// <see langword="true"/>.
/// </summary>
public sealed class PauseResumeTests
{
    private const int StreamId = 1;

    [DesktopAndNvidiaDriverFact]
    public async Task PauseForwardsCommandToWorker_AndResumeForwardsCommand_AndKeyframeArrivesAtViewer()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        FakeWorkerProcessLauncher fakeWorkerLauncher = new FakeWorkerProcessLauncher();

        await using CoordinatorLoopbackHarness harness = await CoordinatorLoopbackHarness.StartAsync(
            workerLauncher: fakeWorkerLauncher,
            cancellationToken: cancellation.Token);

        await using FakeViewer viewer = await harness.ConnectViewerAsync(cancellation.Token);

        // ── Step 1: handshake ────────────────────────────────────────────────
        await viewer.SendAsync(
            new HelloMessage(
                ViewerVersion: 2,
                DisplayCapabilities: new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellation.Token);

        ControlMessage helloResponse = await viewer.ReceiveAsync(cancellation.Token);
        ServerHelloMessage serverHello = Assert.IsType<ServerHelloMessage>(helloResponse);
        Assert.True(serverHello.UdpPort > 0);

        // ── Step 2: register the viewer's UDP endpoint ───────────────────────
        await viewer.SendAsync(
            new ViewerReadyMessage(viewer.LocalUdpEndpoint.Port),
            cancellation.Token);

        // ── Step 3: inject a fake window ─────────────────────────────────────
        ulong windowId = 42UL;
        WindowDescriptor windowDescriptor = new WindowDescriptor(
            WindowId: windowId,
            Hwnd: 0x1234L,
            ProcessId: 0,
            ProcessName: "fake",
            Title: "Fake Window",
            PhysicalWidth: 640,
            PhysicalHeight: 480);
        EncoderOptions encoderOptions = new EncoderOptions(
            widthPixels: 640,
            heightPixels: 480,
            framesPerSecond: 30,
            bitrateBitsPerSecond: 4_000_000,
            groupOfPicturesLength: 30,
            safetyKeyframeIntervalSeconds: 1);
        harness.InjectWindow(windowDescriptor, hwnd: 0x1234L, encoderOptions);

        // ── Step 4: open a stream ────────────────────────────────────────────
        // InjectWindow fires a WINDOW_ADDED notification to the connected viewer.
        // Drain any such interstitial messages (WindowAddedMessage, etc.) before
        // looking for STREAM_STARTED.
        await viewer.SendAsync(new OpenStreamMessage(windowId), cancellation.Token);

        StreamStartedMessage? streamStarted = null;
        for (int attempt = 0; attempt < 10 && streamStarted is null; attempt++)
        {
            ControlMessage incoming = await viewer.ReceiveAsync(cancellation.Token);
            streamStarted = incoming as StreamStartedMessage;
        }
        Assert.NotNull(streamStarted);
        Assert.Equal(windowId, streamStarted.WindowId);
        int actualStreamId = streamStarted.StreamId;

        // Obtain the fake worker's test-side pipe now that the stream is open.
        FakeWorkerHandle? fakeWorker = null;
        for (int attempt = 0; attempt < 20 && fakeWorker is null; attempt++)
        {
            fakeWorker = fakeWorkerLauncher.GetFakeWorker(actualStreamId);
            if (fakeWorker is null)
            {
                await Task.Delay(25, cancellation.Token);
            }
        }
        Assert.NotNull(fakeWorker);
        Stream workerSidePipe = fakeWorker!.WorkerSidePipe;

        // ── Step 5: emit one IDR + one non-IDR so the viewer has baseline data ─
        byte[] fakeIdrPayload = new byte[] { 0x65, 0x88, 0x01 }; // NAL type 5 = IDR slice
        byte[] fakePPayload = new byte[] { 0x41, 0x9A, 0x02 };   // NAL type 1 = non-IDR slice

        await WorkerChunkPipe.WriteChunkAsync(
            workerSidePipe,
            new WorkerChunkFrame(PresentationTimestampMicroseconds: 0UL, IsKeyframe: true, Payload: fakeIdrPayload),
            cancellation.Token);
        ReassembledNalUnit idrUnit = await viewer.ReceiveNalUnitAsync(actualStreamId, cancellation.Token);
        Assert.True(idrUnit.IsIdrFrame, "first chunk should be marked as IDR");

        await WorkerChunkPipe.WriteChunkAsync(
            workerSidePipe,
            new WorkerChunkFrame(PresentationTimestampMicroseconds: 33_333UL, IsKeyframe: false, Payload: fakePPayload),
            cancellation.Token);
        ReassembledNalUnit nonIdrUnit = await viewer.ReceiveNalUnitAsync(actualStreamId, cancellation.Token);
        Assert.False(nonIdrUnit.IsIdrFrame, "second chunk should not be marked as IDR");

        // ── Step 6: pause — verify the Pause command reaches the worker pipe ──
        await viewer.SendAsync(new PauseStreamMessage(actualStreamId), cancellation.Token);

        WorkerCommandFrame pauseCommand = await WorkerChunkPipe.ReadCommandAsync(workerSidePipe, cancellation.Token);
        Assert.Equal(WorkerCommandTag.Pause, pauseCommand.Tag);

        // ── Step 7: resume — verify the Resume command reaches the worker pipe ─
        await viewer.SendAsync(new ResumeStreamMessage(actualStreamId), cancellation.Token);

        WorkerCommandFrame resumeCommand = await WorkerChunkPipe.ReadCommandAsync(workerSidePipe, cancellation.Token);
        Assert.Equal(WorkerCommandTag.Resume, resumeCommand.Tag);

        // ── Step 8: worker emits an IDR chunk to simulate the post-resume keyframe ─
        // On the real worker side, WorkerCommandHandler.ExecuteAsync calls
        // encoder.RequestKeyframe() on Resume, which causes the next encoded NAL to
        // be an IDR frame. We simulate that here by injecting an IDR-flagged chunk.
        byte[] postResumeIdrPayload = new byte[] { 0x65, 0x88, 0x03 }; // IDR slice
        await WorkerChunkPipe.WriteChunkAsync(
            workerSidePipe,
            new WorkerChunkFrame(PresentationTimestampMicroseconds: 66_666UL, IsKeyframe: true, Payload: postResumeIdrPayload),
            cancellation.Token);

        ReassembledNalUnit postResumeUnit = await viewer.ReceiveNalUnitAsync(actualStreamId, cancellation.Token);
        Assert.True(postResumeUnit.IsIdrFrame, "post-resume chunk should be marked as IDR (keyframe)");
    }
}
#endif
