# M4 Smoke Results + GXR Viewer Follow-up

**Status:** M4 server-side pipeline verified working (Fold E2E + GXR worker frames) on 2026-05-04. Branch `feature/m4-hwaccel-ingestion` is **still NOT merged to main** because the GXR end-to-end latency comparison vs the pre-M1 baseline could not be completed — viewer-side surface lifecycle race blocked frame reception on GXR. Server-side has no evidence of regression.

## What was measured this session

### Verified working

- **Fold 3, Unity 4K source, M4 pipeline, 248 paired frames over a multi-minute capture:**

  ```
  stage                              n   p50   p95   max
  -------------------------------------------------------
  enc -> reasm (network + frag)    248    11    47   137 ms
  reasm -> dec (decoder)           248    17    28    41 ms
  dec -> present (compositor)      248     8    21    45 ms
  enc -> present (end-to-end)      248    44    80   185 ms
  ```

- **Galaxy XR, Unity 4K source, M4 server-side worker:** worker process spawned cleanly, produced 848+ FRAMECOUNT lines (`stage=convert` + `stage=enc`) over ~2 minutes of capture; no encoder errors, no D3D11 errors, no GPU memory growth. The server-side pipeline (WGC → M3 video processor → M4 hwaccel NVENC) is functioning identically to its behaviour on the Fold session.

### NOT measured — the open work

**GXR end-to-end latency vs pre-M1 baseline.** Pre-M1 baseline (memory `project_gxr_wifi_powersave_jitter.md`, measured 2026-04-26 with WifiLock + tune=ull + gop=30 + fps=60 stack) was:

```
| stage                              | p50  | p95  | max  |
| cap -> enc (server, 4K H264 NVENC) | 28   |  37  |  46  |
| enc -> reasm (network)             |  1   |   9  |  31  |
| reasm -> dec (viewer)              | 10   |  15  |  24  |
| dec -> present (compositor)        | 11   |  17  |  41  |
| **cap -> present (end-to-end)**    |**51**|**66**|**92**|
```

So the pre-M1 baseline is **~51 ms p50 / ~66 ms p95 cap→present on GXR**, not the rough "~100 ms" figure that appears in `project_gpu_pipeline_refactor.md`. The 100 ms figure was a pre-stack-of-fixes approximation; the table above is the apples-to-apples target.

The Fold p50 of 44 ms enc→present is suggestive that M4 isn't slower, but Fold ≠ GXR — Fold has higher network RTT and a different decoder/compositor pipeline, so subtracting one measurement to predict the other is unreliable. **The actual M4 GXR comparison must be run.**

## Why the GXR measurement didn't land

The server is fine — it produced frames continuously. The viewer-side failure is at `DemoActivity.kt:309-312`:

```kotlin
val freshSurface: Surface = holder.surface
if (!freshSurface.isValid) {
    error("Surface for pipeline $streamIndex was released during pipeline startup; will retry on next surfaceCreated")
}
```

Repro: launch `DemoActivity` on Galaxy XR with explicit `streamHost` + `streamPort` + `selectedWindowIds`. The TCP/protocol handshake completes (ServerHello received, StreamStarted received with correct WxH+fps, viewer UDP socket bound, VIEWER_READY + RequestKeyframe sent). Then this exact `IllegalStateException` fires in `runPipeline` before `MediaCodecDecoder` can attach to the SurfaceView. The `runCatching { runPipeline(...) }` block calls `tearDownPipelineLocked` and the comment claims "the SurfaceView's next surfaceCreated callback will trigger a fresh attempt with the new Surface" — but in our session the next callback never fired (or fired after the user took the HMD off, killing the activity).

This is a **pre-existing GXR-specific viewer issue**, not introduced by M3 or M4. The protocol-handshake path is purely v2 viewer code, untouched by the GPU pipeline refactor. Rough working theories:
- The Galaxy XR XR-composition layer may recycle the SurfaceView surface differently from a regular 2D Android display — possibly during proximity-sensor-driven activity lifecycle transitions even when the OS reports `procState=TOP`.
- The TCP handshake on GXR took ~1 second (vs <100ms on Fold) because of the wifi RTT + radio state, which widens the window for surface recycling.
- The expected retry-on-next-surfaceCreated may not fire reliably on this device.

## Other bugs uncovered (file as separate Gitea issues, not M5 blockers)

1. **Server `activeChannel` state leak when a viewer's TCP closes uncleanly.** When the previous viewer is force-stopped from the host (e.g. `adb shell am force-stop`), the server's `ServeViewerAsync` does NOT promptly clear `activeChannel`. The next viewer hits the `if (busy)` branch in `RunAcceptLoopAsync` and gets a silent `ViewerBusy` ERROR which the viewer's `filterIsInstance<ServerHello>()` filter ignores → 10-second timeout with no actionable error. We saw this between the Fold disconnect and the GXR retry. Either the receive loop should detect the dropped socket faster, or the busy-rejection path should leave a clearer trail.

2. **`ResolveEncoderOptions` silent fallback when `ProbeCaptureSizeAsync` throws or returns null.** The viewer's auto-window-pick path (`serverHello.windows.firstOrNull()`) sometimes lands on an un-capturable window (we hit it on a "Smoke test" agent-tracker window with `WindowCaptureException: WGC frame conversion failed`). Server returns `WindowNotFound`, viewer waits silently for `StreamStarted` → 10s timeout. The exception logging is now landed in this branch (commit `62965ac`), but the deeper fix is to (a) filter advertised windows by capturability at server side OR (b) have the viewer retry with the next window when `StreamStarted` doesn't arrive within a budget.

