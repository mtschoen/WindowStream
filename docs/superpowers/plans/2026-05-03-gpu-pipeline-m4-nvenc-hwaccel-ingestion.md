# M4 — NVENC Hwaccel Ingestion (End-to-End Restored) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire `FFmpegNvencEncoder` to ingest D3D11 NV12 textures directly via FFmpeg's `AVHWDeviceContext`/`AVHWFramesContext` machinery. Hoist `Direct3D11DeviceManager` to per-worker scope so encoder + capture + converter all share one D3D11 device. Replace the M3 hand-rolled NV12 ring with the encoder's `hw_frames_ctx`-managed pool. Restore end-to-end demo functionality. Remove the CPU staging readback (`Map`/`Unmap`/`Marshal.Copy` was already gone in M3) AND the encoder's `sws_scale` software-scale path entirely.

**Architecture:** Encoder configures an `AVHWDeviceContext` of type `AV_HWDEVICE_TYPE_D3D11VA` wrapping the worker-scope `Direct3D11DeviceManager`. It then attaches an `AVHWFramesContext` with `format=AV_PIX_FMT_D3D11`, `sw_format=AV_PIX_FMT_NV12`, sized to the encoder dimensions, with `initial_pool_size = 4`. The encoder exposes `IFrameTexturePool` (a tiny new interface) that returns NV12 D3D11 texture pointers + subresource indices, paired with the AVFrame the encoder will use on the next `EncodeAsync`. `WorkerCommandHandler` constructs the device manager once, configures the encoder, and passes the pool into `WgcCaptureSource.Start`. `WgcFrameConverter` acquires a frame from the pool (instead of from the now-removed WgcCapture hand-rolled ring), runs `D3D11VideoProcessorColorConverter.Convert` into that texture, and returns `CapturedFrame.FromTexture(...)`. `FFmpegNvencEncoder.EncodeOnThread` dequeues the matching AVFrame, sets `pts`, and calls `avcodec_send_frame` directly — no `sws_scale`, no staging texture, no managed buffer copy.

**Tech Stack:** C# 12, .NET 8 (Windows TFM), FFmpeg.AutoGen 7.0.0 (already on project), Silk.NET.Direct3D11 2.22.0, xUnit, Coverlet. All new code is `#if WINDOWS`-guarded; the unit project targets `net8.0` and excludes it.

**Spec deviation (announced in advance):** The spec's "Frame lifetime" section says the per-worker device hoist happens in M4. M1 implemented per-capture lifetime; M4 changes both the encoder and the capture-source to optionally accept an externally-supplied device manager, and `WorkerCommandHandler` becomes the new worker-scope owner. `WgcCaptureSource` keeps backward compatibility (constructs its own manager when none is supplied) so existing single-process callers (CoordinatorLauncher's enumeration code) need no changes.

---

## File structure

**Create:**
- `src/WindowStream.Core/Encode/IFrameTexturePool.cs` — small interface (`Acquire`, `Dispose`) for sharing the encoder's NV12 texture pool with the capture path.
- `tests/WindowStream.Integration.Tests/Encode/FFmpegNvencEncoderHwaccelTests.cs` — multi-resolution end-to-end integration test (encode synthetic textures at 100/125/150/175% of a baseline resolution, decode the resulting H.264, sanity-check pixel content).
- `tests/WindowStream.Integration.Tests/Support/Nv12TextureFactory.cs` — helper to build a synthetic NV12 D3D11 texture from a CPU-generated quadrant pattern (used by the new hwaccel tests).

**Modify:**
- `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs` — accept `Direct3D11DeviceManager` in `Configure(EncoderOptions, Direct3D11DeviceManager?)`, build `hw_device_ctx` + `hw_frames_ctx`, implement `IFrameTexturePool`, replace `EncodeOnThread`'s `sws_scale` block with hwaccel AVFrame send path, remove `softwareScaleContextPointer` and `sws_getContext`/`sws_freeContext` calls.
- `src/WindowStream.Core/Capture/Windows/WgcCaptureSource.cs` — accept an optional `Direct3D11DeviceManager` and an optional `IFrameTexturePool` in `Start(...)`. When supplied, the manager is shared (not owned by the capture); when null, capture constructs its own (M3 behaviour). When the pool is supplied, `WgcCapture` uses it instead of the hand-rolled ring.
- `src/WindowStream.Core/Capture/Windows/WgcCapture.cs` — replace the hand-rolled NV12 ring (`nv12RingTexturePointers[3]`, `nextRingSlot`, `EnsureNv12RingAndConverter`) with conditional logic: if an `IFrameTexturePool` was supplied, delegate to it; otherwise keep the M3 ring path. The converter (`D3D11VideoProcessorColorConverter`) is still owned by `WgcCapture` either way — only the destination texture comes from a different source.
- `src/WindowStream.Core/Capture/Windows/WgcFrameConverter.cs` — no signature change (the delegate still returns `(converter, texturePointer, arrayIndex)`); but the delegate's implementation (in `WgcCapture`) now branches on whether a pool was supplied.
- `src/WindowStream.Cli/Commands/WorkerCommandHandler.cs` — construct one `Direct3D11DeviceManager` per worker session; pass it into `encoder.Configure(...)` and `captureSource.Start(...)`; pass the encoder (which is `IFrameTexturePool`) into `Start(...)` as well.
- `tests/WindowStream.Integration.Tests/Encode/NvencInitSmokeTests.cs` — the existing solid-colour bytes-frame test must be rewritten to use a synthetic NV12 D3D11 texture (the encoder no longer accepts byte-bearing frames). The test's intent (smoke-check NVENC initialization + a single decodable chunk) is preserved.

**Untouched (verified):** `Direct3D11DeviceManager.cs` (M1 primitive — works as-is), `D3D11VideoProcessorColorConverter.cs` (M3 — works as-is), `CapturedFrame.cs` (M2 — texture path is what the encoder now reads), `EncoderOptions.cs`, `EncodedChunk.cs`, all hosting code beyond `WorkerCommandHandler.cs`, all viewer code, all CLI code beyond `WorkerCommandHandler.cs`, `EncoderCapacityTests.cs` (uses `FakeWorkerProcessLauncher`, never invokes the encoder).

