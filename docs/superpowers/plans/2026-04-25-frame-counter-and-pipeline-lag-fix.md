# Frame-counter instrumentation + pipeline-depth lag fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add four-stage `[FRAMECOUNT]` log instrumentation across server and viewer, use it to confirm the constant ~4-5 frame steady-state typing lag, then apply the two suspected pipeline-depth fixes (NVENC `surfaces=1`, MediaCodec `KEY_LOW_LATENCY=1`) and verify the gap drops to ~1-2 frames using the same instrumentation.

**Architecture:** Pure additive instrumentation — no protocol changes, no new files, no API changes. Server emits `[FRAMECOUNT] stage=enc|frag` to stderr at packet emit and at fragmenter handoff; viewer emits `[FRAMECOUNT] stage=reasm|dec` to logcat at flow-collect entry and at output-buffer-available callback. PTS in microseconds is the join key (already on the wire). After Phase 1 confirms the bug with real numbers, two minimal fixes are applied: one `av_opt_set` line in the NVENC configure path, one `setInteger` line on the Android `MediaFormat`.

**Tech Stack:** .NET 8 / FFmpeg.AutoGen (server), Kotlin / Android `MediaCodec` (viewer), `Console.Error.WriteLine` + `adb logcat` for observation, `Stopwatch`/`System.currentTimeMillis()` for monotonic wall clocks.

**Source spec:** `docs/superpowers/specs/2026-04-25-frame-counter-and-pipeline-lag-fix-design.md`

---

## Status (closed 2026-04-26)

Tasks 1-7 complete and committed on `main`. Task 8 (Phase 3 end-to-end verification on real hardware) **partially complete**:

- ✅ **Server-side** validated by direct measurement on Fold 6: NVENC queue depth dropped from 3 frames to 1, `cap → enc` median 751ms → 252ms, matching the user's "4-5 keypresses behind" symptom and confirming the fix. Result section in spec captures the numbers.
- ❌ **GXR end-to-end subjective test** blocked by a separately-tracked regression: `DemoActivity` reliably enters STOPPED state ~150ms after launch on Galaxy XR (same APK works on Fold 6). Documented as memory entry `project_gxr_demoactivity_lifecycle_regression` with a suggested bisect from commit `7079049 v1 thesis proved end-to-end: Windows → Galaxy XR streaming works!`. Treat that bisect as the next-session starting point.

A bonus side fix that landed during measurement work: `d5a7f1c fix(encoder): clamp sws_scale srcSliceH to configured height` — unblocks any odd-dimensional capture target.

---

## File-by-file scope

| File | Change |
|------|--------|
| `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs` | Task 1: emit `stage=enc` log. Task 6: add `surfaces=1` option. |
| `src/WindowStream.Core/Session/SessionHost.cs` | Task 2: emit `stage=frag` log. |
| `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/decoder/MediaCodecDecoder.kt` | Task 3: emit `stage=reasm` and `stage=dec` logs. Task 7: set `KEY_LOW_LATENCY=1`. |
| `CLAUDE.md` | Task 4: add `FRAMECOUNT:V` to documented logcat filter. |

No new files. No tests added (verification is end-to-end manual observation per spec — see Tasks 5 and 8).

---

### Task 1: Emit `[FRAMECOUNT] stage=enc` from the NVENC encoder

**Files:**
- Modify: `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs` (imports at top; receive-packet loop in `EncodeOnThread` near lines 192-211)

- [ ] **Step 1: Add `using System.Diagnostics;` import**

Open `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs`. The current imports already include `System.Diagnostics.CodeAnalysis` but **not** `System.Diagnostics` itself. `Stopwatch` lives in the latter. Add it after the existing `System.Diagnostics.CodeAnalysis` line so the block reads:

```csharp
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
```

- [ ] **Step 2: Insert the log line in the receive-packet loop**

Locate this block in `EncodeOnThread` (around lines 204-210):

```csharp
            byte[] managed = new byte[packet->size];
            Marshal.Copy((IntPtr)packet->data, managed, 0, packet->size);
            bool isKeyframe = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            long timestampMicroseconds = 1_000_000L * packet->pts
                * context->time_base.num / context->time_base.den;
            chunkChannel.Writer.TryWrite(new EncodedChunk(managed, isKeyframe, timestampMicroseconds));
            ffmpeg.av_packet_unref(packet);
```

