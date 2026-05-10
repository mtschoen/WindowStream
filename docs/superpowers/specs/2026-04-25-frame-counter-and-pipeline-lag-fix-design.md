# Frame-counter instrumentation + pipeline-depth lag fix

**Date:** 2026-04-25
**Status:** ✅ Implemented and merged

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

## Result (measured 2026-04-26)

Bug confirmed and fix applied. PTS-based instrumentation alone could not see
the bug (PTS is assigned at encoder-emit, not capture). Adding a fifth
`stage=cap` site at WGC frame arrival, combined with a discrete-event
synthetic source (250ms timer mimicking typing pace), exposed NVENC's
internal input-surface queue as the dominant lag source. Tested on a
Samsung Fold 6 (`RFCRB0G5DLW`) over USB adb against `192.168.50.76`.

| Metric | Before fix | After `surfaces=1` |
|---|---:|---:|
| Median NVENC queue depth (cap − enc) | 3 frames | 1 frame |
| Median cap → enc per-frame lag | 751 ms | 252 ms |
| Median enc → frag lag | 0 ms | 0 ms |

The queue-depth drop matches the user's reported "4-5 keypresses behind"
symptom: at ~250ms event spacing, 3 buffered frames produce ~750ms felt
lag, and the fix reduces that to one in-flight frame's worth (~250ms,
which is structural — NVENC cannot pipeline less than one frame).

`KEY_LOW_LATENCY` was applied separately during the session by the user;
its viewer-side effect was not isolated in this measurement (no overlap
between viewer and server runs at the low event rate due to a
VIEWER_READY race, see open followups). Subjective end-to-end typing
verification on a live Claude session is the remaining validation step.

The Phase 2 plan also called for fixing the `sws_scale` odd-height crash
that was blocking measurement; that fix landed as commit `d5a7f1c`.

## Result (measured 2026-05-09, post-M5 GPU-resident pipeline)

End-to-end run on current `main` (commit `c51b88a`, M5 GPU pipeline cleanup
+ FRAMECOUNT clock fix). Source: Unity 6.0 4K window on chonkers
(`192.168.50.75`). Sink: Galaxy XR (`R3GYB04E2WB`) over Wi-Fi via TLS adb,
portable-flavor `DemoActivity` intent. Capture window 150 s, 3,814 frames
joined across all five stages. Tool: `tools/framecount-analyze.py`,
estimating server↔viewer clock skew from the floor of `enc → reasm` and
backing it out from cross-source deltas.

| Stage delta                            | n     | p50 (ms) | p95 (ms) | min | max |
|----------------------------------------|------:|---------:|---------:|----:|----:|
| convert → enc (server, GPU→NVENC)      | 3,814 |        8 |       13 |   4 |  90 |
| enc → reasm (network + reassembly)     | 3,814 |        4 |        9 |   0 |  53 |
| reasm → dec (viewer decode)            | 3,814 |       11 |       15 |   6 |  55 |
| dec → present (viewer render)          | 3,814 |       11 |       17 |   1 |  24 |
| **convert → present (END-TO-END)**     | 3,814 |   **34** |   **51** |  17 | 114 |

NVENC queue depth (`convert → enc`, in-flight frames): median 1, p95 1,
max 2. Confirms `surfaces=1` from the original Phase 2 fix is still
holding through the GPU-resident texture-pool path.

Comparison vs the 2026-04-26 measurement (synthetic 250 ms typing source,
pre-GPU-resident pipeline):

| Metric                              | 2026-04-26 | 2026-05-09 |
|-------------------------------------|-----------:|-----------:|
| Median NVENC queue depth            |    1 frame |    1 frame |
| Median capture → encoder lag        |     252 ms | **8 ms** (convert → enc) |
| Median enc → frag lag               |       0 ms | (frag stage retired in M5) |

The capture → encoder collapse from 252 ms → 8 ms reflects the M3+M4+M5
work: GPU-resident colour conversion via `ID3D11VideoProcessor` replaces
`sws_scale` CPU readback, FFmpeg's `hw_frames_ctx` gives NVENC its input
texture by reference rather than via host upload, and the clock-alignment
fix in M5 #3 forwards the WGC capture PTS through to encoder packets so
all five stages share one ptsUs axis.

Subjective end-to-end verification on the live demo: motion in Unity
appears at perceptual parity with native rendering on the headset; user
reports the source window was responsive enough to play the game in
play-mode and edit in the Editor while wearing the HMD.