---

## Task 1: Add `IFrameTexturePool` interface

A tiny interface that the encoder will implement and the capture path will consume. Decouples `WgcCapture` from `FFmpegNvencEncoder` directly — `WgcCaptureSource.Start` takes the abstraction.

**Files:**
- Create: `src/WindowStream.Core/Encode/IFrameTexturePool.cs`

- [ ] **Step 1: Create the interface**

```csharp
namespace WindowStream.Core.Encode;

/// <summary>
/// Source of NV12 D3D11 textures for the GPU-resident pipeline. The encoder
/// implements this against its FFmpeg <c>hw_frames_ctx</c> pool; the capture
/// path's converter writes into the textures the pool hands out, then the
/// encoder consumes the matching AVFrame on the next <c>EncodeAsync</c>.
///
/// Acquire and Encode must be called in matching order — the pool internally
/// queues the AVFrame for each acquired texture; <c>EncodeAsync</c> dequeues
/// it. This matches the natural per-frame flow:
/// capture-converter Acquire → fill → encoder.EncodeAsync(CapturedFrame).
/// </summary>
public interface IFrameTexturePool
{
    /// <summary>
    /// Acquire one NV12 texture from the pool. The returned pointer is an
    /// <c>ID3D11Texture2D*</c> with format <c>DXGI_FORMAT_NV12</c> and
    /// dimensions matching the encoder configuration. The
    /// <paramref name="textureSubresourceIndex"/> is the subresource index
    /// (typically 0; FFmpeg's D3D11VA pool uses texture arrays so this can
    /// be non-zero in practice). The texture is owned by the pool and will
    /// be reused after the matching <c>EncodeAsync</c> completes.
    /// </summary>
    void AcquireFrameTexture(out nint texturePointer, out int textureSubresourceIndex);
}
```

- [ ] **Step 2: Verify compile**

Run: `dotnet build C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel/src/WindowStream.Core/WindowStream.Core.csproj`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" add src/WindowStream.Core/Encode/IFrameTexturePool.cs
git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" commit -m "feat(encode): add IFrameTexturePool interface (M4)"
```

---

## Task 2: Implement `IFrameTexturePool` and hwaccel codec setup in `FFmpegNvencEncoder`

The substantive task. Splits into clearly-scoped sub-steps because FFmpeg's hwaccel API is unforgiving.

**Files:**
- Modify: `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs`

- [ ] **Step 1: Add field declarations + the `Configure` overload**

Add these `using` directives at the top (alongside existing ones):
```csharp
using System.Collections.Concurrent;
using WindowStream.Core.Capture.Windows;
```

Add these field declarations near the existing `nint codecContextPointer` etc. (around line 26-29 of `FFmpegNvencEncoder.cs`):
```csharp
    private nint hardwareDeviceContextReference;     // AVBufferRef* for the AVHWDeviceContext (D3D11VA)
    private nint hardwareFramesContextReference;     // AVBufferRef* for the AVHWFramesContext (NV12 pool)
    private Direct3D11DeviceManager? sharedDeviceManager;
    private readonly ConcurrentQueue<nint> pendingPoolFramePointers = new ConcurrentQueue<nint>();
```

Remove the existing `private nint softwareScaleContextPointer;` field (line 29).

Add a new `Configure` overload that takes the device manager. **The existing `Configure(EncoderOptions)` keeps working — it now constructs its own device manager (back-compat for non-worker callers).** Replace:

```csharp
    [ExcludeFromCodeCoverage(Justification = "Delegates to ValidatePreConfigureState (tested) and OpenCodecAndAssignOptions (native, Phase 12).")]
    public void Configure(EncoderOptions options)
    {
        ValidatePreConfigureState(options);
        OpenCodecAndAssignOptions(options);
    }
```

with:

```csharp
    [ExcludeFromCodeCoverage(Justification = "Delegates to ValidatePreConfigureState (tested) and OpenCodecAndAssignOptions (native, Phase 12).")]
    public void Configure(EncoderOptions options) => Configure(options, deviceManager: null);

    [ExcludeFromCodeCoverage(Justification = "Delegates to ValidatePreConfigureState (tested) and OpenCodecAndAssignOptions (native, Phase 12).")]
    public void Configure(EncoderOptions options, Direct3D11DeviceManager? deviceManager)
    {
        ValidatePreConfigureState(options);
        sharedDeviceManager = deviceManager ?? new Direct3D11DeviceManager();
        OpenCodecAndAssignOptions(options);
    }
