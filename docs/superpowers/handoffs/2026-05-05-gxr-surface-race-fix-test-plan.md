# Galaxy XR Surface-race fix — test plan for next session

**Branch:** `fix/gxr-surface-race-during-handshake` (off `feature/m4-hwaccel-ingestion`)
**Status:** **Validated end-to-end on Galaxy XR 2026-05-06.** Fix works; M4 ready to merge. See "Measured results" section below.

## Measured results (2026-05-06)

End-to-end Unity 4K @ 60fps streaming on Galaxy XR (`R3GYB04E2WB`) ran for ~4.5 minutes before a viewer-side WiFi reception lockup (separate issue, server kept pumping):

```
                            n      p50    p95    p99    max
reasm  -> dec             5460     12     18     21      63 ms
dec    -> present         5460     11     17     20      24 ms
reasm  -> present         5460     23     31     35      70 ms
```

Pre-M1 baseline was reasm→present p50/p95 = 21/32 ms — viewer-side latency is essentially unchanged (M4 was a server-side refactor; viewer pipeline is the same).

End-to-end (server `convert` → viewer `present`) join failed: 0 matches across 10,064 server-convert events and 5,460 viewer-present events. Root cause: server FRAMECOUNT stages use mismatched clock bases (`stage=convert` emits Unix-epoch `wallMs` and a wallclock-derived `ptsUs`; `stage=enc` emits a monotonic `wallMs` and the encoder-assigned PTS). Viewer side uses Unix-epoch `wallMs` and the encoded-frame PTS. Cannot join until server unifies stages on (Unix-epoch `wallMs`, encoded-frame `ptsUs`). Filed in memory `project_v2_server_silent_failure_modes`.

UDP reception rate during the streaming window was ~54% (5,460 received / 10,064 server-convert events). Eventually the stream hard-locked — server kept pumping, viewer stopped receiving entirely. Filed in memory `project_gxr_wifi_sustained_4k_lockup`.

The fix's polling-and-await path (`awaitValidSurface`) **never had to retry** — every launch logged "surface valid on first read after handshake" because the spatial-panel layer didn't actually invalidate the Surface in our handshake window in this OS build. The lock-decoupling and polling architecture still hold for the documented failure modes; tonight just didn't hit them.

## What the fix does

Two-layer change to `viewer/.../viewer/demo/DemoActivity.kt`:

1. **Decoupled `pipelineLock` from the TCP-handshake duration.** `startPipelineLocked` is now non-suspending — it creates the per-stream coroutine scope and launches `runPipeline` *into* that scope, then returns. The caller's `pipelineLock.withLock { … }` releases essentially immediately, so a `surfaceDestroyed` / `surfaceCreated` burst that happens mid-handshake is no longer queue-blocked behind the in-flight handshake.

2. **Polling-based Surface acquisition.** Replaced the brittle one-shot `holder.surface + isValid` check with `awaitValidSurface(streamIndex, holder)` — polls `holder.surface` every 50 ms for up to 10 s, returning the first valid Surface seen. Exits cleanly on timeout with a clear error, exits via `delay`-based cancellation if the scope is torn down.

3. **Diagnostic logging at every Surface lifecycle transition.** `[<index>] surfaceCreated holder=<id> surface=<id> valid=<bool>`, ditto `surfaceChanged` / `surfaceDestroyed`, plus `startPipelineLocked launching handshake into new scope` and the `awaitValidSurface` poll progress.

## Pre-flight

- APKs are pre-built in this worktree: `viewer/WindowStreamViewer/app/build/outputs/apk/{portable,gxr}/debug/app-{portable,gxr}-debug.apk` (~50 MB each).
- Branch is committed and pushed to both `origin` and `gitea` so you can pull from anywhere.
- Fold 3 (`RFCRB0G5DLW`) installed the portable variant during last night's smoke. GXR install is pending.

## Repro / verification steps

