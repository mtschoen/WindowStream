# GPU-resident frame pipeline (server)

**Date:** 2026-05-03
**Status:** Draft (awaiting user review)

## Background

After the pipeline-depth fixes shipped in the 2026-04-25 frame-counter work
(`tune=ull`, `surfaces=1`, `KEY_LOW_LATENCY`, WifiLock), end-to-end latency
on the Unity 4K → Galaxy XR demo has dropped to roughly the 100 ms range. We
have hit the point of diminishing returns on shaving time off individual
stages: the remaining gains come from removing **work** from the pipeline, not
making the existing work faster.

The single largest source of avoidable work today is a per-frame
**GPU → CPU → GPU round-trip** on the server side:

1. Windows Graphics Capture delivers the source window as a D3D11 BGRA texture
   already on the GPU (`WgcCapture.OnFrameArrived`,
   `src/WindowStream.Core/Capture/Windows/WgcCapture.cs:64`).
2. `WgcFrameConverter.Convert`
   (`src/WindowStream.Core/Capture/Windows/WgcFrameConverter.cs:25`) creates a
   CPU staging texture, `CopyResource`'s the GPU texture into it, maps it,
   and `Marshal.Copy`'s the result into a managed `byte[]`. **First readback —
   the frame leaves the GPU here.** At 4K BGRA this is ~33 MB per frame.
3. `FFmpegNvencEncoder.EncodeOnThread`
   (`src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs:167`) hands the bytes
   to `sws_scale`, which performs BGRA → NV12 colour conversion **in software
   on the CPU**. At 4K this is measurable single-digit milliseconds per frame
   on a modern CPU and worse under load.
4. `avcodec_send_frame` then hands the NV12 AVFrame to NVENC, which
   **internally uploads it back to the GPU** for the encoder silicon to chew
   on. Round-trip complete.
5. `avcodec_receive_packet` returns the encoded H.264 packet, which is
   `Marshal.Copy`'d into a managed `byte[]` for transport
   (`FFmpegNvencEncoder.cs:228`). This last readback is **unavoidable** —
   encoded bytes have to hit a socket.

At 4K60 the avoidable bus traffic is ~33 MB/frame downloaded plus ~12 MB
NV12 uploaded, ~2.7 GB/s of PCIe round-trip burned. Worse than the bandwidth
itself, this is a **serialisation point**: the encoder cannot start until the
CPU readback and `sws_scale` finish, so the WGC thread, the encoder thread,
and the GPU all wait on each other.

The viewer side is **already the design we want.** `MediaCodecDecoder`
(`viewer/.../decoder/MediaCodecDecoder.kt:86`) configures MediaCodec with a
`Surface` and uses `releaseOutputBuffer(idx, render=true)` (line 63). The
hardware decoder writes directly into a GPU-backed Surface and SurfaceFlinger
composites without a CPU readback. The only CPU → GPU touch is copying
encoded bitstream bytes into the codec input ByteBuffer, which is the
unavoidable socket → codec hop.

## Goal

Remove the avoidable GPU → CPU → GPU round-trip on the server. The WGC D3D11
texture stays on the GPU through colour conversion (D3D11 fixed-function
`VideoProcessorBlt`) and into NVENC (FFmpeg D3D11VA hwaccel ingestion). The
only memory transition that survives is the encoded H.264 bitstream copied
out of NVENC into a managed byte buffer for the UDP socket.

Targets, measured at the [FRAMECOUNT] join points:

- Eliminate the 33 MB/frame staging-readback at 4K and the corresponding
  ~12 MB NV12 upload that NVENC does internally today.
- Eliminate the per-frame `sws_scale` CPU pass.
- Reduce median cap → enc latency by an estimated 5–15 ms at 4K, with
  larger reductions on the tail.
- Free CPU cycles previously spent in `sws_scale` and the staging copy for
  the rest of the server pipeline.

## Non-goals

