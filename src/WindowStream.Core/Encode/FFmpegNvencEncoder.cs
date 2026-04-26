using System;
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

namespace WindowStream.Core.Encode;

public sealed class FFmpegNvencEncoder : IVideoEncoder
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
    private nint softwareScaleContextPointer;

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
        context->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
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

        int openResult = ffmpeg.avcodec_open2(context, codec, null);
        if (openResult < 0)
        {
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("avcodec_open2 failed.", openResult);
        }

        AVFrame* frame = ffmpeg.av_frame_alloc();
        frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        frame->width = options.widthPixels;
        frame->height = options.heightPixels;
        int allocateResult = ffmpeg.av_frame_get_buffer(frame, 32);
        if (allocateResult < 0)
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("av_frame_get_buffer failed.", allocateResult);
        }

        AVPacket* packet = ffmpeg.av_packet_alloc();
        if (packet == null)
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("av_packet_alloc returned null.");
        }

        codecContextPointer = (nint)context;
        stagingFramePointer = (nint)frame;
        reusablePacketPointer = (nint)packet;
        softwareScaleContextPointer = (nint)ffmpeg.sws_getContext(
            options.widthPixels, options.heightPixels, AVPixelFormat.AV_PIX_FMT_BGRA,
            options.widthPixels, options.heightPixels, AVPixelFormat.AV_PIX_FMT_NV12,
            ffmpeg.SWS_BILINEAR, null, null, null);
        this.options = options;
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
        AVCodecContext* context = (AVCodecContext*)codecContextPointer;
        AVFrame* stagingFrame = (AVFrame*)stagingFramePointer;
        AVPacket* packet = (AVPacket*)reusablePacketPointer;
        SwsContext* scaleContext = (SwsContext*)softwareScaleContextPointer;

        int scaleResult;
        // sws_scale's srcSliceH must be <= the source height passed to
        // sws_getContext, otherwise it crashes with "Slice parameters 0, N
        // are invalid". The CLI rounds probe dims down to even before
        // configuring the encoder, so a frame whose actual height is odd
        // (probe 1182x891 -> encoder configured at 1182x890) used to fault
        // on the very first frame. Clamp to the configured height; matches
        // the spec's "round down -- we lose at most one row".
        int sourceSliceHeight = Math.Min(frame.heightPixels, options!.heightPixels);
        fixed (byte* sourcePointer = frame.pixelBuffer.Span)
        {
            byte*[] sourceData = new byte*[4] { sourcePointer, null, null, null };
            int[] sourceStride = new int[4] { frame.rowStrideBytes, 0, 0, 0 };
            scaleResult = ffmpeg.sws_scale(
                scaleContext,
                sourceData, sourceStride, 0, sourceSliceHeight,
                stagingFrame->data, stagingFrame->linesize);
        }
        if (scaleResult < 0)
        {
            throw new EncoderException("sws_scale failed.", scaleResult);
        }

        stagingFrame->pts = frameIndex++;
        if (forceNextKeyframe)
        {
            stagingFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
            stagingFrame->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
            forceNextKeyframe = false;
        }
        else
        {
            stagingFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            stagingFrame->flags &= ~ffmpeg.AV_FRAME_FLAG_KEY;
        }

        int sendResult = ffmpeg.avcodec_send_frame(context, stagingFrame);
        if (sendResult < 0)
        {
            throw new EncoderException("avcodec_send_frame failed.", sendResult);
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
        if (softwareScaleContextPointer != 0)
        {
            ffmpeg.sws_freeContext((SwsContext*)softwareScaleContextPointer);
            softwareScaleContextPointer = 0;
        }
    }
}