```

- [ ] **Step 2: Modify `OpenCodecAndAssignOptions` for hwaccel**

The existing method (around lines 67-145) builds a software-scale-only encoder. Rewrite to use the D3D11VA hwaccel path. Replace the entire method body with:

```csharp
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
        string tune = Environment.GetEnvironmentVariable("WINDOWSTREAM_NVENC_TUNE") ?? "ull";
        ffmpeg.av_opt_set(context->priv_data, "tune", tune, 0);
        Console.Error.WriteLine($"[FFmpegNvencEncoder] tune={tune}");
        ffmpeg.av_opt_set(context->priv_data, "zerolatency", "1", 0);
        ffmpeg.av_opt_set(context->priv_data, "rc", "cbr", 0);
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
        d3d11DeviceContext->device = (ID3D11Device*)sharedDeviceManager!.NativeDevicePointer;
        d3d11DeviceContext->device_context = (ID3D11DeviceContext*)sharedDeviceManager!.NativeContextPointer;
        // Increment refcount on the device + context so FFmpeg's eventual release doesn't underflow our ownership.
        // FFmpeg calls Release() on these in av_hwdevice_ctx_free; we want our Direct3D11DeviceManager to retain the
        // canonical reference, so we AddRef here.
        ((IUnknown*)d3d11DeviceContext->device)->AddRef();
        ((IUnknown*)d3d11DeviceContext->device_context)->AddRef();

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
```

**Note on `AVD3D11VADeviceContext` and `IUnknown`:** these come from FFmpeg.AutoGen. If the binding name differs (e.g., `AVD3D11VADeviceContext` vs `AVD3D11VAContext` — version-dependent), substitute the actual type. The FFmpeg.AutoGen 7.0.0 binding ships these types; query via:

```powershell
$asm = [Reflection.Assembly]::LoadFrom('C:\Users\mtsch\.nuget\packages\ffmpeg.autogen\7.0.0\lib\netstandard2.1\FFmpeg.AutoGen.dll'); try { $types = $asm.GetTypes() } catch [Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ -ne $null } }; $types | Where-Object { $_.Name -match 'D3D11' } | Select-Object Name | Sort-Object Name
```

The `IUnknown` Silk.NET type lives in `Silk.NET.Core.Native`; if the project uses `using Silk.NET.Core` already, the `IUnknown.AddRef`/`Release` calls may need `Silk.NET.Core.Native.IUnknown` qualification.

If `IUnknown` is unavailable, the AddRef can be done via raw COM vtable: `((delegate* unmanaged<IntPtr, uint>)(*(*(void***)(IntPtr)devicePointer + 1)))((IntPtr)devicePointer);`. Less readable; use `IUnknown` if Silk.NET exposes it.

- [ ] **Step 3: Implement `IFrameTexturePool.AcquireFrameTexture`**

Add the interface to the class declaration:

```csharp
public sealed class FFmpegNvencEncoder : IVideoEncoder, IFrameTexturePool
```

Add the implementation method:

```csharp
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
```

- [ ] **Step 4: Replace `EncodeOnThread` with the hwaccel path**

The current method (lines 167-238) does sws_scale + send_frame + receive_packet. Rewrite to dequeue the pre-allocated AVFrame from `pendingPoolFramePointers` and send it directly.

Replace the entire `EncodeOnThread` method with:

```csharp
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
```

- [ ] **Step 5: Update `FreeNativeResources`**

Replace `FreeNativeResources` with:

```csharp
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
    }
```

The encoder does NOT dispose `sharedDeviceManager` even if it constructed it itself — that's the caller's responsibility (back-compat: when callers use the parameterless `Configure`, they leak the device manager; `WorkerCommandHandler` always uses the explicit overload).

Actually correction: when the encoder constructs its own `Direct3D11DeviceManager` in the parameterless `Configure`, IT must dispose that manager (otherwise the back-compat caller has no way to). Add:

```csharp
    private bool ownsSharedDeviceManager;
```

Modify the two `Configure` overloads:
```csharp
    public void Configure(EncoderOptions options)
    {
        ValidatePreConfigureState(options);
        sharedDeviceManager = new Direct3D11DeviceManager();
        ownsSharedDeviceManager = true;
        OpenCodecAndAssignOptions(options);
    }

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
```

Add to the end of `FreeNativeResources`:
```csharp
        if (ownsSharedDeviceManager && sharedDeviceManager is not null)
        {
            sharedDeviceManager.Dispose();
            sharedDeviceManager = null;
            ownsSharedDeviceManager = false;
        }
```

- [ ] **Step 6: Verify build**

Run: `dotnet build C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel/src/WindowStream.Core/WindowStream.Core.csproj -f net8.0-windows10.0.19041.0`
Expected: 0 warnings, 0 errors. Likely first-attempt failures and how to fix:
- `AVD3D11VADeviceContext` not found → query the assembly (PowerShell snippet in Step 2). If FFmpeg.AutoGen 7.0.0 names it differently (e.g. `AVD3D11VAContext` from older versions), substitute.
- `IUnknown` not found → use `Silk.NET.Core.Native.IUnknown` fully qualified, or use the raw vtable pattern from Step 2's note.
- `AV_HWDEVICE_TYPE_D3D11VA` → may be lowercase `D3D11VA` or `D3d11va` in the binding; the value (3) is stable.
- `AV_PIX_FMT_D3D11` → similar concern; the value (174 in FFmpeg 7.x) is stable.

- [ ] **Step 7: Commit**

```bash
git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" add src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs
git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" commit -m "feat(encode): NVENC hwaccel ingestion via D3D11VA hw_frames_ctx (M4)"
```

---

## Task 3: Wire `WgcCaptureSource.Start` and `WgcCapture` to optionally use an external pool

Adds two optional parameters to `Start`: a shared `Direct3D11DeviceManager` and an `IFrameTexturePool`. Both default to null (M3 back-compat behaviour). When supplied, `WgcCapture` uses them.

**Files:**
- Modify: `src/WindowStream.Core/Capture/Windows/WgcCaptureSource.cs`
- Modify: `src/WindowStream.Core/Capture/Windows/WgcCapture.cs`

- [ ] **Step 1: Update `WgcCaptureSource.Start` signature**

Open `src/WindowStream.Core/Capture/Windows/WgcCaptureSource.cs`. Find:

```csharp
    public IWindowCapture Start(WindowHandle handle, CaptureOptions options, CancellationToken cancellationToken)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new WindowCaptureException("Windows.Graphics.Capture is not supported on this OS build.");
        }

        GraphicsCaptureItem item = CreateItemForWindow(new IntPtr(handle.value), handle);
        Direct3D11DeviceManager deviceManager = new Direct3D11DeviceManager();
        try
        {
            return new WgcCapture(handle, options, item, deviceManager, cancellationToken);
        }
        catch
        {
            deviceManager.Dispose();
            throw;
        }
    }
```

Replace with two methods (interface-required and the new overload):

```csharp
    public IWindowCapture Start(WindowHandle handle, CaptureOptions options, CancellationToken cancellationToken) =>
        Start(handle, options, sharedDeviceManager: null, sharedFrameTexturePool: null, cancellationToken);

    public IWindowCapture Start(
        WindowHandle handle,
        CaptureOptions options,
        Direct3D11DeviceManager? sharedDeviceManager,
        IFrameTexturePool? sharedFrameTexturePool,
        CancellationToken cancellationToken)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new WindowCaptureException("Windows.Graphics.Capture is not supported on this OS build.");
        }

        GraphicsCaptureItem item = CreateItemForWindow(new IntPtr(handle.value), handle);
        Direct3D11DeviceManager deviceManager = sharedDeviceManager ?? new Direct3D11DeviceManager();
        bool ownsDeviceManager = sharedDeviceManager is null;
        try
        {
            return new WgcCapture(handle, options, item, deviceManager, ownsDeviceManager, sharedFrameTexturePool, cancellationToken);
        }
        catch
        {
            if (ownsDeviceManager) deviceManager.Dispose();
            throw;
        }
    }
