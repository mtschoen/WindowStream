# Frame-counter instrumentation + pipeline-depth lag fix

**Date:** 2026-04-25
**Status:** Draft (awaiting user review)

## Background

Steady-state typing lag has been observed end-to-end: when typing into a window
on the source PC, the viewer renders the character roughly 4-5 frames after
it appeared on the source monitor. The lag is **constant**, not growing —
e.g. PC shows `hello`, viewer simultaneously shows `he`. At 30 FPS this is
~133 ms.

A constant N-frame offset is structurally a **pipeline-depth** problem rather
than a network or sampling-rate problem. Two prime suspects fit the symptom:

1. **NVENC's internal surface queue.** `h264_nvenc` keeps multiple input
   surfaces in flight before emitting the first packet, even with
   `zerolatency=1`. The encoder code in `FFmpegNvencEncoder.EncodeOnThread`
   correctly drains with `avcodec_receive_packet` until `EAGAIN`, but if
   NVENC's internal pipeline depth is N surfaces, the steady-state behaviour
   is "send frame K, receive packet K-(N-1)" — a permanent N-1 frame lag.
   Configure does not currently set the FFmpeg knobs `delay=0` or `surfaces=1`.
2. **Android `MediaCodec` reorder buffer.** Without
   `MediaFormat.KEY_LOW_LATENCY=1` (Android 11 / API 30+), HW decoders may
   hold 1-3 frames before emitting. `MediaCodecDecoder.start` does not
   currently set that key.

Combined these can plausibly produce a constant ~4-5 frame end-to-end lag.

## Goal

1. Add lightweight, generally useful instrumentation that lets us **measure**
   pipeline-depth lag at any time, not just for this bug.
2. Use that instrumentation to confirm the bug as a number (not a feeling).
3. Apply the two targeted fixes above.
4. Use the instrumentation to verify the gap closed, and to attribute the
   remaining lag (if any) to encoder vs network vs decoder stages.

## Non-goals

- General latency reduction beyond the constant pipeline-depth lag (no
  GPU-resident pipeline rebuild, no bump to 60 FPS, no protocol change, no
  bitrate / rate-control tuning).
- A full per-stage trace. Phase 1 instruments four points, not eight.
- Permanent on-by-default frame-by-frame logging. Initial implementation is
  always-on for verification convenience; throttling / env-gating is a
  follow-up if the volume becomes a problem.

## Phase 1 — Frame-counter instrumentation (approach B)

Two log sites per side, four total. **PTS in microseconds is the join key**
across server and viewer — it's already carried in the encoded chunk and
re-emerges from `MediaCodec` as `BufferInfo.presentationTimeUs`, no protocol
change required.

### Shared log format

Both sides emit a single line per frame per stage with the same schema:

```
[FRAMECOUNT] stage=<enc|frag|reasm|dec> ptsUs=<P> wallMs=<T>
```

- `stage` — one of four named stages (see below).
- `ptsUs` — frame presentation timestamp in microseconds. Same value on both
  sides for the same frame.
- `wallMs` — local wall-clock time in milliseconds when the line is emitted.
  Server and viewer clocks are not synchronised; only **deltas within one
  side** are directly comparable. End-to-end gap is observed as a **frame-gap
  at the same external wall-instant**, which is exactly the typing-lag
  behaviour we're trying to measure.

Frame index can be derived as `ptsUs / (1_000_000 / framesPerSecond)` — at
30 FPS, frame N has PTS `N * 33333`.

### Server-side log sites (.NET, stderr)

- **`stage=enc`** — in `FFmpegNvencEncoder.EncodeOnThread`, immediately after
  `avcodec_receive_packet` succeeds and before `chunkChannel.Writer.TryWrite`
  (file `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs`).
- **`stage=frag`** — in `SessionHost.RunEncodePumpAsync`, just before the
  `foreach (FragmentedPacket packet in fragmenter.Fragment(...))` loop, once
  per chunk (file `src/WindowStream.Core/Session/SessionHost.cs`).

Output via `Console.Error.WriteLine` (the same stderr channel
`SessionHost.cs` already uses for its existing per-frame diagnostics — the
encoder file does not currently log, so this is its first diagnostic).
Wall-clock derived from `Stopwatch.GetTimestamp()` and `Stopwatch.Frequency`
on both server sites; using a monotonic clock keeps `enc → frag` deltas
meaningful.

### Viewer-side log sites (Kotlin, logcat)

- **`stage=reasm`** — at the top of the `frameFlow.collect { encodedFrame -> ... }`
  block in `MediaCodecDecoder.start` (file
  `viewer/WindowStreamViewer/app/src/main/kotlin/.../decoder/MediaCodecDecoder.kt`).
  This fires once per fully reassembled `EncodedFrame`, which is the right
  moment to snapshot "wire-arrival + reassembly complete" wall-time.
