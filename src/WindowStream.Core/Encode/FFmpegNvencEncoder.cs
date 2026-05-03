#if WINDOWS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;

namespace WindowStream.Core.Encode;

public sealed class FFmpegNvencEncoder : IVideoEncoder, IFrameTexturePool
{
    private readonly IFFmpegNativeLoader nativeLoader;
    private readonly Channel<EncodedChunk> chunkChannel =
        Channel.CreateUnbounded<EncodedChunk>(new UnboundedChannelOptions { SingleReader = true });
    private EncoderOptions? options;
    private long frameIndex;
    private bool forceNextKeyframe;
    private bool disposed;

    // Native context pointers stored as nint to avoid unsafe class-level field declarations
    private nint codecContextPointer;
    private nint stagingFramePointer;
    private nint reusablePacketPointer;
    private nint hardwareDeviceContextReference;     // AVBufferRef* for the AVHWDeviceContext (D3D11VA)
    private nint hardwareFramesContextReference;     // AVBufferRef* for the AVHWFramesContext (NV12 pool)
    private Direct3D11DeviceManager? sharedDeviceManager;
    private bool ownsSharedDeviceManager;
    private readonly ConcurrentQueue<nint> pendingPoolFramePointers = new ConcurrentQueue<nint>();

    public IAsyncEnumerable<EncodedChunk> EncodedChunks { get; }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
        Justification = "Delegates to FFmpegNativeLoader which is excluded; covered by Phase 12 integration tests.")]
    public FFmpegNvencEncoder() : this(new FFmpegNativeLoader()) { }

    public FFmpegNvencEncoder(IFFmpegNativeLoader nativeLoader)
    {
        this.nativeLoader = nativeLoader ?? throw new ArgumentNullException(nameof(nativeLoader));
        EncodedChunks = ReadAsync();
    }

    [ExcludeFromCodeCoverage(Justification = "Delegates to ValidatePreConfigureState (tested) and OpenCodecAndAssignOptions (native, Phase 12).")]
    public void Configure(EncoderOptions options)
    {
        ValidatePreConfigureState(options);
        sharedDeviceManager = new Direct3D11DeviceManager();
        ownsSharedDeviceManager = true;
        OpenCodecAndAssignOptions(options);
    }

    [ExcludeFromCodeCoverage(Justification = "Delegates to ValidatePreConfigureState (tested) and OpenCodecAndAssignOptions (native, Phase 12).")]
    public void Configure(EncoderOptions options, Direct3D11DeviceManager? deviceManager)
    {
        ValidatePreConfigureState(options);
        if (deviceManager is null)
        {
            sharedDeviceManager = new Direct3D11DeviceManager();
            ownsSharedDeviceManager = true;
        }
        else
        {
            sharedDeviceManager = deviceManager;
            ownsSharedDeviceManager = false;
        }
        OpenCodecAndAssignOptions(options);
    }

    internal void ValidatePreConfigureState(EncoderOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (this.options is not null) throw new InvalidOperationException("Configure already called.");
        nativeLoader.EnsureLoaded();
    }

    /// <summary>
    /// Sets the configured state without invoking native FFmpeg resources.
    /// For use in unit tests via InternalsVisibleTo only.
    /// </summary>
    internal void SimulateConfiguredForTest(EncoderOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    [ExcludeFromCodeCoverage(Justification = "Native FFmpeg calls; exercised by Phase 12 integration tests.")]
    private unsafe void OpenCodecAndAssignOptions(EncoderOptions options)
    {
        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
        if (codec == null)
        {
            throw new EncoderException("h264_nvenc codec not available in the loaded FFmpeg build.");
        }

        AVCodecContext* context = ffmpeg.avcodec_alloc_context3(codec);
        if (context == null)
        {
            throw new EncoderException("avcodec_alloc_context3 returned null.");
        }

        context->width = options.widthPixels;
        context->height = options.heightPixels;
        context->time_base = new AVRational { num = 1, den = options.framesPerSecond };
        context->framerate = new AVRational { num = options.framesPerSecond, den = 1 };
        context->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
        context->sw_pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
        context->bit_rate = options.bitrateBitsPerSecond;
        context->gop_size = options.groupOfPicturesLength;
        context->max_b_frames = 0;

        ffmpeg.av_opt_set(context->priv_data, "preset", "p1", 0);
        // tune is read from env so the operator can A/B test ll vs ull without rebuilding.
        // Default = ull (ultra-low-latency). Measured improvement vs ll on Unity 4K → GXR:
        // server cap stdev 101ms → 9ms, viewer reasm p99 577ms → 40ms, cap→dec max 185ms → 96ms.
        // Mechanism: ull disables enough prediction/rate-control machinery that every frame
        // encodes in a similar fixed time, so NVENC stops back-pressuring the WGC capture
        // pump and the entire pipeline runs at smooth ~28ms intervals. Set
        // WINDOWSTREAM_NVENC_TUNE=ll to fall back if visual quality regresses on a source.
        string tune = Environment.GetEnvironmentVariable("WINDOWSTREAM_NVENC_TUNE") ?? "ull";
        ffmpeg.av_opt_set(context->priv_data, "tune", tune, 0);
        Console.Error.WriteLine($"[FFmpegNvencEncoder] tune={tune}");
        ffmpeg.av_opt_set(context->priv_data, "zerolatency", "1", 0);
        ffmpeg.av_opt_set(context->priv_data, "rc", "cbr", 0);
        // Cap NVENC's input surface queue to its minimum. With the default
        // (~4 surfaces), discrete-event capture (typing) shows 3 frames
        // permanently buffered inside the encoder — measured 751ms cap->enc
        // median lag at 250ms event spacing, perfectly matching the user-felt
        // "4-5 keypresses behind" symptom.
        ffmpeg.av_opt_set(context->priv_data, "surfaces", "1", 0);

        // Build AVHWDeviceContext (D3D11VA) wrapping the shared D3D11 device.
        AVBufferRef* deviceContextReference = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (deviceContextReference == null)
        {
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("av_hwdevice_ctx_alloc(D3D11VA) returned null.");
        }
        AVHWDeviceContext* deviceContext = (AVHWDeviceContext*)deviceContextReference->data;
        AVD3D11VADeviceContext* d3d11DeviceContext = (AVD3D11VADeviceContext*)deviceContext->hwctx;
        d3d11DeviceContext->device = (ID3D11Device*)(void*)sharedDeviceManager!.NativeDevicePointer;
        d3d11DeviceContext->device_context = (ID3D11DeviceContext*)(void*)sharedDeviceManager!.NativeContextPointer;
        // Increment refcount on the device + context so FFmpeg's eventual release doesn't underflow our ownership.
        // FFmpeg calls Release() on these in av_hwdevice_ctx_free; we want our Direct3D11DeviceManager to retain the
        // canonical reference, so we AddRef here.
        ((Silk.NET.Core.Native.IUnknown*)(void*)d3d11DeviceContext->device)->AddRef();
        ((Silk.NET.Core.Native.IUnknown*)(void*)d3d11DeviceContext->device_context)->AddRef();

        int hwDeviceInitResult = ffmpeg.av_hwdevice_ctx_init(deviceContextReference);
        if (hwDeviceInitResult < 0)
        {
            ffmpeg.av_buffer_unref(&deviceContextReference);
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("av_hwdevice_ctx_init failed.", hwDeviceInitResult);
        }

        // Build AVHWFramesContext for NV12 textures.
        AVBufferRef* framesContextReference = ffmpeg.av_hwframe_ctx_alloc(deviceContextReference);
        if (framesContextReference == null)
        {
            ffmpeg.av_buffer_unref(&deviceContextReference);
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("av_hwframe_ctx_alloc returned null.");
        }
        AVHWFramesContext* framesContext = (AVHWFramesContext*)framesContextReference->data;
        framesContext->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        framesContext->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        framesContext->width = options.widthPixels;
        framesContext->height = options.heightPixels;
        framesContext->initial_pool_size = 4;

        int hwFramesInitResult = ffmpeg.av_hwframe_ctx_init(framesContextReference);
        if (hwFramesInitResult < 0)
        {
            ffmpeg.av_buffer_unref(&framesContextReference);
            ffmpeg.av_buffer_unref(&deviceContextReference);
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("av_hwframe_ctx_init failed.", hwFramesInitResult);
        }

        context->hw_frames_ctx = ffmpeg.av_buffer_ref(framesContextReference);
        if (context->hw_frames_ctx == null)
        {
            ffmpeg.av_buffer_unref(&framesContextReference);
            ffmpeg.av_buffer_unref(&deviceContextReference);
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("av_buffer_ref(hw_frames_ctx) returned null.");
        }

        int openResult = ffmpeg.avcodec_open2(context, codec, null);
        if (openResult < 0)
        {
            ffmpeg.av_buffer_unref(&framesContextReference);
            ffmpeg.av_buffer_unref(&deviceContextReference);
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("avcodec_open2 failed.", openResult);
        }

        AVPacket* packet = ffmpeg.av_packet_alloc();
        if (packet == null)
        {
            ffmpeg.av_buffer_unref(&framesContextReference);
            ffmpeg.av_buffer_unref(&deviceContextReference);
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("av_packet_alloc returned null.");
        }

        codecContextPointer = (nint)context;
        hardwareDeviceContextReference = (nint)deviceContextReference;
        hardwareFramesContextReference = (nint)framesContextReference;
        reusablePacketPointer = (nint)packet;
        // stagingFramePointer (the pre-allocated AVFrame for sws_scale) is gone — frames come from the pool now.
        stagingFramePointer = 0;
        this.options = options;
    }

    [ExcludeFromCodeCoverage(Justification = "Native FFmpeg calls; exercised by Phase 12 integration tests.")]
    public unsafe void AcquireFrameTexture(out nint texturePointer, out int textureSubresourceIndex)
    {
        if (options is null)
        {
            throw new InvalidOperationException("Configure must be called before AcquireFrameTexture.");
        }
        if (hardwareFramesContextReference == 0)
        {
            throw new InvalidOperationException("Hardware frames context is not initialized.");
        }

        AVFrame* frame = ffmpeg.av_frame_alloc();
        if (frame == null)
        {
            throw new EncoderException("av_frame_alloc returned null.");
        }

        AVBufferRef* framesReference = (AVBufferRef*)hardwareFramesContextReference;
        int allocateResult = ffmpeg.av_hwframe_get_buffer(framesReference, frame, 0);
        if (allocateResult < 0)
        {
            ffmpeg.av_frame_free(&frame);
            throw new EncoderException("av_hwframe_get_buffer failed.", allocateResult);
        }

        // For D3D11 hwaccel, frame->data[0] is the ID3D11Texture2D* and
        // frame->data[1] is the subresource index (cast through intptr).
        texturePointer = (nint)frame->data[0];
        textureSubresourceIndex = (int)(long)frame->data[1];

        pendingPoolFramePointers.Enqueue((nint)frame);
    }

    public void RequestKeyframe()
    {
        if (options is null) throw new InvalidOperationException("Configure must be called first.");
        forceNextKeyframe = true;
    }

    public Task EncodeAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        if (options is null) throw new InvalidOperationException("Configure must be called before EncodeAsync.");
        cancellationToken.ThrowIfCancellationRequested();
        return EncodeAsyncCore(frame, cancellationToken);
    }

    [ExcludeFromCodeCoverage(Justification = "Native encoding path; exercised by Phase 12 integration tests.")]
    private async Task EncodeAsyncCore(CapturedFrame frame, CancellationToken cancellationToken)
    {
        await Task.Run(() => EncodeOnThread(frame), cancellationToken).ConfigureAwait(false);
    }

    [ExcludeFromCodeCoverage(Justification = "Native FFmpeg calls; exercised by Phase 12 integration tests.")]
    private unsafe void EncodeOnThread(CapturedFrame frame)
    {
        if (frame.representation != FrameRepresentation.Texture)
        {
            throw new EncoderException(
                "FFmpegNvencEncoder requires texture-bearing CapturedFrames after M4. "
                + "Bytes-bearing frames are no longer supported.");
        }

        if (!pendingPoolFramePointers.TryDequeue(out nint pendingFramePointer))
        {
            throw new EncoderException(
                "EncodeAsync called without a matching AcquireFrameTexture — pool queue is empty.");
        }

        AVFrame* poolFrame = (AVFrame*)pendingFramePointer;
        if ((nint)poolFrame->data[0] != frame.nativeTexturePointer
            || (int)(long)poolFrame->data[1] != frame.textureArrayIndex)
        {
            ffmpeg.av_frame_free(&poolFrame);
            throw new EncoderException(
                "EncodeAsync received a CapturedFrame whose texture pointer + array index "
                + "do not match the next queued pool frame. Pool / encode ordering is broken.");
        }

        AVCodecContext* context = (AVCodecContext*)codecContextPointer;
        AVPacket* packet = (AVPacket*)reusablePacketPointer;

        poolFrame->pts = frameIndex++;
        if (forceNextKeyframe)
        {
            poolFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
            poolFrame->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
            forceNextKeyframe = false;
        }
        else
        {
            poolFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            poolFrame->flags &= ~ffmpeg.AV_FRAME_FLAG_KEY;
        }

        try
        {
            int sendResult = ffmpeg.avcodec_send_frame(context, poolFrame);
            if (sendResult < 0)
            {
                throw new EncoderException("avcodec_send_frame failed.", sendResult);
            }
        }
        finally
        {
            // Release the pool's buffers; FFmpeg internally recycles the texture for the next acquire.
            ffmpeg.av_frame_free(&poolFrame);
        }

        while (true)
        {
            int receiveResult = ffmpeg.avcodec_receive_packet(context, packet);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
            {
                break;
            }
            if (receiveResult < 0)
            {
                throw new EncoderException("avcodec_receive_packet failed.", receiveResult);
            }

            byte[] managed = new byte[packet->size];
            Marshal.Copy((IntPtr)packet->data, managed, 0, packet->size);
            bool isKeyframe = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            long timestampMicroseconds = 1_000_000L * packet->pts
                * context->time_base.num / context->time_base.den;
            long wallClockMilliseconds = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
            System.Console.Error.WriteLine(
                $"[FRAMECOUNT] stage=enc ptsUs={timestampMicroseconds} wallMs={wallClockMilliseconds}");
            chunkChannel.Writer.TryWrite(new EncodedChunk(managed, isKeyframe, timestampMicroseconds));
            ffmpeg.av_packet_unref(packet);
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Async enumerable state machine is exercised end-to-end by Phase 12 integration tests.")]
    private async IAsyncEnumerable<EncodedChunk> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await chunkChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (chunkChannel.Reader.TryRead(out EncodedChunk? chunk))
            {
                yield return chunk;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        chunkChannel.Writer.TryComplete();
        FreeNativeResources();
        return ValueTask.CompletedTask;
    }

    [ExcludeFromCodeCoverage(Justification = "Native FFmpeg calls; exercised by Phase 12 integration tests.")]
    private unsafe void FreeNativeResources()
    {
        // Drain any unconsumed pool frames first.
        while (pendingPoolFramePointers.TryDequeue(out nint pendingFramePointer))
        {
            AVFrame* pendingFrame = (AVFrame*)pendingFramePointer;
            ffmpeg.av_frame_free(&pendingFrame);
        }

        if (reusablePacketPointer != 0)
        {
            AVPacket* packet = (AVPacket*)reusablePacketPointer;
            ffmpeg.av_packet_free(&packet);
            reusablePacketPointer = 0;
        }
        if (stagingFramePointer != 0)
        {
            AVFrame* frame = (AVFrame*)stagingFramePointer;
            ffmpeg.av_frame_free(&frame);
            stagingFramePointer = 0;
        }
        if (codecContextPointer != 0)
        {
            AVCodecContext* context = (AVCodecContext*)codecContextPointer;
            ffmpeg.avcodec_free_context(&context);
            codecContextPointer = 0;
        }
        if (hardwareFramesContextReference != 0)
        {
            AVBufferRef* reference = (AVBufferRef*)hardwareFramesContextReference;
            ffmpeg.av_buffer_unref(&reference);
            hardwareFramesContextReference = 0;
        }
        if (hardwareDeviceContextReference != 0)
        {
            AVBufferRef* reference = (AVBufferRef*)hardwareDeviceContextReference;
            ffmpeg.av_buffer_unref(&reference);
            hardwareDeviceContextReference = 0;
        }
        if (ownsSharedDeviceManager && sharedDeviceManager is not null)
        {
            sharedDeviceManager.Dispose();
            sharedDeviceManager = null;
            ownsSharedDeviceManager = false;
        }
    }
}
#endif
