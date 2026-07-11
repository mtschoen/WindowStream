#if WINDOWS
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using ID3D11Device = FFmpeg.AutoGen.ID3D11Device;
using ID3D11DeviceContext = FFmpeg.AutoGen.ID3D11DeviceContext;

namespace WindowStream.Core.Encode;

public sealed class FFmpegNvencEncoder : IVideoEncoder, IFrameTexturePool
{
    readonly IFFmpegNativeLoader _nativeLoader;

    readonly Channel<EncodedChunk> _chunkChannel =
        Channel.CreateUnbounded<EncodedChunk>(new UnboundedChannelOptions { SingleReader = true });

    EncoderOptions? _options;
    bool _forceNextKeyframe;
    bool _disposed;

    static readonly bool IsFrameCountLogEnabled =
        Environment.GetEnvironmentVariable("WINDOWSTREAM_FRAMECOUNT") == "1";

    // Native context pointers stored as nint to avoid unsafe class-level field declarations
    nint _codecContextPointer;
    nint _stagingFramePointer;
    nint _reusablePacketPointer;
    nint _hardwareDeviceContextReference;     // AVBufferRef* for the AVHWDeviceContext (D3D11VA)
    nint _hardwareFramesContextReference;     // AVBufferRef* for the AVHWFramesContext (NV12 pool)
    Direct3D11DeviceManager? _sharedDeviceManager;
    bool _ownsSharedDeviceManager;

    readonly ConcurrentDictionary<(nint texturePointer, int subresourceIndex), nint> _pendingPoolFramesByKey =
        new ConcurrentDictionary<(nint texturePointer, int subresourceIndex), nint>();

    public IAsyncEnumerable<EncodedChunk> EncodedChunks { get; }

    [ExcludeFromCodeCoverage(
        Justification = "Delegates to FFmpegNativeLoader which is excluded; covered by Phase 12 integration tests.")]
    public FFmpegNvencEncoder() : this(new FFmpegNativeLoader()) { }

    public FFmpegNvencEncoder(IFFmpegNativeLoader nativeLoader)
    {
        _nativeLoader = nativeLoader ?? throw new ArgumentNullException(nameof(nativeLoader));
        EncodedChunks = ReadAsync();
    }

    [ExcludeFromCodeCoverage(Justification = "Delegates to ValidatePreConfigureState (tested) and OpenCodecAndAssignOptions (native, Phase 12).")]
    public void Configure(EncoderOptions options)
    {
        ValidatePreConfigureState(options);
        _sharedDeviceManager = new Direct3D11DeviceManager();
        _ownsSharedDeviceManager = true;
        OpenCodecAndAssignOptions(options);
    }

    [ExcludeFromCodeCoverage(Justification = "Delegates to ValidatePreConfigureState (tested) and OpenCodecAndAssignOptions (native, Phase 12).")]
    public void Configure(EncoderOptions options, Direct3D11DeviceManager? deviceManager)
    {
        ValidatePreConfigureState(options);
        if (deviceManager is null)
        {
            _sharedDeviceManager = new Direct3D11DeviceManager();
            _ownsSharedDeviceManager = true;
        }
        else
        {
            _sharedDeviceManager = deviceManager;
            _ownsSharedDeviceManager = false;
        }
        OpenCodecAndAssignOptions(options);
    }