3. **GXR Surface-release-during-startup race in `DemoActivity.runPipeline`** (the blocker for this smoke). The viewer should either delay surface acquisition until after the protocol handshake settles, retain a SurfaceHolder reference across the gap, or use a fresh-attempt loop that survives the surfaceDestroyed/surfaceCreated cycle.

## Code state on this branch (after this session)

- 9 commits since main: 8 from M4 implementation + 1 fixup `fix(hosting): surface worker stderr and probe failures (M4 follow-up)` (`62965ac`).
- The fixup turns two pre-existing silent-failure paths into noisy ones: worker-stderr is mirrored to parent stderr, and `ResolveEncoderOptions` logs the swallowed exception. Behaviour-preserving — only adds Console.Error.WriteLine and rethrows with a richer message. 296 unit tests pass, coverage steady at 94.93%/91.07% on `WindowStream.Core`.
- The branch is still NOT merged. Pending: GXR end-to-end measurement.

## Steps for the next session

### 0. Re-establish state

```powershell
cd C:\Users\mtsch\WindowStream\.worktrees\m4-hwaccel
git status                  # should be clean
git log --oneline main..HEAD # 9 commits, top is 62965ac
dotnet build src/WindowStream.Cli/WindowStream.Cli.csproj -f net8.0-windows10.0.19041.0 -c Release
```

### 1. Diagnose and fix the GXR Surface race

The repro:
```bash
# Server side (fresh)
"src/WindowStream.Cli/bin/Release/net8.0-windows10.0.19041.0/windowstream.exe" serve > m4-smoke-server.log 2>&1 &
PORT=$(grep -oE 'TCP [0-9]+' m4-smoke-server.log | head -1 | awk '{print $2}')
echo "TCP $PORT"

# GXR side (HMD on-head — see project_xr_test_fleet.md for the why)
adb mdns services           # discover wireless adb endpoint
adb connect <addr>:<port>   # from mdns output
DEV=<full mdns serial form>
adb -s "$DEV" shell am force-stop com.mtschoen.windowstream.viewer
adb -s "$DEV" logcat -c
adb -s "$DEV" shell am start -n com.mtschoen.windowstream.viewer/.demo.DemoActivity \
    --es streamHost 192.168.50.75 --ei streamPort $PORT \
    --ela selectedWindowIds <unity-window-id>
sleep 10
adb -s "$DEV" logcat -d -s WindowStreamDemo:V FRAMECOUNT:V SurfaceView:V VRI:V *:E
```

Expect the failure: `IllegalStateException: Surface for pipeline 0 was released during pipeline startup`. Possible fixes to evaluate:
- Move the `freshSurface = holder.surface` read **inside** the surfaceCreated callback's coroutine retry loop (so each retry starts from a known-good surface).
- Add a `withTimeout` around the surface-acquisition step that, on failure, retries N times with backoff before giving up (rather than relying on the next `surfaceCreated` callback firing).
- Switch the surface acquisition to a `MutableStateFlow<Surface?>` model where `surfaceCreated`/`surfaceDestroyed` push state changes and `runPipeline` collects until a valid surface arrives. This decouples "I got the protocol handshake" from "I have a surface."
- Confirm whether GXR's spatial-panel SurfaceView fires `surfaceCreated` again automatically after `surfaceDestroyed`; if not, the retry pattern needs a manual trigger.

This is **viewer-side Kotlin work**, separate from the M4 server changes. The M4 branch should not need modifications to fix it; the fix lives in `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/DemoActivity.kt`.

Recommend filing as a **separate PR/branch off main** rather than piggybacking on `feature/m4-hwaccel-ingestion`, because the M4 branch should merge based on server-side correctness alone once GXR receive can be verified independently.

### 2. Once GXR is receiving frames

Capture FRAMECOUNT data per the existing flow (server stderr to log, viewer logcat to log). Use `analyze-latency-m4.py` in `.claude/scripts/` to compute per-stage medians. The script is freshly written this session and handles the post-M3 schema.

### 3. Compare to pre-M1 baseline

The 51 ms p50 / 66 ms p95 / 92 ms max from `project_gxr_wifi_powersave_jitter.md` is the target. The M4 hypothesis is that removing sws_scale + CPU staging readback should drop `cap→enc` from 28 ms p50 to something closer to 5-15 ms (the spec said "5-15 ms median reduction at 4K"). End-to-end should be ~36-46 ms p50 if the spec's hypothesis holds.

If M4 lands within the spec's predicted improvement: write up the numbers in the spec's "Measured results" section, merge to main, file the three follow-up issues, write the M5 plan.

If M4 is at parity: still merge — the architectural simplification is worth it.

If M4 is *worse*: invoke the regression rule, do not merge, diagnose. Top suspects per the original handoff:
- `pendingPoolFramePointers` queue back-pressure when converter outpaces encoder (pool size 4)
- `BindFlags=RenderTarget|ShaderResource` on hw_frames_ctx textures affecting NVENC's preferred input format
- Shared `Direct3D11DeviceManager` immediate-context contention between WGC framepool and NVENC

## Useful artifacts left in the worktree

- `m4-smoke-viewer-fold.log` — Fold viewer logcat with 2148 FRAMECOUNT entries
- `m4-smoke-server-fold.log` — Server convert/enc events corresponding to the Fold session (extracted from the foreground tool-result file because the live server log got overwritten)
- `m4-smoke-viewer-gxr.log` — GXR's handshake-then-surface-failure logcat (22 lines, useful as a regression test for the surface fix)
- `m4-smoke-server.log` — Final server log including [worker:11568] FRAMECOUNT lines from the GXR attempt (server-side proof of frame production)

These are gitignored-by-extension OR untracked — do not commit them; they're transient observation artifacts.
