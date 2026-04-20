using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Encode;
using WindowStream.Core.Protocol;
using WindowStream.Core.Session;
using WindowStream.Core.Session.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Session;

public sealed class SessionHostTests
{
    private static readonly TimeSpan DefaultTestTimeout = TimeSpan.FromSeconds(10);

    // ── Task 25: single-viewer acceptance and handshake ────────────────────────

    [Fact]
    public async Task Accepts_First_Viewer_And_Completes_Handshake()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using SessionHostTestHarness harness = await SessionHostTestHarness.StartAsync(cancellation.Token);

        await using FakeViewerEndpoint viewer = harness.ConnectViewer();
        await viewer.SendAsync(
            new HelloMessage(ViewerVersion: 1, DisplayCapabilities: new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellation.Token);

        ServerHelloMessage serverHello = await viewer.ReceiveAsync<ServerHelloMessage>(cancellation.Token);

        Assert.Equal(1, serverHello.ServerVersion);
        Assert.NotNull(serverHello.ActiveStream);
        Assert.Equal(harness.UdpPort, serverHello.ActiveStream!.UdpPort);
        Assert.Equal(320, serverHello.ActiveStream.Width);
        Assert.Equal(240, serverHello.ActiveStream.Height);
        Assert.Equal("h264", serverHello.ActiveStream.Codec);
    }

    [Fact]
    public async Task Handshake_Requests_Keyframe_From_Encoder()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using SessionHostTestHarness harness = await SessionHostTestHarness.StartAsync(cancellation.Token);

        await using FakeViewerEndpoint viewer = harness.ConnectViewer();
        await viewer.SendAsync(
            new HelloMessage(1, new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellation.Token);
        _ = await viewer.ReceiveAsync<ServerHelloMessage>(cancellation.Token);

        await PollUntilAsync(() => harness.Encoder.KeyframeRequestCount > 0, cancellation.Token);
        Assert.True(harness.Encoder.KeyframeRequestCount > 0);
    }

    // ── Task 26: reject second viewer with VIEWER_BUSY ──────────────────────────

    [Fact]
    public async Task Rejects_Second_Viewer_With_Viewer_Busy()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using SessionHostTestHarness harness = await SessionHostTestHarness.StartAsync(cancellation.Token);

        await using FakeViewerEndpoint firstViewer = harness.ConnectViewer();
        await firstViewer.SendAsync(
            new HelloMessage(1, new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellation.Token);
        _ = await firstViewer.ReceiveAsync<ServerHelloMessage>(cancellation.Token);

        await using FakeViewerEndpoint secondViewer = harness.ConnectViewer();
        ControlMessage response = await secondViewer.ReceiveAsync(cancellation.Token);

        ErrorMessage error = Assert.IsType<ErrorMessage>(response);
        Assert.Equal(ProtocolErrorCode.ViewerBusy, error.Code);
    }

    // ── Task 27: REQUEST_KEYFRAME forwarded to encoder ──────────────────────────

    [Fact]
    public async Task Request_Keyframe_Forwards_To_Encoder()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using SessionHostTestHarness harness = await SessionHostTestHarness.StartAsync(cancellation.Token);

        await using FakeViewerEndpoint viewer = harness.ConnectViewer();
        await viewer.SendAsync(
            new HelloMessage(1, new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellation.Token);
        _ = await viewer.ReceiveAsync<ServerHelloMessage>(cancellation.Token);

        int countBeforeRequest = harness.Encoder.KeyframeRequestCount;
        await viewer.SendAsync(new RequestKeyframeMessage(StreamId: 1), cancellation.Token);

        await PollUntilAsync(() => harness.Encoder.KeyframeRequestCount > countBeforeRequest, cancellation.Token);
        Assert.True(harness.Encoder.KeyframeRequestCount > countBeforeRequest);
    }

    // ── Task 28: heartbeat send + timeout teardown ──────────────────────────────

    [Fact]
    public async Task Sends_Heartbeat_At_Configured_Interval()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using SessionHostTestHarness harness = await SessionHostTestHarness.StartAsync(
            cancellation.Token,
            heartbeatIntervalMilliseconds: 100,
            heartbeatTimeoutMilliseconds: 10_000);