    internal void ValidatePreConfigureState(EncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_options is not null) throw new InvalidOperationException("Configure already called.");
        _nativeLoader.EnsureLoaded();
    }

    /// <summary>
    /// Sets the configured state without invoking native FFmpeg resources.
    /// For use in unit tests via InternalsVisibleTo only.
    /// </summary>
    internal void SimulateConfiguredForTest(EncoderOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    [ExcludeFromCodeCoverage(Justification = "Native FFmpeg calls; exercised by Phase 12 integration tests.")]
    unsafe void OpenCodecAndAssignOptions(EncoderOptions options)
    {
        var sharedDeviceManager = _sharedDeviceManager
            ?? throw new InvalidOperationException("Configure must assign _sharedDeviceManager before opening the codec.");

        var codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
        if (codec == null)
        {
            throw new EncoderException("h264_nvenc codec not available in the loaded FFmpeg build.");
        }

        var context = ffmpeg.avcodec_alloc_context3(codec);
        if (context == null)
        {
            throw new EncoderException("avcodec_alloc_context3 returned null.");
        }

        context->width = options.WidthPixels;
        context->height = options.HeightPixels;
        // Microsecond-granularity time_base lets us pass the capture's wall-relative ptsUs
        // straight through as packet->pts, so the [FRAMECOUNT] join key is identical at
        // stage=convert (server), stage=enc (server), and stage=dec/present (viewer).
        context->time_base = new AVRational { num = 1, den = 1_000_000 };
        context->framerate = new AVRational { num = options.FramesPerSecond, den = 1 };
        context->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
        context->sw_pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
        context->bit_rate = options.BitrateBitsPerSecond;
        context->gop_size = options.GroupOfPicturesLength;
        context->max_b_frames = 0;

        ffmpeg.av_opt_set(context->priv_data, "preset", "p1", 0);
        // tune is read from env so the operator can A/B test ll vs ull without rebuilding.
        // Default = ull (ultra-low-latency). Measured improvement vs ll on Unity 4K → GXR:
        // server cap stdev 101ms → 9ms, viewer reasm p99 577ms → 40ms, cap→dec max 185ms → 96ms.
        // Mechanism: ull disables enough prediction/rate-control machinery that every frame
        // encodes in a similar fixed time, so NVENC stops back-pressuring the WGC capture
        // pump and the entire pipeline runs at smooth ~28ms intervals. Set
        // WINDOWSTREAM_NVENC_TUNE=ll to fall back if visual quality regresses on a source.
        var tune = Environment.GetEnvironmentVariable("WINDOWSTREAM_NVENC_TUNE") ?? "ull";
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
        var deviceContextReference = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (deviceContextReference == null)
        {
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("av_hwdevice_ctx_alloc(D3D11VA) returned null.");
        }
        var deviceContext = (AVHWDeviceContext*)deviceContextReference->data;
        // d3d11* keeps the Direct3D 11 domain spelling; ReSharper's digit rule would force d3D11.
        // ReSharper disable once InconsistentNaming
        var d3d11DeviceContext = (AVD3D11VADeviceContext*)deviceContext->hwctx;
        d3d11DeviceContext->device = (ID3D11Device*)(void*)sharedDeviceManager.NativeDevicePointer;
        d3d11DeviceContext->device_context = (ID3D11DeviceContext*)(void*)sharedDeviceManager.NativeContextPointer;
        // Increment refcount on the device + context so FFmpeg's eventual release doesn't underflow our ownership.
        // FFmpeg calls Release() on these in av_hwdevice_ctx_free; we want our Direct3D11DeviceManager to retain the
        // canonical reference, so we AddRef here.
        ((IUnknown*)d3d11DeviceContext->device)->AddRef();
        ((IUnknown*)d3d11DeviceContext->device_context)->AddRef();

        var hwDeviceInitResult = ffmpeg.av_hwdevice_ctx_init(deviceContextReference);
        if (hwDeviceInitResult < 0)
        {
            ffmpeg.av_buffer_unref(&deviceContextReference);
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("av_hwdevice_ctx_init failed.", hwDeviceInitResult);
        }

        var framesContextReference = ffmpeg.av_hwframe_ctx_alloc(deviceContextReference);
        if (framesContextReference == null)
        {
            ffmpeg.av_buffer_unref(&deviceContextReference);
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("av_hwframe_ctx_alloc returned null.");
        }
        var framesContext = (AVHWFramesContext*)framesContextReference->data;
        framesContext->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        framesContext->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        framesContext->width = options.WidthPixels;
        framesContext->height = options.HeightPixels;
        framesContext->initial_pool_size = 4;

        // For NVENC encoding the pool textures must carry D3D11_BIND_RENDER_TARGET (0x20).
        // The default D3D11VA BindFlags (D3D11_BIND_DECODER | D3D11_BIND_SHADER_RESOURCE)
        // cause E_INVALIDARG on av_hwframe_ctx_init because NVENC's driver rejects decode-only
        // bind flags for encode surfaces. D3D11_BIND_SHADER_RESOURCE is included so the same
        // texture can also be used as an input view in the video-processor path if needed.
        // ReSharper disable once InconsistentNaming
        var d3d11FramesContext = (AVD3D11VAFramesContext*)framesContext->hwctx;
        d3d11FramesContext->BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource);

        var hwFramesInitResult = ffmpeg.av_hwframe_ctx_init(framesContextReference);
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

        var openResult = ffmpeg.avcodec_open2(context, codec, null);
        if (openResult < 0)
        {
            ffmpeg.av_buffer_unref(&framesContextReference);
            ffmpeg.av_buffer_unref(&deviceContextReference);
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("avcodec_open2 failed.", openResult);
        }

        var packet = ffmpeg.av_packet_alloc();
        if (packet == null)
        {
            ffmpeg.av_buffer_unref(&framesContextReference);
            ffmpeg.av_buffer_unref(&deviceContextReference);
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("av_packet_alloc returned null.");
        }

        _codecContextPointer = (nint)context;
        _hardwareDeviceContextReference = (nint)deviceContextReference;
        _hardwareFramesContextReference = (nint)framesContextReference;
        _reusablePacketPointer = (nint)packet;
        // stagingFramePointer (the pre-allocated AVFrame for sws_scale) is gone — frames come from the pool now.
        _stagingFramePointer = 0;
        _options = options;
    }

    [ExcludeFromCodeCoverage(Justification = "Native FFmpeg calls; exercised by Phase 12 integration tests.")]
    public unsafe void AcquireFrameTexture(out nint texturePointer, out int textureSubresourceIndex)
    {
        if (_options is null)
        {
            throw new InvalidOperationException("Configure must be called before AcquireFrameTexture.");
        }
        if (_hardwareFramesContextReference == 0)
        {
            throw new InvalidOperationException("Hardware frames context is not initialized.");
        }

        var frame = ffmpeg.av_frame_alloc();
        if (frame == null)
        {
            throw new EncoderException("av_frame_alloc returned null.");
        }

        var framesReference = (AVBufferRef*)_hardwareFramesContextReference;
        var allocateResult = ffmpeg.av_hwframe_get_buffer(framesReference, frame, 0);
        if (allocateResult < 0)
        {
            ffmpeg.av_frame_free(&frame);
            throw new EncoderException("av_hwframe_get_buffer failed.", allocateResult);
        }

        // For D3D11 hwaccel, frame->data[0] is the ID3D11Texture2D* and
        // frame->data[1] is the subresource index (cast through intptr).
        texturePointer = (nint)frame->data[0];
        textureSubresourceIndex = (int)(long)frame->data[1];

        if (!_pendingPoolFramesByKey.TryAdd((texturePointer, textureSubresourceIndex), (nint)frame))
        {
            ffmpeg.av_frame_free(&frame);
            throw new EncoderException(
                "Duplicate pool key: FFmpeg pool returned ("
                + "texP=0x" + texturePointer.ToString("X", CultureInfo.InvariantCulture)
                + ", idx=" + textureSubresourceIndex
                + ") while a prior acquisition is still in flight. "
                + "Indicates FFmpeg pool corruption or a missing Release.");
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Native FFmpeg calls; exercised by Phase 12 integration tests.")]
    public unsafe void ReleaseFrameTexture(nint texturePointer, int textureSubresourceIndex)
    {
        if (_options is null)
        {
            throw new InvalidOperationException("Configure must be called before ReleaseFrameTexture.");
        }
        if (!_pendingPoolFramesByKey.TryRemove((texturePointer, textureSubresourceIndex), out var pendingFramePointer))
        {
            throw new EncoderException(
                "No pool AVFrame matches released ("
                + "texP=0x" + texturePointer.ToString("X", CultureInfo.InvariantCulture)
                + ", idx=" + textureSubresourceIndex
                + ") — either release was called without a matching acquire, "
                + "or the texture was already consumed by EncodeAsync.");
        }
        var poolFrame = (AVFrame*)pendingFramePointer;
        ffmpeg.av_frame_free(&poolFrame);
    }

    public void RequestKeyframe()
    {
        if (_options is null) throw new InvalidOperationException("Configure must be called first.");
        _forceNextKeyframe = true;
    }

    public Task EncodeAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        if (_options is null) throw new InvalidOperationException("Configure must be called before EncodeAsync.");
        cancellationToken.ThrowIfCancellationRequested();
        return EncodeAsyncCore(frame, cancellationToken);
    }

    [ExcludeFromCodeCoverage(Justification = "Native encoding path; exercised by Phase 12 integration tests.")]
    async Task EncodeAsyncCore(CapturedFrame frame, CancellationToken cancellationToken)
    {
        await Task.Run(() => EncodeOnThread(frame), cancellationToken).ConfigureAwait(false);
    }

    [ExcludeFromCodeCoverage(Justification = "Native FFmpeg calls; exercised by Phase 12 integration tests.")]
    unsafe void EncodeOnThread(CapturedFrame frame)
    {
        if (frame.Representation != FrameRepresentation.Texture)
        {
            throw new EncoderException(
                "FFmpegNvencEncoder requires texture-bearing CapturedFrames after M4. "
                + "Bytes-bearing frames are no longer supported.");
        }

        if (!_pendingPoolFramesByKey.TryRemove((frame.NativeTexturePointer, frame.TextureArrayIndex), out var pendingFramePointer))
        {
            throw new EncoderException(
                "No pool AVFrame matches captured ("
                + "texP=0x" + frame.NativeTexturePointer.ToString("X", CultureInfo.InvariantCulture)
                + ", idx=" + frame.TextureArrayIndex
                + ") — caller violated the IFrameTexturePool contract "
                + "(EncodeAsync or ReleaseFrameTexture must follow each AcquireFrameTexture exactly once).");
        }

        var poolFrame = (AVFrame*)pendingFramePointer;

        var context = (AVCodecContext*)_codecContextPointer;
        var packet = (AVPacket*)_reusablePacketPointer;

        poolFrame->pts = frame.PresentationTimestampMicroseconds;
        if (_forceNextKeyframe)
        {
            poolFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
            poolFrame->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
            _forceNextKeyframe = false;
        }
        else
        {
            poolFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            poolFrame->flags &= ~ffmpeg.AV_FRAME_FLAG_KEY;
        }

        try
        {
            var sendResult = ffmpeg.avcodec_send_frame(context, poolFrame);
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
            var receiveResult = ffmpeg.avcodec_receive_packet(context, packet);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
            {
                break;
            }
            if (receiveResult < 0)
            {
                throw new EncoderException("avcodec_receive_packet failed.", receiveResult);
            }

            var managed = new byte[packet->size];
            Marshal.Copy((IntPtr)packet->data, managed, 0, packet->size);
            var isKeyframe = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            // time_base is {1, 1_000_000}, so packet->pts already is microseconds.
            var timestampMicroseconds = packet->pts;
            var wallClockMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (IsFrameCountLogEnabled)
            {
                Console.Error.WriteLine(
                    $"[FRAMECOUNT] stage=enc ptsUs={timestampMicroseconds} wallMs={wallClockMilliseconds}");
            }
            _chunkChannel.Writer.TryWrite(new EncodedChunk(managed, isKeyframe, timestampMicroseconds));
            ffmpeg.av_packet_unref(packet);
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Async enumerable state machine is exercised end-to-end by Phase 12 integration tests.")]
    async IAsyncEnumerable<EncodedChunk> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _chunkChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_chunkChannel.Reader.TryRead(out var chunk))
            {
                yield return chunk;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _chunkChannel.Writer.TryComplete();
        FreeNativeResources();
        return ValueTask.CompletedTask;
    }

    [ExcludeFromCodeCoverage(Justification = "Native FFmpeg calls; exercised by Phase 12 integration tests.")]
    unsafe void FreeNativeResources()
    {
        // Drain any unconsumed pool frames. Safe to iterate without copying:
        // DisposeAsync sets `disposed = true` and completes the channel writer
        // before reaching here, so no concurrent AcquireFrameTexture can be
        // mutating the dictionary by this point. Clear() afterward removes the
        // dangling nint entries so a hypothetical re-entry is a no-op.
        foreach (var entry in _pendingPoolFramesByKey)
        {
            var pendingFrame = (AVFrame*)entry.Value;
            ffmpeg.av_frame_free(&pendingFrame);
        }
        _pendingPoolFramesByKey.Clear();

        if (_reusablePacketPointer != 0)
        {
            var packet = (AVPacket*)_reusablePacketPointer;
            ffmpeg.av_packet_free(&packet);
            _reusablePacketPointer = 0;
        }
        if (_stagingFramePointer != 0)
        {
            var frame = (AVFrame*)_stagingFramePointer;
            ffmpeg.av_frame_free(&frame);
            _stagingFramePointer = 0;
        }
        if (_codecContextPointer != 0)
        {
            var context = (AVCodecContext*)_codecContextPointer;
            ffmpeg.avcodec_free_context(&context);
            _codecContextPointer = 0;
        }
        if (_hardwareFramesContextReference != 0)
        {
            var reference = (AVBufferRef*)_hardwareFramesContextReference;
            ffmpeg.av_buffer_unref(&reference);
            _hardwareFramesContextReference = 0;
        }
        if (_hardwareDeviceContextReference != 0)
        {
            var reference = (AVBufferRef*)_hardwareDeviceContextReference;
            ffmpeg.av_buffer_unref(&reference);
            _hardwareDeviceContextReference = 0;
        }
        if (_ownsSharedDeviceManager && _sharedDeviceManager is not null)
        {
            _sharedDeviceManager.Dispose();
            _sharedDeviceManager = null;
            _ownsSharedDeviceManager = false;
        }
    }
}
#endif