- AMD AMF (Radeon) encoder support. The architecture is chosen so a future
  AMF path slots in cleanly (AMF also takes D3D11 textures), but
  AMF integration is not in this work.
- CUDA-based filters (e.g., on-GPU scale or crop). Future work; we will
  add CUDA dependency when a feature actually needs it.
- Changes to the wire protocol or any viewer code.
- A software / CPU encoder fallback path.
- Bumping target framerate, changing rate-control mode, or any other tuning
  not directly required by the pipeline change.

## Architecture

### Target pipeline

```
[ WGC ]                  -> D3D11 BGRA texture (GPU)
                                |
                                v   VideoProcessorBlt  (fixed-function GPU video engine)
[ D3D11 video processor ] -> D3D11 NV12 texture from hw_frames_ctx pool (GPU)
                                |
                                v   FFmpeg D3D11VA hwaccel
[ NVENC ]                -> AVPacket H.264 bytes  (CPU — single, unavoidable readback)
                                |
                                v
[ NAL fragmenter ]       -> UDP socket
```

A single `ID3D11Device` (created with `D3D11_CREATE_DEVICE_VIDEO_SUPPORT`) is
shared between WGC, the video processor, and FFmpeg's hwaccel layer. The
device is owned by a new `Direct3D11DeviceManager`. WGC consumes it via the
existing `IDirect3DDevice` WinRT wrapper; the video processor and FFmpeg
consume the raw `ID3D11Device*`.

### New components

- **`Direct3D11DeviceManager`** (`src/WindowStream.Core/Capture/Windows/`) —
  owns the shared `ID3D11Device` and `ID3D11DeviceContext`. Exposes both the
  raw COM pointers (for FFmpeg `AVHWDeviceContext`) and the WinRT
  `IDirect3DDevice` (for WGC). Created once per `SessionHost`. Disposes the
  underlying device on dispose.

- **`D3D11VideoProcessorColorConverter`**
  (`src/WindowStream.Core/Capture/Windows/`) — wraps `ID3D11VideoDevice`,
  `ID3D11VideoContext`, `ID3D11VideoProcessor`,
  `ID3D11VideoProcessorEnumerator`. Exposes a single method:

  ```
  void Convert(ID3D11Texture2D* sourceBgra, ID3D11Texture2D* destinationNv12, int destinationArrayIndex);
  ```

  Internally creates per-call `ID3D11VideoProcessorInputView` and
  `ID3D11VideoProcessorOutputView` against the supplied textures and calls
  `VideoProcessorBlt`. The processor itself, the enumerator, and the
  context are reused across frames.

### Changed components

- **`CapturedFrame`** (`src/WindowStream.Core/Capture/CapturedFrame.cs`) —
  during the transition gains an optional native-texture representation
  alongside the existing managed-byte buffer:

  ```
  CapturedFrame.FromBytes(...)        // existing path; test fakes; transitional only
  CapturedFrame.FromTexture(nint texturePointer, int arrayIndex, ...)  // new path
  ```

  The bytes path is removed in M5 (or scoped to a test-only constructor).

- **`WgcCapture`** (`src/WindowStream.Core/Capture/Windows/WgcCapture.cs`) —
  takes a `Direct3D11DeviceManager` rather than creating its own
  `IDirect3DDevice`. No behavioural change otherwise.

- **`WgcFrameConverter`**
  (`src/WindowStream.Core/Capture/Windows/WgcFrameConverter.cs`) — replaces
  the staging-texture readback (`CopyResource` + `Map` + `Marshal.Copy`) with:
  acquire an NV12 D3D11 texture from the encoder's `hw_frames_ctx` pool,
  call `D3D11VideoProcessorColorConverter.Convert`, return a `CapturedFrame`
  referencing the NV12 texture. The output texture is held by the encoder's
  pool and released after the encoder is finished with it.