Insert two lines between the `timestampMicroseconds` calculation and the `chunkChannel.Writer.TryWrite` call:

```csharp
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
```

Notes:
- The enclosing method is already annotated `[ExcludeFromCodeCoverage]` (FFmpeg native path), so the new line inherits the exclusion — coverage gate is unaffected.
- Variable name uses full words per project convention. The log key `wallMs=` stays terse for greppability — log keys are output format, not code identifiers.

- [ ] **Step 3: Build to confirm the file compiles**

Run: `dotnet build src/WindowStream.Core/WindowStream.Core.csproj`

Expected: build succeeds with no errors.

- [ ] **Step 4: Run the full test suite to confirm nothing regressed**

Run: `dotnet test`

Expected: all tests pass; coverage gate (100% line + branch on `WindowStream.Core`) still passes. The single new line is unconditional (no branches) and lives inside `[ExcludeFromCodeCoverage]`, so it does not move coverage numbers.

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs
git commit -m "$(cat <<'EOF'
feat(encoder): emit FRAMECOUNT log at packet emit (stage=enc)

First of four FRAMECOUNT instrumentation sites for diagnosing the constant
~4-5 frame steady-state typing lag. PTS in microseconds is the cross-side
join key; wall-clock from Stopwatch keeps server-internal stage deltas
monotonic.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Emit `[FRAMECOUNT] stage=frag` from `SessionHost`

**Files:**
- Modify: `src/WindowStream.Core/Session/SessionHost.cs` (imports at top; `RunEncodePumpAsync` near lines 99-142)

- [ ] **Step 1: Add `using System.Diagnostics;` import**

Open `src/WindowStream.Core/Session/SessionHost.cs`. Current imports do not include `System.Diagnostics`. Add it so the block reads:

```csharp
using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Encode;
using WindowStream.Core.Protocol;
using WindowStream.Core.Transport;
```

- [ ] **Step 2: Insert the log line just before the fragmenter `foreach`**

Locate this block in `RunEncodePumpAsync` (around lines 120-130):

```csharp
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
```

Insert two lines between `currentSequence` assignment and the `foreach`:

```csharp
                byte[] nalUnit = chunk.payload.ToArray();
                int currentSequence = sequence++;
                long fragWallClockMilliseconds = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
                System.Console.Error.WriteLine(
                    $"[FRAMECOUNT] stage=frag ptsUs={chunk.presentationTimestampMicroseconds} wallMs={fragWallClockMilliseconds}");
                foreach (FragmentedPacket packet in fragmenter.Fragment(
                    streamId: options.StreamId,
                    sequence: currentSequence,
                    presentationTimestampMicroseconds: chunk.presentationTimestampMicroseconds,
                    isIdrFrame: chunk.isKeyframe,
                    nalUnit: nalUnit))
                {
                    await udpSender.SendPacketAsync(packet, destination, cancellationToken).ConfigureAwait(false);
                }
```

Notes:
- Variable name `fragWallClockMilliseconds` follows full-word convention. Same `wallMs=` log key for greppability across stages.
- This line is unconditional (no branches), so the 100% branch-coverage gate is unaffected. Existing integration tests exercise this path; line coverage will be picked up automatically.

- [ ] **Step 3: Build**

Run: `dotnet build`

Expected: build succeeds.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test`

Expected: all tests pass; coverage still 100% on `WindowStream.Core`.

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Session/SessionHost.cs
git commit -m "$(cat <<'EOF'
feat(session): emit FRAMECOUNT log at fragmenter handoff (stage=frag)

Second of four FRAMECOUNT instrumentation sites. Together with stage=enc
this lets us measure how long an encoded packet sits in the encoder's
output channel before being fragmented and handed to UDP.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Emit `[FRAMECOUNT] stage=reasm` and `stage=dec` from `MediaCodecDecoder`

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/decoder/MediaCodecDecoder.kt` (imports already include `android.util.Log`; flow collect near line 73, output callback near line 48)

- [ ] **Step 1: Insert `stage=reasm` log at the top of `frameFlow.collect`**