        await using FakeViewerEndpoint viewer = harness.ConnectViewer();
        await viewer.SendAsync(
            new HelloMessage(1, new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellation.Token);
        _ = await viewer.ReceiveAsync<ServerHelloMessage>(cancellation.Token);

        HeartbeatMessage first = await viewer.ReceiveAsync<HeartbeatMessage>(cancellation.Token);
        HeartbeatMessage second = await viewer.ReceiveAsync<HeartbeatMessage>(cancellation.Token);

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Tears_Down_Viewer_On_Heartbeat_Timeout()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using SessionHostTestHarness harness = await SessionHostTestHarness.StartAsync(
            cancellation.Token,
            heartbeatIntervalMilliseconds: 50,
            heartbeatTimeoutMilliseconds: 150);

        await using FakeViewerEndpoint viewer = harness.ConnectViewer();
        await viewer.SendAsync(
            new HelloMessage(1, new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellation.Token);
        _ = await viewer.ReceiveAsync<ServerHelloMessage>(cancellation.Token);

        // Drain heartbeats until the pipe closes (EndOfStreamException means server disconnected us).
        await Assert.ThrowsAsync<System.IO.EndOfStreamException>(async () =>
        {
            while (true)
            {
                await viewer.ReceiveAsync<HeartbeatMessage>(cancellation.Token);
            }
        });
    }

    // ── Task 29: encoded chunks flow to UDP sender ──────────────────────────────

    [Fact]
    public async Task Encoded_Chunks_Are_Forwarded_To_Udp_Sender_When_Viewer_Connected()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using SessionHostTestHarness harness = await SessionHostTestHarness.StartAsync(cancellation.Token);

        // Connect viewer so SessionHost sets activeViewerEndpoint.
        await using FakeViewerEndpoint viewer = harness.ConnectViewer();
        await viewer.SendAsync(
            new HelloMessage(1, new DisplayCapabilities(1920, 1080, new[] { "h264" })),
            cancellation.Token);
        _ = await viewer.ReceiveAsync<ServerHelloMessage>(cancellation.Token);

        // Tell SessionHost where to send UDP packets.
        harness.Host.RegisterViewerEndpoint(new IPEndPoint(IPAddress.Loopback, 55000));

        // Emit a frame into capture, which encodes, which fragments to UDP.
        byte[] pixels = new byte[320 * 240 * 4];
        CapturedFrame frame = new CapturedFrame(320, 240, 320 * 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, pixels);
        harness.CaptureSource.EnqueueFrame(harness.TargetWindow, frame);

        await PollUntilAsync(() => harness.UdpSender.SentPacketCount > 0, cancellation.Token);
        Assert.True(harness.UdpSender.SentPacketCount > 0);
    }

    // ── Task 30: teardown cancels all pumps cleanly ─────────────────────────────

    [Fact]
    public async Task Disposing_Host_Stops_Encoder_And_Udp_Sender()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        SessionHostTestHarness harness = await SessionHostTestHarness.StartAsync(cancellation.Token);

        await harness.DisposeAsync();

        Assert.True(harness.Encoder.Stopped);
        Assert.True(harness.UdpSender.Disposed);
    }

    [Fact]
    public async Task Disposing_Host_Twice_Is_Idempotent()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using SessionHostTestHarness harness = await SessionHostTestHarness.StartAsync(cancellation.Token);
        await harness.DisposeAsync();
        // Second dispose should not throw.
        await harness.DisposeAsync();
    }

    // ── Non-HELLO first message sends error ────────────────────────────────────

    [Fact]
    public async Task Non_Hello_First_Message_Sends_Error()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(DefaultTestTimeout);
        await using SessionHostTestHarness harness = await SessionHostTestHarness.StartAsync(cancellation.Token);

        await using FakeViewerEndpoint viewer = harness.ConnectViewer();
        await viewer.SendAsync(new RequestKeyframeMessage(StreamId: 1), cancellation.Token);

        ControlMessage response = await viewer.ReceiveAsync(cancellation.Token);
        ErrorMessage error = Assert.IsType<ErrorMessage>(response);
        Assert.Equal(ProtocolErrorCode.MalformedMessage, error.Code);
    }

    // ── Helper ──────────────────────────────────────────────────────────────────

    private static async Task PollUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }
}
