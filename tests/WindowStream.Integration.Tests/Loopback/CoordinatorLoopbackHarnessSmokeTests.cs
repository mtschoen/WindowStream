#if WINDOWS
using System;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Protocol;
using WindowStream.Integration.Tests.Infrastructure;
using Xunit;

namespace WindowStream.Integration.Tests.Loopback;

public class CoordinatorLoopbackHarnessSmokeTests
{
    [DesktopAndNvidiaDriverFact]
    public async Task Harness_BootsAndAcceptsViewerHandshake()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // No real workers spawned: this smoke test only verifies the bootstrap
        // path and the HELLO/SERVER_HELLO handshake. The fake launcher won't be
        // invoked because we don't OPEN_STREAM here.
        FakeWorkerProcessLauncher fakeWorkerLauncher = new FakeWorkerProcessLauncher();

        await using CoordinatorLoopbackHarness harness = await CoordinatorLoopbackHarness.StartAsync(
            workerLauncher: fakeWorkerLauncher,
            cancellationToken: cancellation.Token);

        await using FakeViewer viewer = await harness.ConnectViewerAsync(cancellation.Token);

        await viewer.SendAsync(
            new HelloMessage(
                ViewerVersion: 2,
                DisplayCapabilities: new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellation.Token);

        ControlMessage helloResponse = await viewer.ReceiveAsync(cancellation.Token);
        ServerHelloMessage serverHello = Assert.IsType<ServerHelloMessage>(helloResponse);
        Assert.Equal(2, serverHello.ServerVersion);
        Assert.True(serverHello.UdpPort > 0);
    }
}
#endif