Locate the `decodeJob` block (around lines 73-90):

```kotlin
        decodeJob = scope.launch(Dispatchers.IO) {
            frameFlow.collect { encodedFrame ->
                val parsedParameterSets: ParameterSets = ParameterSetParser.extract(encodedFrame.payload)
                if (parsedParameterSets.sequenceParameterSet != null && parsedParameterSets.pictureParameterSet != null) {
                    cachedParameterSets = parsedParameterSets
                }
                val bufferIndex: Int = inputBufferIndexChannel.receive()
```

Add the log line as the first statement inside the `frameFlow.collect` lambda:

```kotlin
        decodeJob = scope.launch(Dispatchers.IO) {
            frameFlow.collect { encodedFrame ->
                Log.d(
                    "FRAMECOUNT",
                    "stage=reasm ptsUs=${encodedFrame.presentationTimestampMicroseconds} wallMs=${System.currentTimeMillis()}"
                )
                val parsedParameterSets: ParameterSets = ParameterSetParser.extract(encodedFrame.payload)
                if (parsedParameterSets.sequenceParameterSet != null && parsedParameterSets.pictureParameterSet != null) {
                    cachedParameterSets = parsedParameterSets
                }
                val bufferIndex: Int = inputBufferIndexChannel.receive()
```

`EncodedFrame.presentationTimestampMicroseconds` is the `Long` PTS field already used by `queueInputBuffer` lower in the same method — same value, same units as the server's `stage=enc` PTS, so direct PTS-equality joins are valid.

- [ ] **Step 2: Insert `stage=dec` log in `onOutputBufferAvailable`**

Locate the existing callback (around lines 48-53):

```kotlin
            override fun onOutputBufferAvailable(
                mediaCodec: MediaCodec, outputBufferIndex: Int, bufferInformation: MediaCodec.BufferInfo
            ) {
                mediaCodec.releaseOutputBuffer(outputBufferIndex, surface != null)
                frameSink.onFrameRendered(bufferInformation.presentationTimeUs)
            }
```

Insert one line between `releaseOutputBuffer` and `onFrameRendered`:

```kotlin
            override fun onOutputBufferAvailable(
                mediaCodec: MediaCodec, outputBufferIndex: Int, bufferInformation: MediaCodec.BufferInfo
            ) {
                mediaCodec.releaseOutputBuffer(outputBufferIndex, surface != null)
                Log.d(
                    "FRAMECOUNT",
                    "stage=dec ptsUs=${bufferInformation.presentationTimeUs} wallMs=${System.currentTimeMillis()}"
                )
                frameSink.onFrameRendered(bufferInformation.presentationTimeUs)
            }
```

- [ ] **Step 3: Build the viewer to confirm it still compiles**

Run from repo root:

```bash
cd viewer/WindowStreamViewer && ./gradlew :app:compilePortableDebugKotlin
```

Expected: compilation succeeds.

- [ ] **Step 4: Run viewer unit tests**

Run from `viewer/WindowStreamViewer`:

```bash
./gradlew :app:testPortableDebugUnitTest
```

Expected: all unit tests pass. Both new log calls are unconditional (no branches), so Kover branch-coverage is unaffected. There is no direct unit test for `MediaCodecDecoder` (it is exercised by `LoopbackEndToEndTest` instrumented test) so no test file needs updating.

- [ ] **Step 5: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/decoder/MediaCodecDecoder.kt
git commit -m "$(cat <<'EOF'
feat(viewer): emit FRAMECOUNT logs at reasm and dec stages

Third and fourth of four FRAMECOUNT instrumentation sites. stage=reasm
fires when a fully reassembled EncodedFrame enters the decode flow;
stage=dec fires when MediaCodec hands an output buffer back for render.
Together with the two server-side stages this gives a four-point view
of where a frame's time is spent.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Add `FRAMECOUNT:V` to the documented logcat filter

**Files:**
- Modify: `CLAUDE.md` line 103

- [ ] **Step 1: Edit the logcat filter command**

The current line in `CLAUDE.md` reads:

```
- Viewer logs: `adb logcat -d --pid=$(adb shell pidof com.mtschoen.windowstream.viewer) -s WindowStreamDemo:V MediaCodec:V MediaCodecDecoder:V *:E` filters to relevant output.
```