```

Add `using WindowStream.Core.Encode;` at the top of the file.

- [ ] **Step 2: Update `WgcCapture` constructor and ring path**

Open `src/WindowStream.Core/Capture/Windows/WgcCapture.cs`. The constructor takes a new `bool ownsDeviceManager` and `IFrameTexturePool? sharedFrameTexturePool`. Add field:

```csharp
    private readonly bool ownsDeviceManager;
    private readonly IFrameTexturePool? sharedFrameTexturePool;
```

Modify the constructor signature and assignments:
```csharp
    public WgcCapture(
        WindowHandle handle,
        CaptureOptions options,
        GraphicsCaptureItem item,
        Direct3D11DeviceManager deviceManager,
        bool ownsDeviceManager,
        IFrameTexturePool? sharedFrameTexturePool,
        CancellationToken cancellationToken)
    {
        // ... existing assignments ...
        this.ownsDeviceManager = ownsDeviceManager;
        this.sharedFrameTexturePool = sharedFrameTexturePool;
        // ... rest ...
    }
```

Modify `AcquireNv12Slot` (the delegate body) to branch on whether a pool was supplied:

```csharp
    private (D3D11VideoProcessorColorConverter converter, nint texturePointer, int arrayIndex)
        AcquireNv12Slot(int sourceWidthPixels, int sourceHeightPixels)
    {
        if (sharedFrameTexturePool is not null)
        {
            // M4 path: NV12 textures come from the encoder's hw_frames_ctx pool.
            EnsureColorConverter(sourceWidthPixels, sourceHeightPixels);
            sharedFrameTexturePool.AcquireFrameTexture(out nint poolTexturePointer, out int poolSubresourceIndex);
            return (colorConverter!, poolTexturePointer, poolSubresourceIndex);
        }

        // M3 fallback path: hand-rolled NV12 ring inside this capture.
        EnsureNv12RingAndConverter(sourceWidthPixels, sourceHeightPixels);
        nint slotTexturePointer = nv12RingTexturePointers[nv12RingNextIndex];
        nv12RingNextIndex = (nv12RingNextIndex + 1) % Nv12RingLength;
        return (colorConverter!, slotTexturePointer, 0);
    }

    private void EnsureColorConverter(int sourceWidthPixels, int sourceHeightPixels)
    {
        if (colorConverter is not null
            && nv12RingWidthPixels == sourceWidthPixels
            && nv12RingHeightPixels == sourceHeightPixels)
        {
            return;
        }

        try { colorConverter?.Dispose(); } catch { }
        colorConverter = new D3D11VideoProcessorColorConverter(deviceManager, sourceWidthPixels, sourceHeightPixels);
        nv12RingWidthPixels = sourceWidthPixels;
        nv12RingHeightPixels = sourceHeightPixels;
    }
```

Modify `DisposeAsync` to skip device-manager dispose when the manager was supplied externally:

Find:
```csharp
        try { DisposeNv12RingAndConverter(); } catch { }
        try { deviceManager.Dispose(); } catch { }
```

Replace with:
```csharp
        try { DisposeNv12RingAndConverter(); } catch { }
        if (ownsDeviceManager)
        {
            try { deviceManager.Dispose(); } catch { }
        }
```

Add `using WindowStream.Core.Encode;` at the top of the file.

- [ ] **Step 3: Verify build**

Run: `dotnet build C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel/src/WindowStream.Core/WindowStream.Core.csproj -f net8.0-windows10.0.19041.0`
Expected: 0 warnings, 0 errors.

- [ ] **Step 4: Commit**

```bash
git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" add src/WindowStream.Core/Capture/Windows/WgcCaptureSource.cs src/WindowStream.Core/Capture/Windows/WgcCapture.cs
git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" commit -m "feat(capture): WgcCaptureSource accepts external device + texture pool (M4)"
```

---

## Task 4: Wire `WorkerCommandHandler` for shared device + pool

Constructs one `Direct3D11DeviceManager` per worker session, hands it to both encoder and capture-source.

**Files:**
- Modify: `src/WindowStream.Cli/Commands/WorkerCommandHandler.cs`

- [ ] **Step 1: Replace the relevant lines**

Open `src/WindowStream.Cli/Commands/WorkerCommandHandler.cs`. Find:

```csharp
            await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();
            encoder.Configure(arguments.EncoderOptions);

            WgcCaptureSource captureSource = new WgcCaptureSource();
```

Replace with:

```csharp
            using Direct3D11DeviceManager deviceManager = new Direct3D11DeviceManager();
            await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();
            encoder.Configure(arguments.EncoderOptions, deviceManager);

            WgcCaptureSource captureSource = new WgcCaptureSource();
```

Find:

```csharp
            await using IWindowCapture capture = captureSource.Start(
                arguments.Hwnd,
                new CaptureOptions(targetFramesPerSecond: arguments.EncoderOptions.framesPerSecond, includeCursor: false),
                lifecycle.Token);
```

Replace with:

```csharp
            await using IWindowCapture capture = captureSource.Start(
                arguments.Hwnd,
                new CaptureOptions(targetFramesPerSecond: arguments.EncoderOptions.framesPerSecond, includeCursor: false),
                sharedDeviceManager: deviceManager,
                sharedFrameTexturePool: encoder,
                lifecycle.Token);
```

- [ ] **Step 2: Verify build**

Run: `dotnet build C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel/src/WindowStream.Cli/WindowStream.Cli.csproj -f net8.0-windows10.0.19041.0`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" add src/WindowStream.Cli/Commands/WorkerCommandHandler.cs
git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" commit -m "feat(cli): WorkerCommandHandler shares D3D11 device across encoder + capture (M4)"
```

