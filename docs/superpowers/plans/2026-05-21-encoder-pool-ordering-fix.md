# Encoder pool-ordering fix — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the `EncoderException("Pool / encode ordering is broken")` at `FFmpegNvencEncoder.cs:305` and the AVFrame-leak on the worker's pause-skip path, by replacing the FIFO `ConcurrentQueue<nint>` with a texture-keyed `ConcurrentDictionary` and adding a `ReleaseFrameTexture` API.

**Architecture:** Producer (`WgcCapture.OnFrameArrived`) calls `AcquireFrameTexture` which now inserts the `AVFrame*` into a `ConcurrentDictionary<(nint, int), nint>` keyed on `(textureP, subresourceIndex)`. Consumer (`WorkerCommandHandler.EncodeAsync` or `ReleaseFrameTexture`) looks up the matching `AVFrame*` by key and removes it. The encoder is now ordering-agnostic — the FIFO invariant is gone.

**Tech Stack:** C# 12 / .NET 8 (`WindowStream.Core`) + .NET 10 (MAUI server), FFmpeg.AutoGen 7.0.0 (`h264_nvenc` + D3D11VA hwaccel), Silk.NET 2.22.0 (D3D11 interop), xUnit + Coverlet for tests, integration tests gated by `[NvidiaDriverTheory]`/`[NvidiaDriverFact]`.

