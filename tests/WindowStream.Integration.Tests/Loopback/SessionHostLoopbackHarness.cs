#if WINDOWS
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Encode;
using WindowStream.Core.Protocol;
using WindowStream.Core.Session;
using WindowStream.Core.Transport;
using WindowStream.Core.Session.Adapters;
using WindowStream.Integration.Tests.Support;

namespace WindowStream.Integration.Tests.Loopback;

/// <summary>
/// Builds a real <see cref="SessionHost"/> in-process (WGC + NVENC + TCP + UDP) and
/// connects a viewer-side test harness via loopback sockets.
///
/// The harness performs the HELLO / SERVER_HELLO handshake on behalf of the viewer,
/// receives fragmented UDP datagrams, reassembles NAL units, and software-decodes
/// them with FFmpeg. Decoded frame dimensions are surfaced via
/// <see cref="DecodedFrames"/>.
///
/// Usage:
/// <code>
/// await using var harness = await SessionHostLoopbackHarness.CreateAndStartAsync(notepadHandle, timeout);
/// await foreach (var frame in harness.DecodedFrames.WithCancellation(ct)) { ... }
/// </code>
/// </summary>
internal sealed class SessionHostLoopbackHarness : IAsyncDisposable
{
    private readonly SessionHost sessionHost;
    private readonly TcpClient viewerTcpClient;
    private readonly UdpClient viewerUdpReceiver;
    private readonly SoftwareDecoder softwareDecoder;
    private readonly Channel<DecodedVideoFrame> decodedFrameChannel =
        Channel.CreateUnbounded<DecodedVideoFrame>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly CancellationTokenSource pumpCancellation = new CancellationTokenSource();
    private bool disposed;

    public ActiveStreamInformation StreamDescriptor { get; private set; } = null!;
    public (int width, int height) ComputedEncoderSize { get; private set; }
    public int UdpPacketsReceived { get; private set; }
    public int NalUnitsReassembled { get; private set; }
    public int FramesDecoded { get; private set; }
    public string? PumpErrorMessage { get; private set; }

    public IAsyncEnumerable<DecodedVideoFrame> DecodedFrames =>
        decodedFrameChannel.Reader.ReadAllAsync();

    private SessionHostLoopbackHarness(
        SessionHost sessionHost,
        TcpClient viewerTcpClient,
        UdpClient viewerUdpReceiver,
        SoftwareDecoder softwareDecoder)
    {
        this.sessionHost = sessionHost;
        this.viewerTcpClient = viewerTcpClient;
        this.viewerUdpReceiver = viewerUdpReceiver;
        this.softwareDecoder = softwareDecoder;
    }