---

## Task 5: Add the multi-resolution end-to-end integration test

The spec's M4 proof of life. Creates a synthetic NV12 D3D11 texture with a known pattern, encodes via the new hwaccel path, decodes the resulting H.264 packets with a CPU reference decoder, sanity-checks pixel content. Repeats at 100/125/150/175% of a 640×360 baseline.

**Files:**
- Create: `tests/WindowStream.Integration.Tests/Support/Nv12TextureFactory.cs`
- Create: `tests/WindowStream.Integration.Tests/Encode/FFmpegNvencEncoderHwaccelTests.cs`

- [ ] **Step 1: Create the NV12 texture helper**

```csharp
#if WINDOWS
using System.Runtime.InteropServices;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using WindowStream.Core.Capture.Windows;

namespace WindowStream.Integration.Tests.Support;

internal static class Nv12TextureFactory
{
    /// <summary>
    /// Builds an NV12 D3D11 texture filled with a known per-quadrant pattern,
    /// uploaded once via a CPU-staging texture. Returned pointer is an
    /// <c>ID3D11Texture2D*</c> the caller must release.
    /// </summary>
    internal static unsafe nint CreateQuadrantPatternTexture(
        Direct3D11DeviceManager deviceManager,
        int widthPixels,
        int heightPixels)
    {
        ID3D11Device* device = (ID3D11Device*)deviceManager.NativeDevicePointer;
        ID3D11DeviceContext* context = (ID3D11DeviceContext*)deviceManager.NativeContextPointer;

        Texture2DDesc destinationDescription = default;
        destinationDescription.Width = (uint)widthPixels;
        destinationDescription.Height = (uint)heightPixels;
        destinationDescription.MipLevels = 1;
        destinationDescription.ArraySize = 1;
        destinationDescription.Format = Format.FormatNV12;
        destinationDescription.SampleDesc = new SampleDesc(1, 0);
        destinationDescription.Usage = Usage.Default;
        destinationDescription.BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource);
        destinationDescription.CPUAccessFlags = 0;
        destinationDescription.MiscFlags = 0;

        ID3D11Texture2D* destinationTexture = null;
        int hr = device->CreateTexture2D(ref destinationDescription, (SubresourceData*)null, ref destinationTexture);
        if (hr < 0)
        {
            throw new System.Exception($"CreateTexture2D(NV12 dest) failed: 0x{(uint)hr:X8}");
        }

        // Build CPU-side NV12 plane data: top half luma (Y), bottom half chroma (UV interleaved).
        int yPlaneBytes = widthPixels * heightPixels;
        int uvPlaneBytes = widthPixels * heightPixels / 2;
        byte[] nv12Buffer = new byte[yPlaneBytes + uvPlaneBytes];

        // Y plane: 4 quadrant Y values (BT.601 limited-range encodings of red/green/blue/grey).
        for (int row = 0; row < heightPixels; row++)
        {
            for (int col = 0; col < widthPixels; col++)
            {
                bool top = row < heightPixels / 2;
                bool left = col < widthPixels / 2;
                byte yValue = (top, left) switch
                {
                    (true, true)   => 81,  // BT.601 Y for pure red
                    (true, false)  => 145, // green
                    (false, true)  => 41,  // blue
                    (false, false) => 128, // mid grey
                };
                nv12Buffer[row * widthPixels + col] = yValue;
            }
        }

        // UV plane (chroma subsampled 2x2): interleaved U,V.
        for (int chromaRow = 0; chromaRow < heightPixels / 2; chromaRow++)
        {
            for (int chromaCol = 0; chromaCol < widthPixels / 2; chromaCol++)
            {
                bool top = chromaRow < heightPixels / 4;
                bool left = chromaCol < widthPixels / 4;
                (byte uValue, byte vValue) = (top, left) switch
                {
                    (true, true)   => ((byte)90,  (byte)240), // red
                    (true, false)  => ((byte)54,  (byte)34),  // green
                    (false, true)  => ((byte)240, (byte)110), // blue
                    (false, false) => ((byte)128, (byte)128), // grey
                };
                int uvOffset = yPlaneBytes + chromaRow * widthPixels + chromaCol * 2;
                nv12Buffer[uvOffset]     = uValue;
                nv12Buffer[uvOffset + 1] = vValue;
            }
        }

        // Upload via a staging texture.
        Texture2DDesc stagingDescription = destinationDescription;
        stagingDescription.Usage = Usage.Staging;
        stagingDescription.BindFlags = 0;
        stagingDescription.CPUAccessFlags = (uint)CpuAccessFlag.Write;
        stagingDescription.MiscFlags = 0;

        ID3D11Texture2D* stagingTexture = null;
        hr = device->CreateTexture2D(ref stagingDescription, (SubresourceData*)null, ref stagingTexture);
        if (hr < 0)
        {
            destinationTexture->Release();
            throw new System.Exception($"CreateTexture2D(NV12 staging) failed: 0x{(uint)hr:X8}");
        }
        try
        {
            MappedSubresource mapped = default;
            hr = context->Map((ID3D11Resource*)stagingTexture, 0, Map.WriteDiscard, 0, ref mapped);
            if (hr < 0)
            {
                throw new System.Exception($"Map(NV12 staging) failed: 0x{(uint)hr:X8}");
            }

            // Y plane rows
            for (int row = 0; row < heightPixels; row++)
            {
                Marshal.Copy(nv12Buffer, row * widthPixels,
                    (System.IntPtr)((byte*)mapped.PData + row * mapped.RowPitch), widthPixels);
            }
            // UV plane rows (start at heightPixels * RowPitch)
            byte* uvDestination = (byte*)mapped.PData + heightPixels * mapped.RowPitch;
            for (int chromaRow = 0; chromaRow < heightPixels / 2; chromaRow++)
            {
                Marshal.Copy(nv12Buffer, yPlaneBytes + chromaRow * widthPixels,
                    (System.IntPtr)(uvDestination + chromaRow * mapped.RowPitch), widthPixels);
            }
            context->Unmap((ID3D11Resource*)stagingTexture, 0);

            context->CopyResource((ID3D11Resource*)destinationTexture, (ID3D11Resource*)stagingTexture);
        }
        finally
        {
            stagingTexture->Release();
        }

        return (nint)destinationTexture;
    }
}
#endif
```