- **`FFmpegNvencEncoder`**
  (`src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs`) — gains
  `AVHWDeviceContext` of type `AV_HWDEVICE_TYPE_D3D11VA` and an
  `AVHWFramesContext` with `format = AV_PIX_FMT_D3D11`,
  `sw_format = AV_PIX_FMT_NV12`. `EncodeOnThread` builds an AVFrame whose
  `data[0]` is the texture pointer and `data[1]` is the array index, instead
  of running `sws_scale`. The `softwareScaleContextPointer` field, the
  `sws_getContext` call in `OpenCodecAndAssignOptions`, and the entire
  `sws_scale` block in `EncodeOnThread` are removed in M4.

### Test fakes

`FakeWindowCapture` continues producing managed-byte frames — that path
exists so unit tests can synthesise frames without a D3D11 device.
`FakeVideoEncoder` accepts both representations. Production code only
exercises the texture path after M4. The bytes-path constructor on
`CapturedFrame` is scoped to test-only visibility in M5 (likely via
`InternalsVisibleTo` and a `static internal` factory).

### Frame lifetime / resource ownership

This describes the post-M4 steady state. M3 substitutes a hand-rolled NV12
ring for the FFmpeg pool — see milestone M3 below.

The lifecycle subtlety is the WGC frame surface. `Direct3D11CaptureFrame` is
`IDisposable` and the underlying texture is reused by the framepool. The
existing converter handles this correctly because the readback is
synchronous in `OnFrameArrived`. The new converter must preserve that
property: `VideoProcessorBlt` is called synchronously inside `OnFrameArrived`
so the WGC source texture is fully consumed before the WGC frame is
disposed. The destination NV12 texture lives in the encoder's hw_frames_ctx
pool and is reference-counted by FFmpeg via `AVFrame` ownership.

## Migration milestones

Each milestone is a single PR / commit-set that compiles, passes
`dotnet test`, and leaves the system functional end-to-end. Manual smoke
testing on real Unity → GXR hardware happens at M3, M4, M5.

### M1 — Shared D3D11 device

- Add `Direct3D11DeviceManager` with unit tests covering construction,
  device-creation flag set, dual-surface exposure (raw + WinRT), disposal.
- Wire `WgcCapture` to consume it instead of creating its own
  `IDirect3DDevice`. Update the `SessionHost` composition root.
- No behavioural change. The system continues to use the staging-readback +
  sws_scale CPU path.
- Expected diff size: small. ~150 LOC new code, ~20 LOC of changes in
  capture and session wiring.

### M2 — `CapturedFrame` discriminated representation

- Add native-texture path to `CapturedFrame` alongside bytes. Add unit
  tests for both constructors and the discriminator.
- Update `FakeVideoEncoder` to accept both representations (for use in
  later milestones).
- Nothing produces the texture path yet; everything still flows through
  bytes. Purely additive plumbing.
- Expected diff size: small. ~80 LOC.

### M3 — GPU colour converter + texture-producing capture path

- Add `D3D11VideoProcessorColorConverter` with unit tests for setup and
  cleanup, integration test for BGRA → NV12 round-trip correctness
  (decode the NV12 result with a CPU helper, compare to source within a
  tolerance for the colour-space conversion).
- Modify `WgcFrameConverter` to produce NV12 texture frames. Allocate a
  small ring of NV12 textures inside `WgcCapture` for this milestone (the
  hw_frames_ctx pool from M4 will replace this).
- Add a temporary bridge in `FFmpegNvencEncoder`: when given a texture
  frame, read it back to bytes via a staging texture and feed the existing
  sws_scale → NVENC path. **This milestone produces the texture but does
  not yet consume it via hwaccel** — so the system stays functional and
  the change is bounded to the capture side.
- Add `[FRAMECOUNT] stage=convert` log site at the end of
  `D3D11VideoProcessorColorConverter.Convert`. ptsUs is threaded through.
- **Manual smoke checkpoint.** Capture before/after [FRAMECOUNT] data and
  record in this design doc.
- Expected diff size: medium. ~400 LOC including tests.

### M4 — NVENC hwaccel ingestion

