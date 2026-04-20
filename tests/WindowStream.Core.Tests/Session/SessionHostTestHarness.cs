using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Testing;
using WindowStream.Core.Encode;
using WindowStream.Core.Encode.Testing;
using WindowStream.Core.Session;
using WindowStream.Core.Session.Testing;

namespace WindowStream.Core.Tests.Session;

internal sealed class SessionHostTestHarness : IAsyncDisposable
{
    public SessionHost Host { get; }
    public FakeWindowCaptureSource CaptureSource { get; }
    public FakeVideoEncoder Encoder { get; }
    public FakeUdpVideoSender UdpSender { get; }
    public FakeTcpConnectionAcceptor TcpAcceptor { get; }
    public WindowHandle TargetWindow { get; }
    public int UdpPort => UdpSender.LocalPort;
    public int TcpPort => TcpAcceptor.LocalPort;

    private SessionHostTestHarness(
        SessionHost host,
        FakeWindowCaptureSource captureSource,
        FakeVideoEncoder encoder,
        FakeUdpVideoSender udpSender,
        FakeTcpConnectionAcceptor tcpAcceptor,
        WindowHandle targetWindow)
    {
        Host = host;
        CaptureSource = captureSource;
        Encoder = encoder;
        UdpSender = udpSender;
        TcpAcceptor = tcpAcceptor;
        TargetWindow = targetWindow;
    }

    public static async Task<SessionHostTestHarness> StartAsync(
        CancellationToken cancellationToken,
        int heartbeatIntervalMilliseconds = 2000,
        int heartbeatTimeoutMilliseconds = 6000)
    {
        WindowHandle targetWindow = new WindowHandle(1);
        WindowInformation windowInfo = new WindowInformation(targetWindow, "Test Window", "test.exe", 320, 240);

        FakeWindowCaptureSource captureSource = new FakeWindowCaptureSource(new[] { windowInfo });
        FakeVideoEncoder encoder = new FakeVideoEncoder();
        FakeUdpVideoSender udpSender = new FakeUdpVideoSender();
        FakeTcpConnectionAcceptor tcpAcceptor = new FakeTcpConnectionAcceptor(TimeProvider.System);

        SessionHostOptions options = new SessionHostOptions(
            HeartbeatIntervalMilliseconds: heartbeatIntervalMilliseconds,
            HeartbeatTimeoutMilliseconds: heartbeatTimeoutMilliseconds,
            ServerVersion: 1,
            StreamId: 1,
            Codec: "h264");

        SessionHost host = new SessionHost(
            options,
            captureSource,
            encoder,
            tcpAcceptor,
            udpSender,
            TimeProvider.System);

        CaptureOptions captureOptions = new CaptureOptions(30, false);
        EncoderOptions encoderOptions = new EncoderOptions(320, 240, 30, 2_000_000, 30, 2);

        await host.StartAsync(
            targetWindow,
            captureOptions,
            encoderOptions,
            new IPEndPoint(IPAddress.Loopback, 0),
            0,
            cancellationToken).ConfigureAwait(false);

        return new SessionHostTestHarness(host, captureSource, encoder, udpSender, tcpAcceptor, targetWindow);
    }

    public FakeViewerEndpoint ConnectViewer()
    {
        return TcpAcceptor.EnqueueIncomingConnection();
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