**Resolves:** [Gitea #6](https://gitea.llamabox.internal/schoen/WindowStream/issues/6)

---

## Phase 1: Interface + first failing test

### Task 1: Add `ReleaseFrameTexture` to `IFrameTexturePool` interface

**Files:**
- Modify: `src/WindowStream.Core/Encode/IFrameTexturePool.cs`
- Modify: `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs` (add stub)

- [ ] **Step 1: Add the interface method**

Modify `src/WindowStream.Core/Encode/IFrameTexturePool.cs` — append a `ReleaseFrameTexture` method after the existing `AcquireFrameTexture`:

```csharp
namespace WindowStream.Core.Encode;

/// <summary>
/// Source of NV12 D3D11 textures for the GPU-resident pipeline. The encoder
/// implements this against its FFmpeg <c>hw_frames_ctx</c> pool; the capture
/// path's converter writes into the textures the pool hands out, then the
/// encoder consumes the matching AVFrame on the next <c>EncodeAsync</c>.
///
/// Acquire is paired with EITHER <c>EncodeAsync</c> OR
/// <see cref="ReleaseFrameTexture"/> — every acquired texture must be
/// returned to the pool exactly once via one of those two calls.
/// The pool uses a <c>(texturePointer, textureSubresourceIndex)</c> keyed
/// lookup, so acquire-vs-consume order is not constrained.
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
    /// be reused after the matching <c>EncodeAsync</c> or
    /// <see cref="ReleaseFrameTexture"/> completes.
    /// </summary>
    void AcquireFrameTexture(out nint texturePointer, out int textureSubresourceIndex);

    /// <summary>
    /// Return a previously acquired pool texture without encoding it.
    /// Used when the caller acquires a frame but chooses not to encode it
    /// (e.g. the worker pause-skip path). The matching AVFrame is freed
    /// and its pool slot becomes available for reuse.
    /// </summary>
    /// <param name="texturePointer">
    /// The texture pointer returned by a prior <see cref="AcquireFrameTexture"/>.
    /// </param>
    /// <param name="textureSubresourceIndex">
    /// The subresource index returned by the same prior call.
    /// </param>
    void ReleaseFrameTexture(nint texturePointer, int textureSubresourceIndex);
}
```

- [ ] **Step 2: Add a NotImplementedException stub on `FFmpegNvencEncoder`**

Modify `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs`. Add this method right after `AcquireFrameTexture` (after line 263):

```csharp
public void ReleaseFrameTexture(nint texturePointer, int textureSubresourceIndex)
{
    throw new NotImplementedException(
        "ReleaseFrameTexture is not yet implemented; tracked in Gitea #6.");
}
```

- [ ] **Step 3: Build to verify no compile errors**

Run: `dotnet build`
Expected: BUILD SUCCEEDED, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/WindowStream.Core/Encode/IFrameTexturePool.cs src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs
git commit -m "feat(encode): add IFrameTexturePool.ReleaseFrameTexture stub (T1)"
```

### Task 2: Failing integration test — out-of-order encode

Demonstrates the FIFO bug. Run on current code: trips the `EncoderException("Pool / encode ordering is broken")` assert. After the dictionary refactor: passes.

**Files:**
- Create: `tests/WindowStream.Integration.Tests/Encode/FFmpegNvencEncoderPoolOrderingTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/WindowStream.Integration.Tests/Encode/FFmpegNvencEncoderPoolOrderingTests.cs`:

```csharp
#if WINDOWS
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Direct3D11;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Encode;
using WindowStream.Integration.Tests.Infrastructure;
using WindowStream.Integration.Tests.Support;
using Xunit;

namespace WindowStream.Integration.Tests.Encode;

public sealed class FFmpegNvencEncoderPoolOrderingTests
{
    private const int WidthPixels = 640;
    private const int HeightPixels = 360;

    /// <summary>
    /// Acquires two pool frames, then encodes them in the OPPOSITE order
    /// from acquisition. The pre-fix FIFO assertion at
    /// FFmpegNvencEncoder.cs:305 trips because TryDequeue returns A's
    /// AVFrame but the CapturedFrame's (texP, idx) belongs to B.
    /// Post-fix the dictionary lookup finds the correct AVFrame for each.
    /// </summary>
    [NvidiaDriverFact]
    [Trait("Category", "Integration")]
    public async Task OutOfOrderEncode_Succeeds()
    {
        using Direct3D11DeviceManager deviceManager = new Direct3D11DeviceManager();
        await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();
        encoder.Configure(
            new EncoderOptions(
                widthPixels: WidthPixels,
                heightPixels: HeightPixels,
                framesPerSecond: 30,
                bitrateBitsPerSecond: 4_000_000,
                groupOfPicturesLength: 30,
                safetyKeyframeIntervalSeconds: 2),
            deviceManager);

        nint patternTexturePointer = Nv12TextureFactory.CreateQuadrantPatternTexture(
            deviceManager, WidthPixels, HeightPixels);
        try
        {
            // Acquire two distinct pool textures.
            encoder.AcquireFrameTexture(out nint texturePointerA, out int subresourceIndexA);
            CopyPatternInto(deviceManager, patternTexturePointer, texturePointerA, subresourceIndexA);
            CapturedFrame frameA = CapturedFrame.FromTexture(
                widthPixels: WidthPixels,
                heightPixels: HeightPixels,
                rowStrideBytes: WidthPixels,
                pixelFormat: PixelFormat.Nv12,
                presentationTimestampMicroseconds: 0,
                nativeTexturePointer: texturePointerA,
                textureArrayIndex: subresourceIndexA);

            encoder.AcquireFrameTexture(out nint texturePointerB, out int subresourceIndexB);
            CopyPatternInto(deviceManager, patternTexturePointer, texturePointerB, subresourceIndexB);
            CapturedFrame frameB = CapturedFrame.FromTexture(
                widthPixels: WidthPixels,
                heightPixels: HeightPixels,
                rowStrideBytes: WidthPixels,
                pixelFormat: PixelFormat.Nv12,
                presentationTimestampMicroseconds: 33_333,
                nativeTexturePointer: texturePointerB,
                textureArrayIndex: subresourceIndexB);

            encoder.RequestKeyframe();

            // Encode B first, then A — opposite of acquisition order.
            await encoder.EncodeAsync(frameB, CancellationToken.None).ConfigureAwait(false);
            await encoder.EncodeAsync(frameA, CancellationToken.None).ConfigureAwait(false);
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

    private static void CopyPatternInto(
        Direct3D11DeviceManager deviceManager,
        nint patternTexturePointer,
        nint destinationTexturePointer,
        int destinationSubresourceIndex)
    {
        unsafe
        {
            ID3D11DeviceContext* context = (ID3D11DeviceContext*)deviceManager.NativeContextPointer;
            context->CopySubresourceRegion(
                (ID3D11Resource*)destinationTexturePointer,
                (uint)destinationSubresourceIndex,
                0u, 0u, 0u,
                (ID3D11Resource*)patternTexturePointer,
                0u,
                (Box*)null);
        }
    }
}

#endif
```

- [ ] **Step 2: Run test and verify it fails with the FIFO assertion**

Run:
```
dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~OutOfOrderEncode_Succeeds"
```

Expected: FAIL with `EncoderException: EncodeAsync received a CapturedFrame whose texture pointer + array index do not match the next queued pool frame. Pool / encode ordering is broken.` originating at `FFmpegNvencEncoder.cs:305`.

- [ ] **Step 3: Commit**

```bash
git add tests/WindowStream.Integration.Tests/Encode/FFmpegNvencEncoderPoolOrderingTests.cs
git commit -m "test(encode): failing out-of-order encode reproduction (T2, Gitea #6)"
```

---

## Phase 2: Replace queue with dictionary + implement Release

### Task 3: Replace `ConcurrentQueue` with `ConcurrentDictionary` in `FFmpegNvencEncoder`

**Files:**
- Modify: `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs`

- [ ] **Step 1: Swap the field declaration**

Modify `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs:34`:

Replace:
```csharp
    private readonly ConcurrentQueue<nint> pendingPoolFramePointers = new ConcurrentQueue<nint>();
```

With:
```csharp
    private readonly ConcurrentDictionary<(nint texturePointer, int subresourceIndex), nint> pendingPoolFramesByKey =
        new ConcurrentDictionary<(nint, int), nint>();
```

- [ ] **Step 2: Update `AcquireFrameTexture` to insert by key**

Modify `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs:262`. Replace the existing `Enqueue` line with a `TryAdd` + defensive throw:

```csharp
        texturePointer = (nint)frame->data[0];
        textureSubresourceIndex = (int)(long)frame->data[1];

        if (!pendingPoolFramesByKey.TryAdd((texturePointer, textureSubresourceIndex), (nint)frame))
        {
            ffmpeg.av_frame_free(&frame);
            throw new EncoderException(
                "Duplicate pool key: FFmpeg pool returned ("
                + "texP=0x" + texturePointer.ToString("X")
                + ", idx=" + textureSubresourceIndex
                + ") while a prior acquisition is still in flight. "
                + "Indicates FFmpeg pool corruption or a missing Release.");
        }
```

- [ ] **Step 3: Update `EncodeOnThread` to look up by key**

Modify `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs:294-308`. Replace the `TryDequeue` block + the equality assert with a single dictionary `TryRemove`:

```csharp
        (nint, int) lookupKey = (frame.nativeTexturePointer, frame.textureArrayIndex);
        if (!pendingPoolFramesByKey.TryRemove(lookupKey, out nint pendingFramePointer))
        {
            throw new EncoderException(
                "No pool AVFrame matches captured ("
                + "texP=0x" + frame.nativeTexturePointer.ToString("X")
                + ", idx=" + frame.textureArrayIndex
                + ") — caller violated the IFrameTexturePool contract "
                + "(EncodeAsync or ReleaseFrameTexture must follow each AcquireFrameTexture exactly once).");
        }

        AVFrame* poolFrame = (AVFrame*)pendingFramePointer;
```

Note: the old equality assert is gone — the dictionary lookup makes it structurally impossible to mismatch.

- [ ] **Step 4: Update `FreeNativeResources` to drain the dictionary**

Modify `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs:390-395`. Replace the `while (TryDequeue)` block:

```csharp
        // Drain any unconsumed pool frames first. ConcurrentDictionary's
        // enumerator is a moment-in-time snapshot so it's safe to iterate
        // here; Clear() afterward removes the dangling nint entries so a
        // hypothetical second FreeNativeResources call is a no-op for the
        // dict. (Dispose's `disposed` guard already prevents that, but keep
        // the Clear() defensive.)
        foreach (System.Collections.Generic.KeyValuePair<(nint, int), nint> entry in pendingPoolFramesByKey)
        {
            AVFrame* pendingFrame = (AVFrame*)entry.Value;
            ffmpeg.av_frame_free(&pendingFrame);
        }
        pendingPoolFramesByKey.Clear();
```

- [ ] **Step 5: Implement `ReleaseFrameTexture`**

Modify `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs`. Replace the stub `ReleaseFrameTexture` (added in T1) with a real implementation. Mark `[ExcludeFromCodeCoverage]` to match the surrounding native paths:

```csharp
[ExcludeFromCodeCoverage(Justification = "Native FFmpeg calls; exercised by Phase 12 integration tests.")]
public unsafe void ReleaseFrameTexture(nint texturePointer, int textureSubresourceIndex)
{
    if (options is null)
    {
        throw new InvalidOperationException("Configure must be called before ReleaseFrameTexture.");
    }
    (nint, int) lookupKey = (texturePointer, textureSubresourceIndex);
    if (!pendingPoolFramesByKey.TryRemove(lookupKey, out nint pendingFramePointer))
    {
        throw new EncoderException(
            "No pool AVFrame matches released ("
            + "texP=0x" + texturePointer.ToString("X")
            + ", idx=" + textureSubresourceIndex
            + ") — either Release was called without a matching Acquire, "
            + "or the texture was already consumed by EncodeAsync.");
    }
    AVFrame* poolFrame = (AVFrame*)pendingFramePointer;
    ffmpeg.av_frame_free(&poolFrame);
}
```

- [ ] **Step 6: Verify build**

Run: `dotnet build`
Expected: BUILD SUCCEEDED, 0 errors. `using System.Linq;` may be required at the top of the file if `.Values.ToArray()` doesn't already resolve — add it to the existing `using` block if so.

- [ ] **Step 7: Run T2 test and verify it passes**

Run:
```
dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~OutOfOrderEncode_Succeeds"
```

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs
git commit -m "fix(encode): replace FIFO queue with texture-keyed dictionary (T3, Gitea #6)"
```

### Task 4: Add release-path test

**Files:**
- Modify: `tests/WindowStream.Integration.Tests/Encode/FFmpegNvencEncoderPoolOrderingTests.cs`

- [ ] **Step 1: Add the test method**

Append this test to `FFmpegNvencEncoderPoolOrderingTests` after `OutOfOrderEncode_Succeeds`:

```csharp
/// <summary>
/// Acquires a pool frame, releases it without encoding (simulating the
/// worker pause-skip path), acquires another, and encodes successfully.
/// Verifies that ReleaseFrameTexture returns the AVFrame to the pool
/// cleanly and the subsequent EncodeAsync finds its own matching frame.
/// </summary>
[NvidiaDriverFact]
[Trait("Category", "Integration")]
public async Task AcquireReleaseAcquireEncode_Succeeds()
{
    using Direct3D11DeviceManager deviceManager = new Direct3D11DeviceManager();
    await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();
    encoder.Configure(
        new EncoderOptions(
            widthPixels: WidthPixels,
            heightPixels: HeightPixels,
            framesPerSecond: 30,
            bitrateBitsPerSecond: 4_000_000,
            groupOfPicturesLength: 30,
            safetyKeyframeIntervalSeconds: 2),
        deviceManager);

    nint patternTexturePointer = Nv12TextureFactory.CreateQuadrantPatternTexture(
        deviceManager, WidthPixels, HeightPixels);
    try
    {
        // Acquire frame A and immediately release it (simulating pause-skip).
        encoder.AcquireFrameTexture(out nint texturePointerA, out int subresourceIndexA);
        encoder.ReleaseFrameTexture(texturePointerA, subresourceIndexA);

        // Acquire frame B and encode normally.
        encoder.AcquireFrameTexture(out nint texturePointerB, out int subresourceIndexB);
        CopyPatternInto(deviceManager, patternTexturePointer, texturePointerB, subresourceIndexB);
        CapturedFrame frameB = CapturedFrame.FromTexture(
            widthPixels: WidthPixels,
            heightPixels: HeightPixels,
            rowStrideBytes: WidthPixels,
            pixelFormat: PixelFormat.Nv12,
            presentationTimestampMicroseconds: 0,
            nativeTexturePointer: texturePointerB,
            textureArrayIndex: subresourceIndexB);

        encoder.RequestKeyframe();
        await encoder.EncodeAsync(frameB, CancellationToken.None).ConfigureAwait(false);
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

- [ ] **Step 2: Run test, verify it passes**

Run:
```
dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~AcquireReleaseAcquireEncode_Succeeds"
```

Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/WindowStream.Integration.Tests/Encode/FFmpegNvencEncoderPoolOrderingTests.cs
git commit -m "test(encode): verify ReleaseFrameTexture returns slot to pool (T4)"
```

---

## Phase 3: Wire `ReleaseFrameTexture` into the worker pause path

### Task 5: Route worker pause-skip through `ReleaseFrameTexture`

**Files:**
- Modify: `src/WindowStream.Cli/Commands/WorkerCommandHandler.cs:91-97`

- [ ] **Step 1: Update the encode loop**

Modify `src/WindowStream.Cli/Commands/WorkerCommandHandler.cs:91-97`. Replace the existing `await foreach` body:

```csharp
                await foreach (CapturedFrame captured in capture.Frames.WithCancellation(lifecycle.Token).ConfigureAwait(false))
                {
                    bool currentlyPaused;
                    lock (pauseLock) currentlyPaused = paused;
                    if (currentlyPaused)
                    {
                        // Return the acquired pool frame so the encoder doesn't leak
                        // its AVFrame across the pause window. Without this the
                        // pool slot stays held until the worker exits and a
                        // subsequent resume's EncodeAsync would fail to find a
                        // matching dictionary entry (Gitea #6).
                        encoder.ReleaseFrameTexture(captured.nativeTexturePointer, captured.textureArrayIndex);
                        continue;
                    }
                    await encoder.EncodeAsync(captured, lifecycle.Token).ConfigureAwait(false);
                }
```

- [ ] **Step 2: Verify build**

Run: `dotnet build`
Expected: BUILD SUCCEEDED.

- [ ] **Step 3: Run the full Core + Integration suite to verify no regression**

Run:
```
dotnet test
```

Expected: all existing tests pass, plus the two new `FFmpegNvencEncoderPoolOrderingTests`. 100% line + branch coverage gate satisfied.

- [ ] **Step 4: Commit**

```bash
git add src/WindowStream.Cli/Commands/WorkerCommandHandler.cs
git commit -m "fix(worker): release acquired pool frame on pause-skip (T5, Gitea #6)"
```

---

## Phase 4: Concurrent regression test + end-to-end verification

### Task 6: Three concurrent encoders survive 30 seconds

This is the regression test for the original multi-worker contention symptom in the Gitea issue. The bug is per-encoder, so running three concurrent in-process encoders exercises the same GPU-contention conditions without needing three separate worker processes.

**Files:**
- Modify: `tests/WindowStream.Integration.Tests/Encode/FFmpegNvencEncoderPoolOrderingTests.cs`

- [ ] **Step 1: Add the regression test**

Append to the same test class:

```csharp
/// <summary>
/// Regression test for Gitea #6. Spawns three concurrent FFmpegNvencEncoder
/// instances pumping synthetic captured frames in parallel on the same GPU,
/// each encoding for ~30s at 30fps (~900 frames per encoder). Pre-fix this
/// would surface the FIFO assertion within ~10s on multi-worker contention.
/// Post-fix all three must survive without EncoderException.
/// </summary>
[NvidiaDriverFact]
[Trait("Category", "Integration")]
[Trait("Category", "LongRunning")]
public async Task ThreeConcurrentEncoders_SurviveThirtySeconds()
{
    const int DurationSeconds = 30;
    const int FramesPerSecond = 30;
    const int TotalFrames = DurationSeconds * FramesPerSecond;

    using CancellationTokenSource overallTimeout =
        new CancellationTokenSource(System.TimeSpan.FromSeconds(DurationSeconds + 10));

    Task[] encoderTasks = new Task[3];
    for (int encoderIndex = 0; encoderIndex < 3; encoderIndex++)
    {
        int capturedEncoderIndex = encoderIndex;
        encoderTasks[encoderIndex] = Task.Run(
            () => RunEncoderForFrames(TotalFrames, FramesPerSecond, capturedEncoderIndex, overallTimeout.Token),
            overallTimeout.Token);
    }

    await Task.WhenAll(encoderTasks).ConfigureAwait(false);
}

private static async Task RunEncoderForFrames(
    int totalFrames,
    int framesPerSecond,
    int encoderIndex,
    CancellationToken cancellationToken)
{
    using Direct3D11DeviceManager deviceManager = new Direct3D11DeviceManager();
    await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();
    encoder.Configure(
        new EncoderOptions(
            widthPixels: WidthPixels,
            heightPixels: HeightPixels,
            framesPerSecond: framesPerSecond,
            bitrateBitsPerSecond: 4_000_000,
            groupOfPicturesLength: 30,
            safetyKeyframeIntervalSeconds: 2),
        deviceManager);

    nint patternTexturePointer = Nv12TextureFactory.CreateQuadrantPatternTexture(
        deviceManager, WidthPixels, HeightPixels);
    try
    {
        long frameDurationMicroseconds = 1_000_000L / framesPerSecond;
        encoder.RequestKeyframe();
        for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            encoder.AcquireFrameTexture(out nint poolTexturePointer, out int poolSubresourceIndex);
            CopyPatternInto(deviceManager, patternTexturePointer, poolTexturePointer, poolSubresourceIndex);
            CapturedFrame textureFrame = CapturedFrame.FromTexture(
                widthPixels: WidthPixels,
                heightPixels: HeightPixels,
                rowStrideBytes: WidthPixels,
                pixelFormat: PixelFormat.Nv12,
                presentationTimestampMicroseconds: frameIndex * frameDurationMicroseconds,
                nativeTexturePointer: poolTexturePointer,
                textureArrayIndex: poolSubresourceIndex);
            await encoder.EncodeAsync(textureFrame, cancellationToken).ConfigureAwait(false);
        }
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

- [ ] **Step 2: Run the regression test**

Run:
```
dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~ThreeConcurrentEncoders_SurviveThirtySeconds"
```

Expected: PASS within ~30-40 seconds.

If it fails with an `EncoderException`, the dictionary fix is incomplete — re-investigate which code path is still tripping the contract. Do not paper over the failure.

- [ ] **Step 3: Commit**

```bash
git add tests/WindowStream.Integration.Tests/Encode/FFmpegNvencEncoderPoolOrderingTests.cs
git commit -m "test(encode): three concurrent encoders regression (T6, Gitea #6)"
```

### Task 7: End-to-end verification on phone

Manual smoke test using the phone viewer the user has connected.

- [ ] **Step 1: Confirm phone is reachable**

Run: `adb devices`
Expected: at least one device listed as `device` (not `unauthorized` / `offline`).

- [ ] **Step 2: Build viewer + install portable flavor**

Run:
```
./gradlew :app:assemblePortableDebug
adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/portable/debug/app-portable-debug.apk
```

Expected: APK installed.

- [ ] **Step 3: Start CLI server on host**

Run:
```
dotnet run --project src/WindowStream.Cli -f net8.0-windows10.0.19041.0 -- serve
```

Leave running in a background terminal. Note the IP + TCP port from banner.

- [ ] **Step 4: List capturable windows**

In a second terminal:
```
dotnet run --project src/WindowStream.Cli -f net8.0-windows10.0.19041.0 -- list
```

Pick three HWNDs with active content (Terminal, Fork, Unity, etc.) and even width/height — avoid Edge kiosk and Firefox (separate known issues).

- [ ] **Step 5: Launch viewer with three streams via adb intent**

Run (substitute HWNDs and IP/port):
```
adb shell am start -n com.mtschoen.windowstream.viewer/.demo.DemoActivity \
    --es streamHost <pc-lan-ip> --ei streamPort <tcpPort> \
    --ela selectedWindowHwnds <hwnd1>,<hwnd2>,<hwnd3>
```

Expected: viewer opens, three streams begin rendering.

- [ ] **Step 6: Run for ~60 seconds, check for crashes**

Watch the server stdout in the first terminal. Expected: NO `EncoderException` lines. NO `[worker] capture failed` lines.

Watch viewer overlay (or pull viewer JSONL after):
```
adb pull /storage/emulated/0/Android/data/com.mtschoen.windowstream.viewer/files/logs/
```

Expected: zero `StreamStopped reason=CaptureFailed` events.

- [ ] **Step 7: Force-stop viewer and shut down server**

```
adb shell am force-stop com.mtschoen.windowstream.viewer
```

Then Ctrl+C the server.

- [ ] **Step 8: Verify with the latency-clock perf test (no regression)**

The risk section of the spec called out perf regression as a concern. Run the canonical cold-start latency clock to confirm the dictionary ops don't shift the p50/p95.

Run: `tools\latency-test`

Expected: HMD-camera p50 and FRAMECOUNT cap→present p50 within noise floor of the 2026-05-11 baseline (E2E p50 ~28 ms, p95 ~40 ms, reasm→dec p50 ~9 ms). A regression >5 ms p50 is a fail.

This step is manual + on-headset; coordinate with the user.

- [ ] **Step 9: If all green, write up the result**

Update Gitea issue #6 with the verification result (commit hashes, test pass list, observability summary), then close it via the `gitea` MCP or the web UI.

---

## Tasks not in this plan (intentionally)

- **CLI-side observability (Gitea #7)** is unrelated to this fix and was deferred.
- **Edge kiosk WGC bust** and **GXR Wi-Fi sustained-4K lockup** memories should be re-tested after this lands — the encoder-pool-ordering memory predicts they may retire as symptoms — but that retesting is separate work, not part of this plan.
