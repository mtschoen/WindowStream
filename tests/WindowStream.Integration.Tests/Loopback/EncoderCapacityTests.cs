#if WINDOWS
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Encode;
using WindowStream.Core.Protocol;
using WindowStream.Integration.Tests.Infrastructure;
using Xunit;

namespace WindowStream.Integration.Tests.Loopback;

public class EncoderCapacityTests
{
    [DesktopAndNvidiaDriverFact]
    public async Task OpenStream_WhenAtCapacity_RespondsWithEncoderCapacityError()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        FakeWorkerProcessLauncher fakeWorkerLauncher = new FakeWorkerProcessLauncher();

        await using CoordinatorLoopbackHarness harness = await CoordinatorLoopbackHarness.StartAsync(
            maximumConcurrentStreams: 1,
            workerLauncher: fakeWorkerLauncher,
            cancellationToken: cancellation.Token);

        await using FakeViewer viewer = await harness.ConnectViewerAsync(cancellation.Token);

        // Complete the handshake.
        await viewer.SendAsync(
            new HelloMessage(
                ViewerVersion: 2,
                DisplayCapabilities: new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellation.Token);

        ControlMessage helloResponse = await viewer.ReceiveAsync(cancellation.Token);
        Assert.IsType<ServerHelloMessage>(helloResponse);

        // Inject two fake windows so the coordinator can resolve both OPEN_STREAM requests.
        EncoderOptions encoderOptions = new EncoderOptions(
            widthPixels: 1920,
            heightPixels: 1080,
            framesPerSecond: 30,
            bitrateBitsPerSecond: 8_000_000,
            groupOfPicturesLength: 30,
            safetyKeyframeIntervalSeconds: 2);

        WindowDescriptor window1 = new WindowDescriptor(
            WindowId: 1,
            Hwnd: 1001,
            ProcessId: 0,
            ProcessName: "fake1.exe",
            Title: "Fake Window 1",
            PhysicalWidth: 1920,
            PhysicalHeight: 1080);

        WindowDescriptor window2 = new WindowDescriptor(
            WindowId: 2,
            Hwnd: 1002,
            ProcessId: 0,
            ProcessName: "fake2.exe",
            Title: "Fake Window 2",
            PhysicalWidth: 1920,
            PhysicalHeight: 1080);

        harness.InjectWindow(window1, hwnd: 1001, encoderOptions);
        harness.InjectWindow(window2, hwnd: 1002, encoderOptions);

        // Open the first stream (windowId=1). It should succeed.
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 1), cancellation.Token);
        StreamStartedMessage streamStarted = Assert.IsType<StreamStartedMessage>(
            await viewer.ReceiveAsync(cancellation.Token));
        int firstStreamId = streamStarted.StreamId;

        // Attempt a second stream (windowId=2). The coordinator is at capacity.
        await viewer.SendAsync(new OpenStreamMessage(WindowId: 2), cancellation.Token);
        ErrorMessage error = Assert.IsType<ErrorMessage>(
            await viewer.ReceiveAsync(cancellation.Token));
        Assert.Equal(ProtocolErrorCode.EncoderCapacity, error.Code);

        // The first stream must still be alive.
        Stream? firstStreamPipe = harness.Supervisor.GetPipe(firstStreamId);
        Assert.NotNull(firstStreamPipe);
    }
}
#endif