- Configure `AVHWDeviceContext` (D3D11VA) and `AVHWFramesContext`
  (sw_format=NV12) in `FFmpegNvencEncoder.OpenCodecAndAssignOptions`.
  Replace the M3 hand-rolled NV12 ring with the FFmpeg-managed
  hw_frames_ctx pool; update `WgcFrameConverter` to acquire textures from
  that pool.
- Replace `EncodeOnThread`'s `sws_scale` block with construction of a
  D3D11 AVFrame referencing the texture. Remove the M3 readback bridge.
  Remove `softwareScaleContextPointer` and the `sws_getContext` call.
- Integration test for end-to-end hwaccel encode → CPU reference decode of
  the resulting H.264 stream, verifying correctness at multiple resolutions
  (the existing DPI matrix: 100% / 125% / 150% / 175% scaling).
- **Manual smoke checkpoint — latency win should appear here.** Capture
  [FRAMECOUNT] data and record in this design doc.
- **Regression rule:** if this milestone shows latency *worse* than the
  M1 baseline, stop and diagnose before proceeding to M5.
- Expected diff size: medium-large. ~500 LOC including tests, with the
  bulk being the FFmpeg hwaccel boilerplate in `OpenCodecAndAssignOptions`.

### M5 — Cleanup and coverage restoration

- Scope the bytes constructor on `CapturedFrame` to test-only visibility:
  mark it `internal` and rely on the existing `InternalsVisibleTo`
  declaration that exposes internals to `WindowStream.Core.Tests`.
  Production code (capture, encode, hosting) only constructs texture
  frames after M4, so this is a visibility tightening rather than a
  deletion. The bytes representation field on `CapturedFrame` itself
  stays so test fakes can keep using it.
- Remove the discriminated-union dispatch from production code paths
  (anywhere that branches on representation type collapses to the
  texture-only case).
- Restore coverage thresholds in `Directory.Build.props` to 100% line /
  100% branch. Add or adjust tests to satisfy the gate.
- Update this design doc with the final measured before/after [FRAMECOUNT]
  numbers across all of M3, M4, M5.
- Update `CLAUDE.md` to describe the new pipeline shape and the
  `Direct3D11DeviceManager` composition root.
- Final manual smoke confirming no regression vs M4.
- Expected diff size: small to medium. Mostly deletions plus tests. ~200
  LOC net.

## Coverage gate strategy during transition

The gate stays **on** at all times — never disabled. Thresholds in
`Directory.Build.props` are temporarily relaxed during M2–M4:

- `WindowStream.Core` line coverage: 100% → 90% (M2 entry), restored in M5
- `WindowStream.Core` branch coverage: 100% → 85% (M2 entry), restored in M5

Each PR that lowers a threshold includes a `<!-- TEMPORARY: M5 restores -->`
comment next to the threshold line in the build props. M5 is in scope from
day one — not optional. The M5 PR description includes a checklist of the
specific files that dropped coverage during the transition, derived from
the `cobertura.xml` outputs across milestones.

The new orchestration code (device manager, converter wrappers,
discriminated-union dispatch, frame lifecycle) gets real unit tests
following TDD discipline. The new native COM-heavy code (VideoProcessorBlt
setup, FFmpeg hwaccel configuration) follows the existing
`[ExcludeFromCodeCoverage(Justification = "Native ...")]` pattern with
integration test coverage — same model as `FFmpegNvencEncoder` today.

## Validation plan

### Per-commit
- `dotnet test` on Windows with NVENC available. Unit tests + integration
  tests including the existing DPI matrix and the new round-trip
  correctness tests added in M3 / M4.

### Manual smoke at M3, M4, M5
- Setup: Unity 4K window on PC, Galaxy XR HMD on-head with
  DemoActivity launched via adb intent (per `CLAUDE.md`).
- Procedure: ≥60 seconds of active Unity content with measurable motion.
  `adb logcat -s FRAMECOUNT:V` on the viewer; server stderr captured to
  file. Compute median + p99 cap → enc, cap → present, and the new
  cap → convert intervals from joined PTS.