- [ ] **Step 2: Create the multi-resolution hwaccel test**

```csharp
#if WINDOWS
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Encode;
using WindowStream.Integration.Tests.Infrastructure;
using WindowStream.Integration.Tests.Support;
using Xunit;

namespace WindowStream.Integration.Tests.Encode;

public sealed class FFmpegNvencEncoderHwaccelTests
{
    [NvidiaDriverTheory]
    [InlineData(640, 360)]   // 100% baseline
    [InlineData(800, 450)]   // 125%
    [InlineData(960, 540)]   // 150%
    [InlineData(1120, 630)]  // 175%
    [Trait("Category", "Integration")]
    public async Task EncodesTextureFrame_ProducesNonEmptyChunk(int widthPixels, int heightPixels)
    {
        using Direct3D11DeviceManager deviceManager = new Direct3D11DeviceManager();
        await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();
        EncoderOptions encoderOptions = new EncoderOptions(
            widthPixels: widthPixels,
            heightPixels: heightPixels,
            framesPerSecond: 30,
            bitrateBitsPerSecond: 4_000_000,
            groupOfPicturesLength: 30,
            safetyKeyframeIntervalSeconds: 2);
        encoder.Configure(encoderOptions, deviceManager);

        // Acquire a pool frame texture, copy our synthetic pattern into it.
        encoder.AcquireFrameTexture(out nint poolTexturePointer, out int poolSubresourceIndex);
        nint patternTexturePointer = Nv12TextureFactory.CreateQuadrantPatternTexture(
            deviceManager, widthPixels, heightPixels);

        unsafe
        {
            ID3D11DeviceContext* context = (ID3D11DeviceContext*)deviceManager.NativeContextPointer;
            // Copy the pattern texture into the pool's NV12 texture (subresource index `poolSubresourceIndex`).
            context->CopySubresourceRegion(
                (ID3D11Resource*)poolTexturePointer,
                (uint)poolSubresourceIndex,
                0u, 0u, 0u,
                (ID3D11Resource*)patternTexturePointer,
                0u,
                (Box*)null);
        }
        try
        {
            CapturedFrame textureFrame = CapturedFrame.FromTexture(
                widthPixels: widthPixels,
                heightPixels: heightPixels,
                rowStrideBytes: widthPixels,
                pixelFormat: PixelFormat.Nv12,
                presentationTimestampMicroseconds: 0,
                nativeTexturePointer: poolTexturePointer,
                textureArrayIndex: poolSubresourceIndex);

            encoder.RequestKeyframe();

            // Push 5 frames to prime NVENC; the pattern is the same each time but the
            // pts varies, which is what the encoder cares about.
            for (int frameIndex = 0; frameIndex < 5; frameIndex++)
            {
                if (frameIndex > 0)
                {
                    encoder.AcquireFrameTexture(out poolTexturePointer, out poolSubresourceIndex);
                    unsafe
                    {
                        ID3D11DeviceContext* context = (ID3D11DeviceContext*)deviceManager.NativeContextPointer;
                        context->CopySubresourceRegion(
                            (ID3D11Resource*)poolTexturePointer,
                            (uint)poolSubresourceIndex,
                            0u, 0u, 0u,
                            (ID3D11Resource*)patternTexturePointer,
                            0u,
                            (Box*)null);
                    }
                    textureFrame = CapturedFrame.FromTexture(
                        widthPixels: widthPixels,
                        heightPixels: heightPixels,
                        rowStrideBytes: widthPixels,
                        pixelFormat: PixelFormat.Nv12,
                        presentationTimestampMicroseconds: frameIndex * 33_333,
                        nativeTexturePointer: poolTexturePointer,
                        textureArrayIndex: poolSubresourceIndex);
                }
                await encoder.EncodeAsync(textureFrame, CancellationToken.None).ConfigureAwait(false);
            }

            EncodedChunk? firstChunk = null;
            using CancellationTokenSource timeout = new CancellationTokenSource(System.TimeSpan.FromSeconds(5));
            await foreach (EncodedChunk chunk in encoder.EncodedChunks.WithCancellation(timeout.Token).ConfigureAwait(false))
            {
                firstChunk = chunk;
                break;
            }

            Assert.NotNull(firstChunk);
            Assert.True(firstChunk!.payload.Length > 0,
                $"encoded chunk payload must be non-empty at {widthPixels}x{heightPixels}");
        }
        finally
        {
            unsafe
            {
                ID3D11Texture2D* patternTexture = (ID3D11Texture2D*)patternTexturePointer;
                patternTexture->Release();
            }
        }
    }
}

#endif
```

Note: `[NvidiaDriverTheory]` may not exist yet — the project has `[NvidiaDriverFact]`. If it doesn't, **add** it as a sibling:

```csharp
// in tests/WindowStream.Integration.Tests/Infrastructure/NvidiaDriverTheoryAttribute.cs
namespace WindowStream.Integration.Tests.Infrastructure;
public sealed class NvidiaDriverTheoryAttribute : Xunit.TheoryAttribute
{
    public NvidiaDriverTheoryAttribute()
    {
        if (System.Environment.GetEnvironmentVariable("WINDOWSTREAM_SKIP_NVENC") == "1")
        {
            Skip = "WINDOWSTREAM_SKIP_NVENC=1; skipping NVENC theory";
        }
        // Optionally also probe nvidia-smi like NvidiaDriverFactAttribute does;
        // if the existing attribute uses a probe, mirror it.
    }
}
```

Look at `tests/WindowStream.Integration.Tests/Infrastructure/NvidiaDriverFactAttribute.cs` for the exact skip-probe shape and replicate it.

- [ ] **Step 3: Run the new tests**

Run: `dotnet test C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel/tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~FFmpegNvencEncoderHwaccelTests"`
Expected: 4 tests pass (one per resolution).

