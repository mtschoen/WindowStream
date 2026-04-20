using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Encode;
using WindowStream.Core.Protocol;
using WindowStream.Core.Transport;

namespace WindowStream.Core.Session;

public sealed class SessionHost : IAsyncDisposable
{
    private readonly SessionHostOptions options;
    private readonly IWindowCaptureSource captureSource;
    private readonly IVideoEncoder videoEncoder;
    private readonly ITcpConnectionAcceptor tcpAcceptor;
    private readonly IUdpVideoSender udpSender;
    private readonly TimeProvider timeProvider;

    private CancellationTokenSource? lifecycleCancellation;
    private IControlChannel? activeChannel;
    private IPEndPoint? activeViewerEndpoint;
    private WindowHandle targetWindow;
    private CaptureOptions captureOptions = new CaptureOptions(30, false);
    private EncoderOptions? encoderOptions;
    private int sequence;
    private bool disposed;

    public int UdpPort => udpSender.LocalPort;
    public int TcpPort => tcpAcceptor.LocalPort;

    public SessionHost(
        SessionHostOptions options,
        IWindowCaptureSource captureSource,
        IVideoEncoder videoEncoder,
        ITcpConnectionAcceptor tcpAcceptor,
        IUdpVideoSender udpSender,
        TimeProvider timeProvider)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.captureSource = captureSource ?? throw new ArgumentNullException(nameof(captureSource));
        this.videoEncoder = videoEncoder ?? throw new ArgumentNullException(nameof(videoEncoder));
        this.tcpAcceptor = tcpAcceptor ?? throw new ArgumentNullException(nameof(tcpAcceptor));
        this.udpSender = udpSender ?? throw new ArgumentNullException(nameof(udpSender));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task StartAsync(
        WindowHandle window,
        CaptureOptions capture,
        EncoderOptions encoder,
        IPEndPoint udpLocalEndpoint,
        int tcpPort,
        CancellationToken cancellationToken)
    {
        targetWindow = window;
        captureOptions = capture;
        encoderOptions = encoder;
        lifecycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await udpSender.BindAsync(udpLocalEndpoint, lifecycleCancellation.Token).ConfigureAwait(false);
        tcpAcceptor.StartListening(tcpPort);

        videoEncoder.Configure(encoder);

        _ = RunCapturePumpAsync(lifecycleCancellation.Token);
        _ = RunEncodePumpAsync(lifecycleCancellation.Token);
        _ = RunAcceptLoopAsync(lifecycleCancellation.Token);
    }

    private async Task RunCapturePumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using IWindowCapture capture = captureSource.Start(targetWindow, captureOptions, cancellationToken);
            await foreach (CapturedFrame frame in capture.Frames.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await videoEncoder.EncodeAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Lifecycle cancelled — normal shutdown.
        }
    }

    private async Task RunEncodePumpAsync(CancellationToken cancellationToken)
    {
        NalFragmenter fragmenter = new NalFragmenter();
        try
        {
            await foreach (EncodedChunk chunk in videoEncoder.EncodedChunks.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                IPEndPoint? destination = activeViewerEndpoint;
                if (destination is null)
                {
                    continue;
                }

                byte[] nalUnit = chunk.payload.ToArray();
                int currentSequence = sequence++;
                foreach (FragmentedPacket packet in fragmenter.Fragment(
                    streamId: options.StreamId,
                    sequence: currentSequence,
                    presentationTimestampMicroseconds: chunk.presentationTimestampMicroseconds,
                    isIdrFrame: chunk.isKeyframe,
                    nalUnit: nalUnit))
                {
                    await udpSender.SendPacketAsync(packet, destination, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Lifecycle cancelled — normal shutdown.
        }
    }

    private async Task RunAcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IControlChannel channel = await tcpAcceptor.AcceptAsync(cancellationToken).ConfigureAwait(false);

                if (activeChannel is not null)
                {
                    await SendViewerBusyAndCloseAsync(channel, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                activeChannel = channel;
                _ = ServeViewerAsync(channel, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Lifecycle cancelled — normal shutdown.
        }
    }

    private static async Task SendViewerBusyAndCloseAsync(IControlChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource shortTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shortTimeout.Token);
            await channel.SendAsync(
                new ErrorMessage(ProtocolErrorCode.ViewerBusy, "a viewer is already connected"),
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best effort.
        }
        finally
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ServeViewerAsync(IControlChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            ControlMessage firstMessage = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (firstMessage is not HelloMessage)
            {
                await channel.SendAsync(
                    new ErrorMessage(ProtocolErrorCode.MalformedMessage, "expected HELLO"),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            ActiveStreamInformation activeStream = BuildActiveStreamDescriptor();
            await channel.SendAsync(
                new ServerHelloMessage(options.ServerVersion, activeStream),
                cancellationToken).ConfigureAwait(false);

            videoEncoder.RequestKeyframe();

            using CancellationTokenSource viewerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task heartbeatTask = RunHeartbeatAsync(channel, viewerCancellation.Token);

            try
            {
                await RunViewerReceiveLoopAsync(channel, viewerCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                viewerCancellation.Cancel();
                try { await heartbeatTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException)
        {
            // Viewer or lifecycle cancelled.
        }
        catch (System.IO.EndOfStreamException)
        {
            // Viewer disconnected.
        }
        finally
        {
            activeViewerEndpoint = null;
            activeChannel = null;
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunViewerReceiveLoopAsync(IControlChannel channel, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ControlMessage message = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            switch (message)
            {
                case RequestKeyframeMessage:
                    videoEncoder.RequestKeyframe();
                    break;
                case HeartbeatMessage:
                    channel.NotifyHeartbeatReceived();
                    break;
            }
        }
    }

    private async Task RunHeartbeatAsync(IControlChannel channel, CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromMilliseconds(options.HeartbeatIntervalMilliseconds);
        TimeSpan timeout = TimeSpan.FromMilliseconds(options.HeartbeatTimeoutMilliseconds);
        using PeriodicTimer timer = new PeriodicTimer(interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await channel.SendAsync(HeartbeatMessage.Instance, cancellationToken).ConfigureAwait(false);
                if (timeProvider.GetUtcNow() - channel.LastHeartbeatReceived > timeout)
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Heartbeat loop cancelled.
        }
    }

    private ActiveStreamInformation BuildActiveStreamDescriptor()
    {
        EncoderOptions encoder = encoderOptions!;
        return new ActiveStreamInformation(
            StreamId: options.StreamId,
            UdpPort: udpSender.LocalPort,
            Codec: options.Codec,
            Width: encoder.widthPixels,
            Height: encoder.heightPixels,
            FramesPerSecond: encoder.framesPerSecond);
    }

    public void RegisterViewerEndpoint(IPEndPoint endpoint)
    {
        activeViewerEndpoint = endpoint;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;

        lifecycleCancellation?.Cancel();

        if (activeChannel is not null)
        {
            await activeChannel.DisposeAsync().ConfigureAwait(false);
            activeChannel = null;
        }

        await videoEncoder.DisposeAsync().ConfigureAwait(false);
        await udpSender.DisposeAsync().ConfigureAwait(false);
        await tcpAcceptor.DisposeAsync().ConfigureAwait(false);
        lifecycleCancellation?.Dispose();
    }
}