**Run conditions caveat.** Throughout this 150 s capture, chonkers was
also running Unity batch-mode play-mode tests in a separate session,
contending for the same GPU and CPU. The post-skew-corrected numbers
above are therefore an upper bound on contended-host latency, not a
clean-host best case — a quiet-host re-run is expected to come in
tighter. The pipeline holding at p50 34 ms / p95 51 ms despite that
contention suggests meaningful steady-state headroom for multi-window
expansion before host-side resources become the binding constraint.

### Latency timeline

Authoritative measurements across the latency-reduction arc, each
recorded at the time it was taken. Stages and sources differ between
rows and are noted in-line; rows 3 and 4 are the only directly
comparable FRAMECOUNT pair (same Unity 4K source, same WGC capture
path, same chonkers→GXR Wi-Fi sink). The 2026-05-10 HMD-camera rows are
a separate, complementary methodology — see "Methodology comparison"
below.

| Date | Build | Source | Stage measured | p50 | p95 | What this row captures |
|---|---|---|---|---:|---:|---|
| 2026-04-26 | pre-`09515ff` | typing (~4 events/s) | cap → enc | **751 ms** | — | Pre-perf-fix baseline. NVENC input-surface queue depth = 3, structurally bounding worst-case low-rate latency. The "swimmy" era as felt under typing-cadence load. |
| 2026-05-09 (re-measured) | pre-`09515ff` (`83384b6`, queue=3, no `tune=ull`) | Unity 4K active @ ~50 fps | cap → dec | **17 ms** | **35 ms** | Same swimmy-era stack as row 1, but Unity-4K-at-rate source instead of typing. The 751 ms typing-source floor is not present here — sustained 4K capture exercises NVENC fast enough that depth-3 vs depth-1 surface queueing is invisible (median in-flight depth = 1, max = 1). The swimmy era's pipeline-depth bug was load-pattern-specific to sparse-but-bursty input. |
| 2026-04-26 | post-`09515ff` (`surfaces=1`) | typing (~4 events/s) | cap → enc | **252 ms** | — | After capping NVENC's input queue. Knocked the structural low-rate floor down to one in-flight frame. |
| 2026-05-?? | `b9fc7f6` (post the full perf series, pre-M3 GPU pipeline) | Unity 4K @ 60 fps | cap → present | **51 ms** | 66 ms | Steady-state Unity baseline after the full 2026-04-26 perf-fix series (`surfaces=1`, `tune=ull`, GOP 30, 60 fps default, viewer Wi-Fi-low-latency lock). At 60 fps NVENC's queue cycles fast enough that the typing-rate floor is not load-bearing — this is what end-to-end Unity 4K looked like just before M3 began. (Recorded in the `b9fc7f6` commit message, not re-measured.) |
| 2026-05-09 | `c51b88a` main (M3+M4+M5 GPU-resident pipeline) | Unity 4K @ 60 fps | cap → present | **34 ms** | 51 ms | Includes M3 D3D11 video processor, M4 NVENC hwaccel ingestion via `hw_frames_ctx`, and M5 cleanup + clock-alignment fix. |
| 2026-05-10 | current main (post-M5 + viewer `KEY_LOW_LATENCY`) | 165 Hz Chrome kiosk `latency-clock.html` | cap → present (FRAMECOUNT) | **30 ms** | 40 ms | 1067 paired frames. Per-stage: convert→enc 8/11, enc→reasm 2/7, reasm→dec 12/17, dec→present 11/17. NVENC queue depth median 1, max 2. Source-rate-insensitive (165→30 fps cap moves number <2 ms). |
| 2026-05-10 | current main (same build as above) | 165 Hz Chrome kiosk `latency-clock.html` | **input → present** (HMD camera) | **~48 ms** | (σ ≈ 0) | End-to-end via HMD passthrough camera + GXR-rendered virtual panel in the same frame. ~18 ms above the FRAMECOUNT cap→present number — that gap is WGC frame-arrival + HMD passthrough chain (out of WindowStream's control). 4 reads at 3 s spacing. |
| 2026-05-10 | swimmy vintage `83384b6` | 165 Hz Chrome kiosk `latency-clock.html` | **input → present** (HMD camera) | **~248 ms** | (σ ≈ 9 ms) | Same HMD-camera methodology against vintage. **~5× current-main on the same source and viewer link**, despite per-frame transit (cap→dec) being statistically the same as M5 per row 2. The win is pipeline-depth collapse, not per-frame work. 4 reads: 248/230/248/249 ms. |

Reading the arc: rows 1 → 3 was the **2026-04-26 NVENC pipeline-depth
fix** (~3× cap→enc reduction at typing-rate input). Row 2 (the new Unity
re-measurement at the same swimmy vintage) shows the depth-3 issue did
not manifest under sustained Unity 4K — the bug was input-pattern
specific. Row 3 → row 4 is not a direct comparison (different source,
different stage); row 4 is the cleanest snapshot of "system tuned, but
encoder still does CPU readback + sws_scale before NVENC." Row 4 → row
5 is the **GPU-resident pipeline's specific contribution** at 4K@60:
**−17 ms p50 / −15 ms p95 cap → present** (~33% reduction off an
already-tight baseline).

**Latency vs jitter — what the per-frame numbers don't show.** Row 2's
17 ms p50 cap → dec is similar to row 5's 23 ms p50 cap → dec
(synthesized from row 5's per-stage breakdown). But the 2026-05-09
re-measurement also exposed a ~6× difference in encoder-stage
**inter-arrival variance**: swimmy-era stdev was 16.3 ms with 15 gaps
≥100 ms over 150 s of active Unity, vs M5 era stdev 2.6 ms with 0 gaps
≥100 ms in comparable conditions. The CPU sws_scale readback path could
not sustain steady WGC delivery at 4K — bursts and stalls produced the
subjective "swimmy" feel even when individual frames were fast through
the wire. The GPU-resident pipeline's actual gift is **variance
reduction**, not headline p50 latency. Per-frame transit was nearly
this fast at swimmy vintage too on a best-case frame; what changed is
that *every* frame is now best-case.

The subjective "swimmy and borderline → snappy and responsive"
transition therefore spans two distinct mechanisms: the 2026-04-26
NVENC fix removed the typing-cadence structural lag ("4-5 keypresses
behind"), and the M3+M4+M5 GPU-resident pipeline removed the 4K
WGC-stall-induced jitter that made motion feel uneven even when the
mean latency looked fine.

**Methodology comparison — FRAMECOUNT vs HMD camera.** The two
methodologies measure different segments of the pipeline and disagree
in informative ways:

- **FRAMECOUNT cap→present** (rows 2 and 6) instruments the
  WindowStream pipeline from WGC `OnFrameArrived` through MediaCodec
  output buffer render. It does **not** include the source-paint →
  WGC-arrival gap, nor anything between MediaCodec output and the HMD
  panel.
- **HMD camera input→present** (rows 7 and 8) measures the gap between
  a clock rendered in the source window and the same clock as it
  appears on the HMD panel, both visible to the passthrough camera in
  the same recorded frame. This sees the full chain: source paint →
  WGC arrival → entire WindowStream pipeline → HMD panel composite.

For current main, the two methods agree where they should: HMD camera
~48 ms minus FRAMECOUNT 30 ms ≈ 18 ms of out-of-pipeline contribution
(WGC frame-arrival latency + HMD passthrough chain). That gap is
constant-ish and not attackable from the WindowStream side.

For swimmy vintage, however, FRAMECOUNT cap→dec p50 was 17 ms (row 2,
Unity 4K @ ~50 fps) while HMD-camera input→present p50 was ~248 ms
(row 8, Chrome kiosk @ 165 Hz). The same out-of-pipeline ~18 ms
contribution can't account for a 230 ms gap. The remainder is
**pipeline depth** — frames sitting in queues between WGC arrival and
encode entry — that FRAMECOUNT cap→dec can't see because it tracks an
individual frame's transit, not the depth of the queue it had to wait
in. Sources at this scale (165 Hz vs 50 fps Unity) can't fully account
for it either; source-rate insensitivity on current main (row 7) shows
the pipeline floor is what dominates.

The 2026-04-26 NVENC fix and the M3+M4+M5 GPU-resident pipeline
together collapsed that pipeline-depth tail. Per-frame transit was
already fast at swimmy vintage on the rare best-case frame (row 2);
end-to-end input→present was not, because most frames weren't
best-case. **Headline:** swimmy-era → current-main is a **~5×
input→present reduction** (~248 → ~48 ms), even though FRAMECOUNT
per-frame transit looked nearly flat across the same arc.

The earlier "Future work — end-to-end measurement" gap is now closed:
HMD-camera input→present has been measured on both ends of the arc.
Methodology, source artifact (`tools/latency-clock.html`), and recording
script (`.claude/scripts/record-latency-clock.bat` + vintage variant)
are durable and re-runnable.