- **`stage=dec`** — in the `onOutputBufferAvailable` callback, alongside the
  existing `frameSink.onFrameRendered(...)` call. PTS comes from
  `bufferInformation.presentationTimeUs`.

Output via `Log.d("FRAMECOUNT", ...)` so the existing logcat filter pattern
in `CLAUDE.md` extends naturally with `FRAMECOUNT:V`.

### Logging throttle

None initially. The existing CLAUDE.md diagnostics already log at every-frame
cadence in spots (e.g. `SessionHost` capture-pump line). If 30 lines/second
per side per stage becomes painful in practice, follow-up work can add an env
gate (`WINDOWSTREAM_FRAMECOUNT=0/1`) and a conditional check at each site.
Out of scope for v1 of this work.

### CLAUDE.md update

Append `FRAMECOUNT:V` to the debug logcat command snippet in the **Debugging
tips** section so the new tag is visible by default when following the
documented filter.

### Phase 1 acceptance

Run an end-to-end session and observe in two terminals (server stderr +
`adb logcat`):

- All four stages emit one line per frame.
- Same `ptsUs` appears at all four stages for any given frame.
- At any wall-clock instant, the *most recent* `ptsUs` at `stage=dec` lags
  the *most recent* `ptsUs` at `stage=enc` by approximately 4-5 frame
  intervals (i.e. ~133-167 ms at 30 FPS, observed as a difference of
  ~133000-167000 µs in the PTS values).

If the observed gap is dramatically different from the felt symptom, the
counter is wrong and Phase 2 is blocked until the instrumentation is fixed.

## Phase 2 — Targeted pipeline-depth fixes

Gated on Phase 1 acceptance. Two minimal changes:

1. **NVENC**, in `FFmpegNvencEncoder.OpenCodecAndAssignOptions`:
   Drive NVENC's internal pipeline depth to its minimum. The primary knob
   is `surfaces=1` (set via `ffmpeg.av_opt_set(context->priv_data, ...)`,
   matching the surrounding pattern for `preset` / `tune` / `zerolatency` /
   `rc`); this caps the number of pre-allocated input surfaces. The
   implementation phase will additionally verify, against the FFmpeg.AutoGen
   bindings in use, whether `delay` is exposed as an NVENC private option,
   as a public `AVCodecContext` field, or both, and apply whichever is
   correct. Existing `preset=p1` / `tune=ll` / `zerolatency=1` / `rc=cbr`
   settings remain unchanged.

2. **MediaCodec**, in `MediaCodecDecoder.start`:
   - Before `newCodec.configure(...)`, set
     `mediaFormat.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)`.
   - This key was added in Android 11 (API 30). The viewer's `compileSdk`
     and `targetSdk` are 36 (per `CLAUDE.md`), so the constant is available.
     The flag is a hint — drivers that don't honour it will simply ignore
     it, no version guard required.

No new files, no protocol fields, no public API changes.

## Phase 3 — Verification

Re-run the same end-to-end scenario as Phase 1 acceptance. Expected outcome:

- `stage=dec` PTS at any wall-instant is now within ~1-2 frame intervals
  (~33-67 ms) of `stage=enc` PTS, where it was 4-5 before.
- Per-side stage deltas (`enc → frag` on server, `reasm → dec` on viewer)
  drop noticeably; the encoder and decoder are no longer holding multiple
  frames.

If only one of the two fixes is responsible for the improvement, that becomes
visible in the per-side deltas and we can document which knob mattered. If
neither fix moves the needle, the counter still works and we have a tool for
the next round of investigation rather than a wasted session.

## Risk and rollback

- **NVENC `surfaces=1`** is aggressive; some NVENC builds may refuse. The
  encoder's existing error path raises `EncoderException` from
  `avcodec_open2`, so a refused setting will fail loudly at startup, not
  silently degrade.
- **`KEY_LOW_LATENCY`** is a hint and may have no effect on Quest 3 / Galaxy
  XR / Fold 6 silicon. That's fine — the fallback is the existing behaviour.
- All four log lines are pure additions — no existing behaviour changes.
  Phase 1 is trivially revertable; Phase 2 is two-line edits.

## Out-of-scope follow-ups

These are deliberately not part of this work but are reasonable next steps
once the constant lag is gone:

- Bumping capture+encode framerate to 60 FPS (halves sampling delay).
- GPU-resident capture path (eliminate `WgcFrameConverter`'s per-frame heap
  allocation and `sws_scale` CPU conversion).
- Per-stage trace at all eight points if a future bug needs it.
- Env gate / throttle on the FRAMECOUNT logs.