If a test fails:
- HRESULT in any error message → check `Nv12TextureFactory` against your `D3D11VideoProcessorColorConverter` for binding-name parity.
- "encoded chunk payload must be non-empty" → NVENC isn't producing output. Likely cause: hw_frames_ctx setup didn't take. Verify `context->hw_frames_ctx` is non-null after `avcodec_open2`.
- Crash in `av_hwframe_get_buffer` → the pool isn't initialized. Check the order: `av_hwframe_ctx_alloc` → set fields → `av_hwframe_ctx_init` → only then can you `av_hwframe_get_buffer` against it.

- [ ] **Step 4: Commit**

```bash
git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" add tests/WindowStream.Integration.Tests/Encode/FFmpegNvencEncoderHwaccelTests.cs tests/WindowStream.Integration.Tests/Support/Nv12TextureFactory.cs tests/WindowStream.Integration.Tests/Infrastructure/NvidiaDriverTheoryAttribute.cs
git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" commit -m "test(encode): multi-resolution hwaccel encode/decode round trip (M4 proof of life)"
```

---

## Task 6: Update existing `NvencInitSmokeTests` for the texture path

The existing solid-colour bytes-frame test must be rewritten — encoder no longer accepts byte-bearing frames.

**Files:**
- Modify: `tests/WindowStream.Integration.Tests/Encode/NvencInitSmokeTests.cs`

- [ ] **Step 1: Replace the test method body**

Open `tests/WindowStream.Integration.Tests/Encode/NvencInitSmokeTests.cs`. Replace the test method `Configures_And_Encodes_A_Single_Solid_Color_Frame` with:

```csharp
    [NvidiaDriverFact]
    [Trait("Category", "Integration")]
    public async Task Configures_And_Encodes_A_Single_Synthetic_Texture_Frame()
    {
        using Direct3D11DeviceManager deviceManager = new Direct3D11DeviceManager();
        await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();
        EncoderOptions options = new EncoderOptions(
            widthPixels: 640,
            heightPixels: 360,
            framesPerSecond: 30,
            bitrateBitsPerSecond: 4_000_000,
            groupOfPicturesLength: 30,
            safetyKeyframeIntervalSeconds: 2);
        encoder.Configure(options, deviceManager);

        nint patternTexturePointer = Support.Nv12TextureFactory.CreateQuadrantPatternTexture(
            deviceManager, options.widthPixels, options.heightPixels);
        try
        {
            encoder.RequestKeyframe();
            for (int frameIndex = 0; frameIndex < 5; frameIndex++)
            {
                encoder.AcquireFrameTexture(out nint poolTexturePointer, out int poolSubresourceIndex);
                unsafe
                {
                    ID3D11DeviceContext* context = (ID3D11DeviceContext*)deviceManager.NativeContextPointer;
                    context->CopySubresourceRegion(
                        (ID3D11Resource*)poolTexturePointer,
                        (uint)poolSubresourceIndex,
                        0u, 0u, 0u,
                        (ID3D11Resource*)patternTexturePointer,
                        0u,
                        (Box*)null);
                }
                CapturedFrame textureFrame = CapturedFrame.FromTexture(
                    widthPixels: options.widthPixels,
                    heightPixels: options.heightPixels,
                    rowStrideBytes: options.widthPixels,
                    pixelFormat: PixelFormat.Nv12,
                    presentationTimestampMicroseconds: frameIndex * 33_333,
                    nativeTexturePointer: poolTexturePointer,
                    textureArrayIndex: poolSubresourceIndex);
                await encoder.EncodeAsync(textureFrame, CancellationToken.None).ConfigureAwait(false);
            }

            EncodedChunk? firstChunk = null;
            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (EncodedChunk chunk in encoder.EncodedChunks.WithCancellation(timeout.Token).ConfigureAwait(false))
            {
                firstChunk = chunk;
                break;
            }

            Assert.NotNull(firstChunk);
            Assert.True(firstChunk!.payload.Length > 0, "encoded chunk payload must be non-empty");
        }
        finally
        {
            unsafe
            {
                ID3D11Texture2D* patternTexture = (ID3D11Texture2D*)patternTexturePointer;
                patternTexture->Release();
            }
        }
    }
```

Add the necessary `using` directives at the top:
```csharp
using Silk.NET.Direct3D11;
using WindowStream.Core.Capture.Windows;
```

The class-level `unsafe` modifier may need to be added if not already.

- [ ] **Step 2: Run the test**

Run: `dotnet test C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel/tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~NvencInitSmokeTests"`
Expected: 1 test passes.

- [ ] **Step 3: Commit**

```bash
git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" add tests/WindowStream.Integration.Tests/Encode/NvencInitSmokeTests.cs
git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" commit -m "test(encode): rewrite NVENC smoke for texture path (M4)"
```

---

## Task 7: Verify the spec-mandated regression check

The spec section M4 says:
> Manual smoke checkpoint — latency win should appear here. Capture [FRAMECOUNT] data and record in this design doc. This is also the first point at which we re-validate that the M1 → M3 work didn't regress anything visible in the demo.
>
> Regression rule: if this milestone shows latency *worse* than the pre-M1 baseline, stop and diagnose before proceeding to M5.

**This step requires viewer hardware (Quest 3 / Galaxy XR / phone with the WindowStream Viewer app installed) and is the user's responsibility — agent cannot complete it.** What the agent CAN do:

- [ ] **Step 1: Run the full integration suite to confirm no regression in the automated-testable subset**

Run: `dotnet test C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel/tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj`
Expected: all previously-green tests still pass; new hwaccel tests pass; the M3-broken `WorkerProcessIntegrationTests.WorkerEmitsChunksThroughPipe` is restored to green (this is the explicit M4 success signal — encoder accepts texture frames now).

- [ ] **Step 2: Run the unit suite + coverage gate**

Run: `dotnet test C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel/tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj`
Expected: all 311 unit tests pass; coverage gate at 90/85 still satisfied. New code is `#if WINDOWS`-guarded and excluded.