Replace with:

```
- Viewer logs: `adb logcat -d --pid=$(adb shell pidof com.mtschoen.windowstream.viewer) -s WindowStreamDemo:V MediaCodec:V MediaCodecDecoder:V FRAMECOUNT:V *:E` filters to relevant output. The `FRAMECOUNT` tag emits one line per frame at `stage=reasm` (reassembler complete) and `stage=dec` (output buffer rendered); pair with server stderr `[FRAMECOUNT]` lines (`stage=enc`/`stage=frag`) to measure pipeline-depth latency. PTS in microseconds is the join key across server/viewer.
```

- [ ] **Step 2: Commit**

```bash
git add CLAUDE.md
git commit -m "$(cat <<'EOF'
docs: add FRAMECOUNT:V to documented logcat filter

Surfaces the new four-stage instrumentation in the same logcat command
the rest of the project's debugging tips already use, plus a one-line
explainer of how to join with the server stderr counterpart.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Phase 1 acceptance — observe the baseline ~4-5 frame lag

Pure verification gate. No code, no commit. Records the **before** number that Phase 3 will compare against.

- [ ] **Step 1: Build and install both sides fresh**

From repo root:

```bash
dotnet build && \
  cd viewer/WindowStreamViewer && \
  ./gradlew :app:assemblePortableDebug && \
  adb install -r app/build/outputs/apk/portable/debug/app-portable-debug.apk && \
  cd ../..
```

Expected: server compiles, APK builds, installs cleanly to the connected device.

- [ ] **Step 2: Pick a HWND with active content**

```bash
dotnet run --project src/WindowStream.Cli -f net8.0-windows10.0.19041.0 -- list
```

Note: pick a HWND with **even width and height** and **actively-updating content** (terminal cursor blinking, editor with caret, etc.) per `CLAUDE.md` gotchas.

- [ ] **Step 3: Run the server with stderr captured**

```bash
dotnet run --project src/WindowStream.Cli -f net8.0-windows10.0.19041.0 -- serve --hwnd <handle> 2> /tmp/windowstream-server-phase1.log
```

Server stays running until you Ctrl-C it.

- [ ] **Step 4: Launch the viewer and let it discover the server**

In Android launcher, tap the WindowStream Viewer icon (or use the adb intent fallback documented in `CLAUDE.md` if on Galaxy XR). Pick the discovered server in the picker. Once connected, video should be flowing.

- [ ] **Step 5: Capture viewer logs in a separate terminal**

```bash
adb logcat -c && \
  adb logcat -s FRAMECOUNT:V > /tmp/windowstream-viewer-phase1.log