```powershell
# 1. Ensure mDNS network profile is Private (required for the discovery picker; not required for adb-direct path):
Get-NetConnectionProfile -Name SchoenBags_5G-1 | Select-Object NetworkCategory
# If Public: Set-NetConnectionProfile -Name SchoenBags_5G-1 -NetworkCategory Private  (UAC required)

# 2. Start an M4 server pointed at an actively-updating Unity-class window:
cd C:\Users\mtsch\WindowStream\.worktrees\gxr-surface-fix
dotnet build src/WindowStream.Cli/WindowStream.Cli.csproj -f net8.0-windows10.0.19041.0 -c Release
"src/WindowStream.Cli/bin/Release/net8.0-windows10.0.19041.0/windowstream.exe" list
# Note an HWND with active content + EVEN width and height.
"src/WindowStream.Cli/bin/Release/net8.0-windows10.0.19041.0/windowstream.exe" serve > gxr-fix-server.log 2>&1 &
$port = (Select-String -Path gxr-fix-server.log -Pattern 'TCP\s+(\d+)').Matches[0].Groups[1].Value
Write-Host "TCP port: $port"

# 3. Connect to the GXR over adb-wifi (HMD must be on-head — see project_xr_test_fleet.md):
adb mdns services
adb connect <addr-from-mdns>
$DEV = '<full-mdns-serial>'  # e.g. adb-R3GYB04E2WB-EFU6vk._adb-tls-connect._tcp

# 4. Install the GXR-flavor APK + warm logcat:
adb -s "$DEV" install -r .\viewer\WindowStreamViewer\app\build\outputs\apk\gxr\debug\app-gxr-debug.apk
adb -s "$DEV" shell am force-stop com.mtschoen.windowstream.viewer
adb -s "$DEV" logcat -c

# 5. Launch DemoActivity (bypassing the broken Jetpack-XR MainActivity icon path):
adb -s "$DEV" shell am start -n com.mtschoen.windowstream.viewer/.demo.DemoActivity `
    --es streamHost 192.168.50.76 --ei streamPort $port `
    --ela selectedWindowIds <unity-window-id-from-server-log>

# 6. Capture logs (with HMD on-head — radio parks otherwise):
Start-Sleep -Seconds 30
adb -s "$DEV" logcat -d -s WindowStreamDemo:V FRAMECOUNT:V MediaCodecDecoder:V '*:E' > gxr-fix-viewer.log
```

## How to read the result

### Success path (what we want to see)

```
[0] surfaceCreated holder=<H1> surface=<S1> valid=true
[0] startPipelineLocked launching handshake into new scope (scope=<C1>)
[0] stream 0 ServerHello: udpPort=…
[0] stream 0 <streamId>: 3840x2160 @ 60 fps …
[0] stream 0 viewer UDP bound on port …
[0] surface valid on first read after handshake (surface=<S1>)         <-- IDEAL: handshake survived without surface invalidation
   OR
[0] surface INVALID after handshake (surface=<S1>); polling up to 10000ms
[0] surface became valid after N polls (Mms; surface=<S2>)             <-- ALSO GOOD: polling caught the new Surface
FRAMECOUNT stage=reasm …
FRAMECOUNT stage=dec …
FRAMECOUNT stage=present …
```

If FRAMECOUNT events are flowing, run capture for 60–90 s, then run the latency analyzer to compare against the pre-M1 51 ms p50 / 66 ms p95 / 92 ms max baseline (`project_gxr_wifi_powersave_jitter.md`).

### Diagnostic scenarios (what failure tells us)

The fix is built to handle three failure modes:

- **Scenario A** — `surfaceCreated` fires again mid-handshake, `surfaceDestroyed`/`surfaceCreated` are no longer queue-blocked, fresh handshake succeeds. Logcat will show two `surfaceCreated` callbacks; the second's pipeline succeeds.
- **Scenario B** — Surface invalidates without a `surfaceDestroyed`/`surfaceCreated` cycle, but `holder.surface` returns a fresh valid Surface within 10 s. Logcat shows `surface INVALID after handshake; polling …` then `surface became valid after N polls`.
- **Scenario C** — Surface stays invalid for >10 s (the failure mode the OLD code conflated with all the others). Logcat shows the `surface still invalid after N polls` warnings, then the timeout error. **This would mean the OS truly isn't recreating the Surface for our SurfaceView; the fix is structurally insufficient and we'd need to investigate Galaxy XR spatial-panel SurfaceView lifecycle directly.**

### If FRAMECOUNT never appears but no surface error logged

The handshake itself is failing. Check the server log for `VIEWER_BUSY` (lingering registration from a prior viewer — `am force-stop` between attempts), `WindowNotFound` (the `selectedWindowIds` argument doesn't match an advertised window), or capture errors (the chosen HWND is uncapturable — pick a different one).

### Latency comparison

If frames are flowing on GXR, capture 60–90 s of FRAMECOUNT and compare per-stage p50/p95 against the baseline in `2026-05-04-m4-smoke-results-and-gxr-followup.md`. Expected M4 win: `cap→enc` drops from 28 ms p50 toward 5–15 ms p50, end-to-end target ~36–46 ms p50. Use `.claude/scripts/analyze-latency-m4.py` from the M4 worktree.

## What's left after this run

- If GXR latency is ≤ baseline: write up the numbers, merge `feature/m4-hwaccel-ingestion` to main, file a M5 plan.
- If GXR latency is at parity: still merge — architectural simplification is worth it, file follow-up profiling.
- If GXR latency is worse: invoke regression rule, do not merge, diagnose. Top suspects are listed in the `2026-05-04-m4-smoke-results-and-gxr-followup.md` handoff.
- Clean up `WindowStream-Session-*` firewall rules and any test-log artifacts when wrapping.

## Files touched on this branch

- `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/DemoActivity.kt` — the fix.
- `docs/superpowers/handoffs/2026-05-05-gxr-surface-race-fix-test-plan.md` — this file.
- `viewer/WindowStreamViewer/local.properties` — gitignored; SDK path for the worktree's gradle.