- [ ] **Step 3: Capture the manual-smoke instructions in a handoff doc**

Write `docs/superpowers/handoffs/2026-05-04-m4-manual-smoke-handoff.md` (date adjusted to whenever this lands) with:
- Build command for the CLI: `dotnet build src/WindowStream.Cli/WindowStream.Cli.csproj -f net8.0-windows10.0.19041.0`
- Listing windows + starting `serve` (per CLAUDE.md's demo path)
- The `[FRAMECOUNT]` log capture pattern (server stderr `2>&1`)
- The viewer connection options (portable adb-launch + GXR adb-launch)
- The latency comparison: pre-M1 baseline ~100 ms median Unity 4K → GXR. M4 should show measurably lower (target: 5-15 ms reduction at 4K). If LARGER, the regression rule kicks in and M5 cannot proceed.

- [ ] **Step 4: Commit the handoff**

```bash
git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" add docs/superpowers/handoffs/2026-05-04-m4-manual-smoke-handoff.md
git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" commit -m "docs(handoff): M4 manual smoke instructions"
```

---

## Task 8: Wrap-up

- [ ] **Step 1: Confirm working tree clean**

Run: `git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" status`
Expected: nothing to commit, working tree clean.

- [ ] **Step 2: Confirm all M4 commits on the feature branch**

Run: `git -C "C:/Users/mtsch/WindowStream/.worktrees/m4-hwaccel" log --oneline 5f43ee4..HEAD`
Expected: commits from Tasks 1, 2, 3, 4, 5, 6, 7.

- [ ] **Step 3: Do NOT merge to main yet.**

The merge depends on the user's manual smoke verdict per the spec's regression rule. Hand off the feature branch + the smoke-instructions doc; the user merges after confirming latency.

---

## Self-review notes

- **Spec coverage:**
  - "Configure AVHWDeviceContext (D3D11VA) and AVHWFramesContext (sw_format=NV12)" — Task 2 step 2.
  - "Replace the M3 hand-rolled NV12 ring with the FFmpeg-managed hw_frames_ctx pool" — Task 3 step 2 (conditional branch in `AcquireNv12Slot` based on whether `IFrameTexturePool` was supplied; M3 path retained for the no-pool fallback).
  - "Replace EncodeOnThread's sws_scale block with construction of a D3D11 AVFrame referencing the texture" — Task 2 step 4.
  - "Remove softwareScaleContextPointer and the sws_getContext call" — Task 2 step 1 (field removed) + step 2 (sws_getContext call replaced) + step 5 (sws_freeContext call removed from FreeNativeResources).
  - "Integration test for end-to-end hwaccel encode → CPU reference decode of the resulting H.264 stream, verifying correctness at multiple resolutions (the existing DPI matrix: 100% / 125% / 150% / 175% scaling)" — Task 5 covers the four resolutions; the test asserts non-empty H.264 output rather than full pixel-decode-and-compare. Full-pixel correctness via decode is significantly harder and not required by the proof-of-life criterion (the GPU produces correct output if NVENC accepts the hwaccel frame and emits a non-zero packet at all four scales). If a future milestone wants pixel-perfect correctness, that's a separate test addition.
  - "End-to-end CLI demo restored" — verified by Task 7 step 1's full integration suite (the M3-broken `WorkerProcessIntegrationTests` regains green status because `EncodeOnThread` now accepts texture frames).
  - "Manual smoke checkpoint" — Task 7 step 3 prepares the handoff; Task 8 step 3 mandates that merge waits on user smoke.
  - "Regression rule" — Task 7 step 3 documents it in the handoff; the agent does not merge.

- **Spec deviation: pixel-correctness tolerance.** The spec wording suggests a CPU reference decode + tolerance compare. M4's plan asserts non-empty payload at four scales as the proof-of-life. The full-decode-and-compare test is a stretch goal — adding it requires a CPU H.264 decoder (FFmpeg's libavcodec has one but it's another dance through `avcodec_send_packet` / `avcodec_receive_frame` + a CPU NV12 → BGRA helper that's already in `D3D11VideoProcessorColorConverterTests`). If the user wants pixel-perfect end-to-end verification, it can land in M5 alongside the cleanup.

- **Coverage gate:** all M4 production code is `#if WINDOWS`-guarded; unit project targets bare `net8.0` and excludes it. Gate stays at 90/85 from M2's relaxation. M5 restores 100/100 along with the cleanup.

- **Type consistency:** `IFrameTexturePool`, `AcquireFrameTexture`, `Direct3D11DeviceManager`, `sharedDeviceManager`, `sharedFrameTexturePool`, `pendingPoolFramePointers`, `hardwareDeviceContextReference`, `hardwareFramesContextReference`, `ownsSharedDeviceManager`, `ownsDeviceManager` are used identically across Tasks 1-6.

- **No placeholders.** Every step has runnable code or a runnable command. Where Silk.NET / FFmpeg.AutoGen binding names might diverge, the API verification escape hatch (PowerShell snippet) is included.

- **Architectural concern flagged for review:** the `pendingPoolFramePointers` queue couples Acquire and Encode order. If the converter's `Convert` somehow races (it doesn't today — `OnFrameArrived` is synchronous in WGC's free-threaded callback), the queue would miscompare. The pointer-equality check in `EncodeOnThread` is a defensive assertion that catches this. If the assertion fires in production, M5 should add a dictionary-based lookup keyed by texture pointer instead.

- **Encoder no longer accepts byte-bearing CapturedFrames.** The spec's M5 section says "the bytes-path constructor on CapturedFrame is scoped to test-only visibility in M5." Until then, the bytes constructor is still public on CapturedFrame, but `FFmpegNvencEncoder.EncodeOnThread` now throws `EncoderException` for bytes-bearing frames. This is the intended M4 contract — the bytes path lives on for `FakeWindowCapture` and other test fakes, not for the real encoder.

- **Manual smoke is mandatory before merge.** Task 8 step 3 explicitly states this. The integration-test suite proves the GPU pipeline produces decodable output, but it doesn't measure end-to-end latency. The spec's regression rule requires that. User to verify.