```

Let it run for ~10 seconds with the source window updating actively.

- [ ] **Step 6: Stop logging, server, viewer**

Ctrl-C both the logcat and the server. Force-stop the viewer:

```bash
adb shell am force-stop com.mtschoen.windowstream.viewer
```

- [ ] **Step 7: Sanity-check the four log streams exist**

```bash
grep -c 'stage=enc'   /tmp/windowstream-server-phase1.log
grep -c 'stage=frag'  /tmp/windowstream-server-phase1.log
grep -c 'stage=reasm' /tmp/windowstream-viewer-phase1.log
grep -c 'stage=dec'   /tmp/windowstream-viewer-phase1.log
```

Expected: all four counts non-zero and roughly equal. If any is zero, the log site for that stage is broken — fix it before continuing. (Most likely cause: forgot to rebuild/reinstall after editing.)

- [ ] **Step 8: Compute the steady-state PTS gap**

Pick a wall-clock instant in the middle of the run (not the first or last second — startup transients distort the picture). Find:

- The most recent `stage=enc` line at or before that instant on the server side.
- The most recent `stage=dec` line at or before that instant on the viewer side.

The two sides have unsynchronised clocks, so do **not** subtract `wallMs` directly. Instead subtract the `ptsUs` values:

```
gap_microseconds = (stage=enc ptsUs) - (stage=dec ptsUs)
gap_frames_at_30fps = gap_microseconds / 33333
```

A repeatable way: pick three sample instants spaced ~3 seconds apart, compute the gap at each, take the median.

- [ ] **Step 9: Acceptance gate**

Expected: the gap is roughly **4-5 frames** (≈133-167 ms in PTS terms) and stable across the three samples (steady-state, not growing). Record the number — call it `BASELINE_GAP_FRAMES`.

If the observed gap is **dramatically different** from the felt symptom — e.g. 0 frames, or growing without bound — the counter is wrong, not the bug. Stop here and re-investigate the instrumentation before doing Phase 2. If the gap roughly matches "feels like 4-5 frames", you have green light to proceed.

- [ ] **Step 10: Per-side stage decomposition (optional but useful)**

For the same three sample instants, also note:

- Server `stage=frag` `wallMs` − server `stage=enc` `wallMs` for the same `ptsUs` (encoder-output → fragmenter delay; should be sub-millisecond on a healthy machine).
- Viewer `stage=dec` `wallMs` − viewer `stage=reasm` `wallMs` for the same `ptsUs` (reassembled-frame → rendered-frame delay; this is where MediaCodec's reorder buffer lives).

These are the **before** decompositions for comparison after Phase 2.

---

### Task 6: Apply NVENC `surfaces=1` to remove encoder pipeline depth

**Files:**
- Modify: `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs` `OpenCodecAndAssignOptions` (around lines 89-92)

- [ ] **Step 1: Add the `surfaces=1` option**

Locate the existing block:

```csharp
        ffmpeg.av_opt_set(context->priv_data, "preset", "p1", 0);
        ffmpeg.av_opt_set(context->priv_data, "tune", "ll", 0);
        ffmpeg.av_opt_set(context->priv_data, "zerolatency", "1", 0);
        ffmpeg.av_opt_set(context->priv_data, "rc", "cbr", 0);
```

Add one line at the end of the block:

```csharp
        ffmpeg.av_opt_set(context->priv_data, "preset", "p1", 0);
        ffmpeg.av_opt_set(context->priv_data, "tune", "ll", 0);
        ffmpeg.av_opt_set(context->priv_data, "zerolatency", "1", 0);
        ffmpeg.av_opt_set(context->priv_data, "rc", "cbr", 0);
        ffmpeg.av_opt_set(context->priv_data, "surfaces", "1", 0);
```

`surfaces` controls NVENC's pre-allocated input surface count, which is the dominant component of its internal pipeline depth. The existing pattern of calling `av_opt_set` and ignoring the return value is preserved — if NVENC's internal validation rejects the value, the failure surfaces from `avcodec_open2` below, which already throws `EncoderException`.

- [ ] **Step 2: Build**

Run: `dotnet build`

Expected: succeeds.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test`

Expected: all tests pass.

If `dotnet test` now fails specifically inside an integration test that calls `avcodec_open2` (i.e. NVENC refuses `surfaces=1` on this build of FFmpeg), the test failure message will surface as `EncoderException: avcodec_open2 failed.`. In that case, bump the value: try `"2"` then `"3"` until the encoder opens cleanly. Note the working value in the commit message. (The default before this change was `0`/auto, which on most NVENC builds resolves to ~4, the value the spec hypothesises is producing the lag.)

- [ ] **Step 4: Commit**

```bash
git add src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs
git commit -m "$(cat <<'EOF'
perf(encoder): cap NVENC input surface queue to 1

Drops NVENC's internal pipeline depth — the dominant suspect for the
constant ~4-5 frame steady-state lag observed via FRAMECOUNT
instrumentation. surfaces=1 requests one-in-one-out behaviour. Compatible
with existing preset=p1 / tune=ll / zerolatency=1 / rc=cbr settings; if
this NVENC build rejects 1 the encoder will fail loudly at avcodec_open2.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: Apply MediaCodec `KEY_LOW_LATENCY=1` to remove the decoder reorder buffer

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/decoder/MediaCodecDecoder.kt` (around lines 38-43)

- [ ] **Step 1: Set `KEY_LOW_LATENCY` on the `MediaFormat` before `configure`**

Locate this block in `start`:

```kotlin
        val mediaFormat = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, expectedWidth, expectedHeight)
        val newCodec = if (codecName != null) {
            MediaCodec.createByCodecName(codecName)
        } else {
            MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        }
```

Insert one line right after `mediaFormat` is created:

```kotlin
        val mediaFormat = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, expectedWidth, expectedHeight)
        mediaFormat.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
        val newCodec = if (codecName != null) {
            MediaCodec.createByCodecName(codecName)
        } else {
            MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        }
```

`MediaFormat.KEY_LOW_LATENCY` was added in Android 11 / API 30. The viewer's `compileSdk` and `targetSdk` are 36 per `CLAUDE.md`, so the constant is available at compile time. The flag is a hint — drivers that don't honour it (or don't recognise it on older firmware) silently ignore it, so no runtime version guard is needed.

- [ ] **Step 2: Build**

```bash
cd viewer/WindowStreamViewer && ./gradlew :app:compilePortableDebugKotlin && cd ../..
```

Expected: compiles cleanly.

- [ ] **Step 3: Run viewer unit tests**

```bash
cd viewer/WindowStreamViewer && ./gradlew :app:testPortableDebugUnitTest && cd ../..
```

Expected: all tests pass. The change adds one unconditional line; Kover line and branch coverage are unaffected.

- [ ] **Step 4: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/decoder/MediaCodecDecoder.kt
git commit -m "$(cat <<'EOF'
perf(viewer): set MediaCodec KEY_LOW_LATENCY=1

Hints the HW decoder to skip its reorder buffer and emit frames as soon
as they decode. Second of two pipeline-depth fixes targeting the
constant ~4-5 frame steady-state lag observed via FRAMECOUNT
instrumentation. The key is advisory: drivers that don't honour it fall
back to existing behaviour, no version guard required.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: Phase 3 verification — confirm the gap closed

Pure verification gate. No code, no commit. Records the **after** number and attributes the win to encoder vs decoder.

- [ ] **Step 1: Rebuild and reinstall both sides**

From repo root:

```bash
dotnet build && \
  cd viewer/WindowStreamViewer && \
  ./gradlew :app:assemblePortableDebug && \
  adb install -r app/build/outputs/apk/portable/debug/app-portable-debug.apk && \
  cd ../..
```

- [ ] **Step 2: Re-run the same scenario as Phase 1**

Use the **same source HWND** as in Task 5 if at all possible — keeping the source content stable between runs makes the before/after comparison meaningful.

```bash
dotnet run --project src/WindowStream.Cli -f net8.0-windows10.0.19041.0 -- serve --hwnd <same handle> 2> /tmp/windowstream-server-phase3.log
```

In a second terminal, capture viewer logs:

```bash
adb logcat -c && adb logcat -s FRAMECOUNT:V > /tmp/windowstream-viewer-phase3.log
```

Connect the viewer, let it run for ~10 seconds with active source content, then stop both.

- [ ] **Step 3: Compute the new steady-state PTS gap**

Same procedure as Task 5 step 8: median of three sample instants spaced ~3 seconds apart, gap in `ptsUs`, divide by 33333 for frames at 30 FPS. Call this `POST_FIX_GAP_FRAMES`.

- [ ] **Step 4: Compute per-side stage deltas (the attribution step)**

For the same three sample instants, compute as in Task 5 step 10:

- Server: `stage=frag` wallMs − `stage=enc` wallMs for matching `ptsUs`. This delta should still be sub-millisecond — `surfaces=1` does not change this delta directly, but **observed `stage=enc` wallMs values for new frames should arrive much sooner relative to the original input frame**, which is the primary win.
- Viewer: `stage=dec` wallMs − `stage=reasm` wallMs for matching `ptsUs`. This delta is **where `KEY_LOW_LATENCY` shows up**. If it dropped substantially, `KEY_LOW_LATENCY` was honoured. If unchanged, this device's driver ignored the hint.

- [ ] **Step 5: Acceptance gate**

Expected: `POST_FIX_GAP_FRAMES` ≤ 2 (≈67 ms or less in PTS terms), down from the `BASELINE_GAP_FRAMES` ≈ 4-5 recorded in Task 5. The character-typed-N-frames-ago feel should subjectively be gone or dramatically reduced when typing into the source window.

If the gap dropped only partially (e.g. 4 → 3, not 4 → 1):

- The per-side decomposition in step 4 tells you which fix landed and which didn't. Document in a summary commit (next step) which knob was responsible.
- This is acceptable progress, not a regression — Phase 3 doesn't have to bring the gap to zero, only confirm directional improvement.

If the gap did **not** drop:

- Re-check that the new APK actually installed (`adb shell dumpsys package com.mtschoen.windowstream.viewer | grep versionCode` against a clean build, or check install timestamp).
- Re-check that the rebuilt server is the one actually running.
- Both fixes are advisory on some hardware. Galaxy XR / Quest 3 / Fold 6 may behave differently — note the device under test in any followup.

- [ ] **Step 6: Document the result**

Add a one-paragraph summary at the bottom of the spec file `docs/superpowers/specs/2026-04-25-frame-counter-and-pipeline-lag-fix-design.md` under a new `## Result` heading: device tested, baseline gap, post-fix gap, which knob was attributable. Then commit:

```bash
git add docs/superpowers/specs/2026-04-25-frame-counter-and-pipeline-lag-fix-design.md
git commit -m "$(cat <<'EOF'
docs: record Phase 3 result for pipeline-depth lag fix

Captures the observed before/after FRAMECOUNT gap and per-side stage
deltas so the spec records what landed and which knob was responsible.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-review

**Spec coverage check** (from `docs/superpowers/specs/2026-04-25-frame-counter-and-pipeline-lag-fix-design.md`):

| Spec section | Covered by |
|--------------|------------|
| Phase 1 — `stage=enc` log site | Task 1 |
| Phase 1 — `stage=frag` log site | Task 2 |
| Phase 1 — `stage=reasm` log site | Task 3 |
| Phase 1 — `stage=dec` log site | Task 3 |
| Phase 1 — Shared log format with PTS join key | Tasks 1-3 (format string identical across sites; `wallMs` keys; PTS values are the same `Long` `presentationTimestampMicroseconds`/`timestampMicroseconds` value across server and viewer) |
| Phase 1 — `Stopwatch` wall clock on server, `System.currentTimeMillis()` on viewer | Tasks 1, 2 (Stopwatch); Task 3 (currentTimeMillis) |
| Phase 1 — No env gate / always-on | Tasks 1-3 (no conditional wrappers added) |
| Phase 1 — CLAUDE.md `FRAMECOUNT:V` filter update | Task 4 |
| Phase 1 — Acceptance gate (~4-5 frame gap observed) | Task 5 |
| Phase 2 — NVENC pipeline-depth fix | Task 6 |
| Phase 2 — MediaCodec `KEY_LOW_LATENCY=1` | Task 7 |
| Phase 3 — Verify gap drops to ~1-2 frames | Task 8 |
| Phase 3 — Per-side stage attribution | Task 8 step 4 |
| Phase 3 — Document outcome | Task 8 step 6 |

No gaps.

**Placeholder scan:** no `TBD`, `TODO`, `fill in`, `similar to Task N`, or generic "add error handling" steps. Code blocks contain literal code; commands contain literal commands.

**Type / name consistency:**
- Server log key `wallMs` matches viewer log key `wallMs`. Server uses local variable `wallClockMilliseconds` (Task 1) and `fragWallClockMilliseconds` (Task 2) — same casing convention, full words, no clash.
- `presentationTimestampMicroseconds` is the verified Kotlin property name on `EncodedFrame` (read from source) and matches the server-side `chunk.presentationTimestampMicroseconds` and `timestampMicroseconds` local. The PTS values are guaranteed identical because the server-side `timestampMicroseconds` is what gets serialised into the wire packet's PTS field, which the viewer's reassembler restores into `EncodedFrame.presentationTimestampMicroseconds`.
- `MediaFormat.KEY_LOW_LATENCY` is a real platform constant (Android 11+, `compileSdk=36`).
- `surfaces` is the FFmpeg.AutoGen NVENC option name set on `priv_data`, matching the surrounding pattern.

**Outcome:** plan is internally consistent and covers every spec requirement.

---

## Execution handoff

Two execution options:

**1. Subagent-Driven (recommended)** — fresh subagent per task, review between tasks, fast iteration via the `superpowers:subagent-driven-development` skill.

**2. Inline Execution** — execute tasks in this session via `superpowers:executing-plans`, batched with checkpoints for review.