    /// <summary>
    /// Creates a fully wired harness targeting the specified window handle.
    /// Waits up to <paramref name="handshakeTimeout"/> for the server to accept the
    /// viewer TCP connection and complete the HELLO / SERVER_HELLO exchange.
    /// </summary>
    internal static async Task<SessionHostLoopbackHarness> CreateAndStartAsync(
        WindowHandle windowHandle,
        TimeSpan handshakeTimeout)
    {
        // Build real infrastructure adapters on loopback.
        TcpConnectionAcceptorAdapter tcpAcceptor = new TcpConnectionAcceptorAdapter(TimeProvider.System);
        UdpVideoSenderAdapter udpSender = new UdpVideoSenderAdapter();

        // Configure the encoder to match the actual physical (DPI-scaled) window dimensions
        // so that the sws_scale context matches the frames delivered by WGC.
        // WindowInformation.widthPixels/heightPixels use logical pixels (GetWindowRect),
        // but WGC captures at physical pixels — we must account for DPI scaling.
        // Both dimensions must be aligned to 2 pixels for NV12 subsampling.
        (int encoderWidth, int encoderHeight) = GetPhysicalWindowSize(windowHandle);

        // Use a small GOP and high bitrate so the encoder does not buffer frames.
        // GOP=2 gives IDR + P pattern, which forces frequent IDRs without the
        // NVENC input-queue EAGAIN that occurs with larger GOPs at high resolutions.
        EncoderOptions encoderOptions = new EncoderOptions(
            widthPixels: encoderWidth,
            heightPixels: encoderHeight,
            framesPerSecond: 30,
            bitrateBitsPerSecond: 20_000_000,
            groupOfPicturesLength: 2,
            safetyKeyframeIntervalSeconds: 1);

        SessionHostOptions hostOptions = new SessionHostOptions(
            HeartbeatIntervalMilliseconds: 2000,
            HeartbeatTimeoutMilliseconds: 10000,
            ServerVersion: 1,
            StreamId: 1,
            Codec: "h264");

        WgcCaptureSource captureSource = new WgcCaptureSource();
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();

        SessionHost sessionHost = new SessionHost(
            options: hostOptions,
            captureSource: captureSource,
            videoEncoder: encoder,
            tcpAcceptor: tcpAcceptor,
            udpSender: udpSender,
            timeProvider: TimeProvider.System);

        // Start the session host with port 0 so the OS picks TCP and UDP ports.
        CaptureOptions captureOptions = new CaptureOptions(targetFramesPerSecond: 30, includeCursor: false);
        await sessionHost.StartAsync(
            window: windowHandle,
            capture: captureOptions,
            encoder: encoderOptions,
            udpLocalEndpoint: new IPEndPoint(IPAddress.Loopback, 0),
            tcpPort: 0,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        int tcpPort = sessionHost.TcpPort;

        // Open the viewer-side UDP receiver on a separate loopback port.
        UdpClient viewerUdpReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int viewerUdpPort = ((IPEndPoint)viewerUdpReceiver.Client.LocalEndPoint!).Port;

        // Connect the viewer TCP socket to the session host.
        TcpClient viewerTcpClient = new TcpClient();
        using CancellationTokenSource connectTimeout = new CancellationTokenSource(handshakeTimeout);
        await viewerTcpClient.ConnectAsync(IPAddress.Loopback, tcpPort, connectTimeout.Token)
            .ConfigureAwait(false);

        // Perform the HELLO / SERVER_HELLO handshake.
        System.Net.Sockets.NetworkStream tcpStream = viewerTcpClient.GetStream();
        DisplayCapabilities capabilities = new DisplayCapabilities(
            MaximumWidth: 1920,
            MaximumHeight: 1080,
            SupportedCodecs: new[] { "h264" });
        HelloMessage hello = new HelloMessage(ViewerVersion: 1, DisplayCapabilities: capabilities);
        string helloJson = ControlMessageSerialization.Serialize(hello);
        byte[] helloPayload = Encoding.UTF8.GetBytes(helloJson);
        await LengthPrefixFraming.WriteFrameAsync(tcpStream, helloPayload, connectTimeout.Token)
            .ConfigureAwait(false);

        byte[] serverHelloBytes = await LengthPrefixFraming.ReadFrameAsync(tcpStream, connectTimeout.Token)
            .ConfigureAwait(false);
        string serverHelloJson = Encoding.UTF8.GetString(serverHelloBytes);
        ControlMessage serverHelloMessage = ControlMessageSerialization.Deserialize(serverHelloJson);
        if (serverHelloMessage is not ServerHelloMessage serverHello)
        {
            throw new InvalidOperationException($"Expected ServerHelloMessage but got {serverHelloMessage.GetType().Name}");
        }
        ActiveStreamInformation streamDescriptor =
            serverHello.ActiveStream
            ?? throw new InvalidOperationException("ServerHelloMessage carried no ActiveStream.");

        // Tell the session host where to send UDP datagrams (the viewer's UDP receiver).
        sessionHost.RegisterViewerEndpoint(new IPEndPoint(IPAddress.Loopback, viewerUdpPort));

        // Brief pause to ensure the endpoint is registered before requesting an IDR,
        // preventing a race where the IDR packet is sent to a null destination.
        await Task.Delay(100, connectTimeout.Token).ConfigureAwait(false);

        // Request an immediate IDR so we don't have to wait for the GOP boundary.
        // Send two requests to maximise the chance one is processed while the endpoint is set.
        RequestKeyframeMessage keyframeRequest = new RequestKeyframeMessage(streamDescriptor.StreamId);
        string keyframeJson = ControlMessageSerialization.Serialize(keyframeRequest);
        byte[] keyframePayload = Encoding.UTF8.GetBytes(keyframeJson);
        await LengthPrefixFraming.WriteFrameAsync(tcpStream, keyframePayload, connectTimeout.Token)
            .ConfigureAwait(false);
        await LengthPrefixFraming.WriteFrameAsync(tcpStream, keyframePayload, connectTimeout.Token)
            .ConfigureAwait(false);

        SoftwareDecoder softwareDecoder = new SoftwareDecoder();
        SessionHostLoopbackHarness harness = new SessionHostLoopbackHarness(
            sessionHost, viewerTcpClient, viewerUdpReceiver, softwareDecoder);
        harness.StreamDescriptor = streamDescriptor;
        harness.ComputedEncoderSize = (encoderWidth, encoderHeight);

        // Start the UDP reassembly + decode pump in the background.
        _ = Task.Run(() => harness.RunUdpPumpAsync(harness.pumpCancellation.Token));

        // Start the TCP receive + heartbeat echo pump. The server sends HeartbeatMessages
        // and expects the viewer to echo them back within HeartbeatTimeoutMilliseconds.
        // Failing to do so causes the server to close the channel and stop sending UDP.
        _ = Task.Run(() => harness.RunTcpHeartbeatPumpAsync(tcpStream, harness.pumpCancellation.Token));

        return harness;
    }

    /// <summary>
    /// Reads incoming messages on the TCP control channel and echoes
    /// <see cref="HeartbeatMessage"/> instances back so the server's heartbeat
    /// watchdog does not time out and close the channel.
    /// </summary>
    private async Task RunTcpHeartbeatPumpAsync(
        System.Net.Sockets.NetworkStream tcpStream,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] frameBytes = await LengthPrefixFraming.ReadFrameAsync(tcpStream, cancellationToken)
                    .ConfigureAwait(false);
                string json = System.Text.Encoding.UTF8.GetString(frameBytes);
                ControlMessage message = ControlMessageSerialization.Deserialize(json);
                if (message is HeartbeatMessage)
                {
                    // Echo the heartbeat back so the server knows we are still alive.
                    string echoJson = ControlMessageSerialization.Serialize(HeartbeatMessage.Instance);
                    byte[] echoPayload = System.Text.Encoding.UTF8.GetBytes(echoJson);
                    await LengthPrefixFraming.WriteFrameAsync(tcpStream, echoPayload, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (System.IO.EndOfStreamException)
        {
            // Server closed the connection — normal shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Client socket disposed — normal shutdown.
        }
    }

    private async Task RunUdpPumpAsync(CancellationToken cancellationToken)
    {
        NalReassembler reassembler = new NalReassembler(SystemClock.Instance, TimeSpan.FromSeconds(2));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result = await viewerUdpReceiver.ReceiveAsync(cancellationToken)
                    .ConfigureAwait(false);
                byte[] datagram = result.Buffer;
                UdpPacketsReceived++;
                if (datagram.Length < PacketHeader.HeaderByteLength) continue;

                PacketHeader header;
                try
                {
                    header = PacketHeader.Parse(datagram.AsSpan(0, PacketHeader.HeaderByteLength));
                }
                catch (MalformedPacketException)
                {
                    continue;
                }

                byte[] payload = new byte[datagram.Length - PacketHeader.HeaderByteLength];
                Array.Copy(datagram, PacketHeader.HeaderByteLength, payload, 0, payload.Length);

                ReassembledNalUnit? nalUnit = reassembler.Offer(header, payload);
                if (nalUnit is null) continue;

                NalUnitsReassembled++;
                bool isKeyframe = nalUnit.Value.IsIdrFrame;
                bool decoded = softwareDecoder.TryDecode(nalUnit.Value.NalUnit, out int width, out int height);
                if (decoded)
                {
                    FramesDecoded++;
                    await decodedFrameChannel.Writer.WriteAsync(
                        new DecodedVideoFrame(width, height, isKeyframe),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException)
        {
            // UDP client disposed — normal shutdown.
        }
        finally
        {
            decodedFrameChannel.Writer.TryComplete();
        }
    }

    private static int AlignToEven(int value) => value % 2 == 0 ? value : value + 1;

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int left, top, right, bottom; }

    /// <summary>
    /// Returns the physical (DPI-scaled) pixel dimensions of the window.
    /// <see cref="Windows.Core.Capture.Windows.Win32Api.GetWindowSize"/> returns logical pixels;
    /// WGC captures physical pixels.  We need to match the encoder to the physical size.
    /// </summary>
    private static (int widthPixels, int heightPixels) GetPhysicalWindowSize(WindowHandle windowHandle)
    {
        IntPtr hwnd = new IntPtr(windowHandle.value);
        if (!GetWindowRect(hwnd, out NativeRect rect))
        {
            return (640, 360); // safe fallback
        }
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96; // fallback to 100% DPI
        double scale = dpi / 96.0;
        int logicalWidth = rect.right - rect.left;
        int logicalHeight = rect.bottom - rect.top;
        int physicalWidth = (int)Math.Round(logicalWidth * scale);
        int physicalHeight = (int)Math.Round(logicalHeight * scale);
        return (AlignToEven(physicalWidth), AlignToEven(physicalHeight));
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;

        pumpCancellation.Cancel();
        pumpCancellation.Dispose();

        try { viewerTcpClient.Dispose(); } catch { /* best-effort */ }
        try { viewerUdpReceiver.Dispose(); } catch { /* best-effort */ }
        softwareDecoder.Dispose();

        await sessionHost.DisposeAsync().ConfigureAwait(false);
    }
}
#endif