- Record numbers in this design doc under a **Measured results** section
  appended at each manual-smoke checkpoint. Side-by-side comparison vs M1
  baseline.

### Regression catch
If M4 manual smoke shows median or p99 latency *worse* than the M1
baseline, **stop**. Diagnose before proceeding to M5. The most likely
causes in priority order: (1) hw_frames_ctx pool starvation forcing
synchronous waits, (2) device-context locking serialising the WGC and
NVENC threads, (3) `VideoProcessorBlt` incurring a wait for previous-frame
completion.

## Risks

### D3D11 device sharing
The same `ID3D11Device` must satisfy three consumers: WGC (via the WinRT
`IDirect3DDevice` wrapper), the D3D11 video processor (requires
`D3D11_CREATE_DEVICE_VIDEO_SUPPORT`), and FFmpeg's `AVHWDeviceContext` of
type D3D11VA. Feature levels must align. **Mitigation:** the M1 unit tests
explicitly exercise device-creation flags and round-trip the device
through both consumer surfaces. An integration test in M3 confirms the
video processor accepts the device.

### FFmpeg hwaccel error messages
`hw_frames_ctx` misconfiguration surfaces as `AVERROR(EINVAL)` with no
contextual message. Symptoms typically appear at first `avcodec_send_frame`
rather than at `avcodec_open2`, making the failure mode "encoder opened
fine, then died on frame 1". **Mitigation:** M4 starts by getting a
known-good FFmpeg sample working as a side experiment in
`tools/hwaccel-spike/` before any production code changes. The user has
authorised pulling FFmpeg source into the home folder for reference; the
canonical sample is `doc/examples/hw_decode.c` and the NVENC-specific
patterns are in `libavcodec/nvenc.c`.

### WGC frame surface lifetime
`Direct3D11CaptureFrame` reuses textures within the framepool. The new
converter must complete `VideoProcessorBlt` before returning from
`OnFrameArrived`. This is already the shape today (`WgcFrameConverter.Convert`
is synchronous in the callback), so the constraint is preserved by
construction, but it is worth a comment in the new converter.

### "Restore coverage later" drift
Risk that the M5 restoration becomes permanently postponed. **Mitigation:**
M5 is mandatory and listed in this spec from day one with a concrete
checklist. The threshold lines in `Directory.Build.props` carry inline
markers (`<!-- TEMPORARY: M5 restores -->`) that any future contributor or
LLM will see immediately.

### NVENC version compatibility
FFmpeg's D3D11VA hwaccel for h264_nvenc has been stable since FFmpeg 5.x;
WindowStream pulls FFmpeg natives from OBS Studio's `bin/64bit/`, which
ships FFmpeg 6.x. **Mitigation:** verify the OBS-provided FFmpeg build
exposes `AV_PIX_FMT_D3D11` and `AV_HWDEVICE_TYPE_D3D11VA` symbols at M1
entry; document the minimum OBS version in `CLAUDE.md` as part of M5.

## Out-of-scope follow-ups noted for later

- **CUDA filter chain.** When on-GPU scaling becomes a need (e.g., a
  resolution-adaptive ladder), the next addition is a `scale_cuda` filter
  inserted between the converter and NVENC. This requires adding a CUDA
  hwaccel device alongside the D3D11VA one and using `hwmap`/`hwupload`
  to bridge them.
- **AMD AMF encoder.** The `Direct3D11DeviceManager` is intentionally
  encoder-agnostic. An `AmfVideoEncoder` would consume the same
  manager-owned device and accept the same texture-bearing `CapturedFrame`,
  with AMF's own analogue of `hw_frames_ctx` for its surface pool.
- **Cross-process texture sharing.** The v2 coordinator/worker split (per
  `project_v2_architecture_invariants` memory) currently exchanges encoded
  bitstream chunks between processes. If we ever want to share captured
  textures across processes, that requires `KeyedMutex` + shared handles —
  significantly more work, not in scope here.
