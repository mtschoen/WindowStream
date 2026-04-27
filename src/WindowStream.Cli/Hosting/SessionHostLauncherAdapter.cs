#if WINDOWS
using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Discovery;
using WindowStream.Core.Encode;
using WindowStream.Core.Session;
using WindowStream.Core.Session.Adapters;

namespace WindowStream.Cli.Hosting;

/// <summary>
/// Production wiring for <see cref="ISessionHostLauncher"/>.
/// Assembles a <see cref="SessionHost"/> against real WGC capture, FFmpeg NVENC encoder,
/// and TCP/UDP adapters that bind to all interfaces so a LAN viewer (emulator or HMD) can connect.
/// </summary>
public sealed class SessionHostLauncherAdapter : ISessionHostLauncher
{
    private readonly int tcpPort;
    private readonly System.IO.TextWriter output;

    public SessionHostLauncherAdapter(int tcpPort, System.IO.TextWriter output)
    {
        this.tcpPort = tcpPort;
        this.output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public async Task LaunchAsync(WindowHandle handle, CancellationToken cancellationToken)
    {
        // Probe WGC for one frame to learn the exact dimensions it produces —
        // the sws_scale step inside FFmpegNvencEncoder requires src/dst dims
        // to match the capture stream exactly, and GetWindowRect/GetClientRect
        // computations differ by a few pixels from what WGC actually delivers.
        (int probeWidth, int probeHeight) = await ProbeCaptureSizeAsync(handle, cancellationToken).ConfigureAwait(false);
        // NVENC NV12 requires even dimensions. FFmpegNvencEncoder's sws_scale is
        // configured with source == dest dims, so both must be ≤ the actual captured
        // frame's dimensions. Round DOWN — we lose at most one row/column.
        int physicalWidth = probeWidth - (probeWidth % 2);
        int physicalHeight = probeHeight - (probeHeight % 2);
        output.WriteLine($"windowstream: probed {probeWidth}x{probeHeight}, encoding at {physicalWidth}x{physicalHeight}");

        // GOP length is the IDR-keyframe interval (frames between forced keyframes).
        // Default 30 = ~1 keyframe per second at 30fps. Keyframes are 5-10x larger
        // than P-frames; aggressive GOP (e.g. =2 from earlier) packs bitrate into
        // bursts that increase per-frame transit/decode jitter. The safety
        // keyframe path (safetyKeyframeIntervalSeconds=1) still forces a keyframe
        // every second as recovery floor regardless of GOP. Override via
        // WINDOWSTREAM_NVENC_GOP if a source needs faster recovery from packet loss.
        int gopLength = 30;
        string? gopOverride = Environment.GetEnvironmentVariable("WINDOWSTREAM_NVENC_GOP");
        if (gopOverride is not null && int.TryParse(gopOverride, out int parsedGop) && parsedGop >= 1)
        {
            gopLength = parsedGop;
        }

        // Capture & encode framerate. Default 60 cuts the inherent frame-period
        // jitter floor from 33ms to 16ms when the source actually renders that
        // fast (Unity Editor, games). Bandwidth scales linearly so we bump
        // bitrate proportionally to keep per-frame quality. Override via
        // WINDOWSTREAM_NVENC_FPS for sources that don't benefit (text terminals).
        int framesPerSecond = 60;
        string? fpsOverride = Environment.GetEnvironmentVariable("WINDOWSTREAM_NVENC_FPS");
        if (fpsOverride is not null && int.TryParse(fpsOverride, out int parsedFps) && parsedFps >= 1)
        {
            framesPerSecond = parsedFps;
        }
        int bitrateBitsPerSecond = 6_000_000 * framesPerSecond / 30;
        output.WriteLine($"windowstream: gop_size={gopLength} fps={framesPerSecond} bitrate={bitrateBitsPerSecond}");

        EncoderOptions encoderOptions = new EncoderOptions(
            widthPixels: physicalWidth,
            heightPixels: physicalHeight,
            framesPerSecond: framesPerSecond,
            bitrateBitsPerSecond: bitrateBitsPerSecond,
            groupOfPicturesLength: gopLength,
            safetyKeyframeIntervalSeconds: 1);

        SessionHostOptions hostOptions = new SessionHostOptions(
            HeartbeatIntervalMilliseconds: 2000,
            HeartbeatTimeoutMilliseconds: 10000,
            ServerVersion: 1,
            StreamId: 1,
            Codec: "h264");

        WgcCaptureSource captureSource = new WgcCaptureSource();
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();
        TcpConnectionAcceptorAdapter tcpAcceptor = new TcpConnectionAcceptorAdapter(TimeProvider.System);
        UdpVideoSenderAdapter udpSender = new UdpVideoSenderAdapter();

        await using SessionHost sessionHost = new SessionHost(
            options: hostOptions,
            captureSource: captureSource,
            videoEncoder: encoder,
            tcpAcceptor: tcpAcceptor,
            udpSender: udpSender,
            timeProvider: TimeProvider.System);

        CaptureOptions captureOptions = new CaptureOptions(targetFramesPerSecond: framesPerSecond, includeCursor: false);

        await sessionHost.StartAsync(
            window: handle,
            capture: captureOptions,
            encoder: encoderOptions,
            udpLocalEndpoint: new IPEndPoint(IPAddress.Any, 0),
            tcpPort: tcpPort,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Advertise the running server over mDNS so the viewer's
        // NetworkServiceDiscoveryClient can find it without a manually-entered
        // IP. Service type `_windowstream._tcp`, text records per
        // ServiceTextRecords.Build, control port = sessionHost.TcpPort.
        //
        // Include the TCP port in the advertised hostname so multiple server
        // instances on the same machine (multi-window demo) get unique mDNS
        // service instance names. Without this, Android NSD collapses all
        // instances sharing MachineName into a single discovered entry.
        string hostname = $"{Environment.MachineName}-{sessionHost.TcpPort}";
        AdvertisementOptions advertisementOptions = new AdvertisementOptions(
            hostname: hostname,
            protocolMajorVersion: 2,
            protocolRevision: 0);
        await using ServerAdvertiser advertiser = new ServerAdvertiser(new MakaretuMulticastServiceHost());
        await advertiser.StartAsync(advertisementOptions, sessionHost.TcpPort, cancellationToken).ConfigureAwait(false);

        output.WriteLine($"windowstream: serving window 0x{handle.value:X} ({physicalWidth}x{physicalHeight})");
        output.WriteLine($"  TCP control: 0.0.0.0:{sessionHost.TcpPort}");
        output.WriteLine($"  UDP video  : 0.0.0.0:{sessionHost.UdpPort}");
        output.WriteLine($"  mDNS       : _windowstream._tcp as '{hostname}' on port {sessionHost.TcpPort}");
        output.WriteLine("  Press Ctrl-C to stop.");

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    private static async Task<(int widthPixels, int heightPixels)> ProbeCaptureSizeAsync(WindowHandle handle, CancellationToken cancellationToken)
    {
        WgcCaptureSource probeSource = new WgcCaptureSource();
        using CancellationTokenSource probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using IWindowCapture probe = probeSource.Start(handle, new CaptureOptions(30, false), probeTimeout.Token);
        await foreach (CapturedFrame frame in probe.Frames.WithCancellation(probeTimeout.Token))
        {
            return (frame.widthPixels, frame.heightPixels);
        }
        return (640, 360);
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr windowHandle, out NativeRect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int left, top, right, bottom; }

    private static (int widthPixels, int heightPixels) GetPhysicalWindowSize(WindowHandle handle)
    {
        IntPtr hwnd = new IntPtr(handle.value);
        // ClientRect excludes window chrome / borders / shadows, matching what WGC
        // actually captures. Using GetWindowRect here mismatches WGC by ~15 pixels
        // and causes sws_scale to hang on out-of-bounds reads.
        if (!GetClientRect(hwnd, out NativeRect rect))
        {
            return (640, 360);
        }
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;
        int logicalWidth = rect.right - rect.left;
        int logicalHeight = rect.bottom - rect.top;
        int physicalWidth = (int)Math.Round(logicalWidth * scale);
        int physicalHeight = (int)Math.Round(logicalHeight * scale);
        return (AlignToEven(physicalWidth), AlignToEven(physicalHeight));
    }

    private static int AlignToEven(int value) => value % 2 == 0 ? value : value + 1;
}
#endif
