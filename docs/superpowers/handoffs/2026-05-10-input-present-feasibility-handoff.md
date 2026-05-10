# Input→Present Feasibility — 2026-05-10

**Status:** Methodology validated; one clean baseline screenshot captured (~82 ms WindowStream-pipeline delta vs passthrough on current main). Real vintage-vs-current A/B not yet run. Session ended at high token cost; resume clean next session.

## What landed (committed)

- `8d6f185` — pruned 4 stale handoffs (M1, M4 ×2, GXR Surface-race), captured the two open M4-followup bugs into `docs/superpowers/specs/2026-04-20-multi-window-followups.md` "Server-side reliability" section.

## What's in the working tree (uncommitted)

- `tools/latency-clock.html` — HTML clock for the input→present measurement. HH:MM:SS.fff text at top (RAF-driven, runs at monitor refresh rate; uses `tabular-nums` so digits don't shift), plus a **horizontal sweep bar** below — marker traverses left→right over 1000 ms, ticks every 10/100 ms. Camera blur becomes an informative streak whose midpoint encodes exposure-center time.
- `.claude/scripts/record-latency-clock.bat` — one-shot HMD-on script. Force-stops viewer, fires DemoActivity intent at 192.168.50.75:61613, sleeps 3s, runs `adb shell screenrecord --time-limit 15 --bit-rate 20M`, pulls mp4 + extracts a midpoint frame via ffmpeg. **Auto-discovers HWND** via `windowstream list | grep "latency clock"` (temp-file approach to avoid `for /f` quoting fragility) — override with first positional arg.
- `.claude/scripts/feasibility-recording-*.mp4` and `-frame.jpg` — diagnostic captures from this session (gitignored).

## Methodology verdict

The passthrough+virtual-side-by-side approach **works**. One screenshot from this session showed:
- Real-monitor clock (passthrough): `02:52:04.785`
- WindowStream panel clock (virtual): `02:52:04.703`
- Delta: **~82 ms** = WindowStream pipeline contribution above passthrough constant
- For A/B vintage-vs-current, the passthrough constant cancels exactly — no calibration needed

Lights-on dramatically improves clock readability (camera shutter shortens). Sweep bar (added later this session) makes single-sample reads sub-decisecond even with motion blur. Switch from screenshots to **HMD video** for distribution; N samples → mean noise σ/√N, single-sample blur is irrelevant for the headline number.

## What's blocking — three real bugs surfaced this session

### 1. Edge kiosk WGC capture broke mid-session
After capturing successfully on Edge HWND `1179754` (Test 2, the 82 ms screenshot), every subsequent OPEN_STREAM on the same HWND threw `ProbeCaptureSizeAsync threw: WindowCaptureException: WGC frame conversion failed`. Closing kiosk Edge and relaunching produced a fresh HWND `21041616` that **also** failed WGC. A Terminal window (HWND `3149920`) **succeeded** OPEN_STREAM in the same server session — so the bug is Edge-specific, not coordinator-wide.

Likely cause: Edge's DirectComposition path on this Windows version became hostile to WGC after the orphan-worker scenario (see #2). May also have been an Edge background update mid-session. **Restart-from-cold next session — should clear it.** If still busted, switch source to Chrome (`--kiosk file:///...`) or a Unity scene (per CLAUDE.md, Unity is known-good for WGC).

### 2. Orphan worker after viewer self-exit (concrete instance of M4-followup bug #1)
Test 2's viewer self-exited (cause unknown — could be GXR off-head sandbox gating per `reference_gxr_app_network_gate`, or activity lifecycle). Server detected the TCP close but **the worker process kept running**, holding the WGC capture session on HWND `1179754`. Subsequent OPEN_STREAM probes on that HWND failed because WGC said "already captured." Killed worker PID 26232 with taskkill; the next OPEN_STREAM on the original HWND **still** failed (different bug — see #1 for what was actually broken about that window).

The activeChannel state-leak we wrote up this morning has this concrete consequence: **viewer self-exit → orphan worker → WGC session locked → subsequent OPEN_STREAM on that HWND fails.** Worth refining the bug entry in the followups spec to mention the worker-orphan consequence. Eventually fix: when ServeViewerAsync's TCP receive throws, tear down the worker for that stream.

### 3. GXR SurfaceView destroyed-during-startup race (regression?)
When testing Terminal capture, viewer logs showed `surfaceDestroyed` immediately after `surfaceCreated`, then `pipeline cancelled during startup`. This is the bug commit `64a2a74` was supposed to fix. The fresh APK (installed today, lastUpdateTime 2026-05-10 02:50:50) may not include or may not be triggering the retry path properly. May also have been triggered by HMD off-head briefly during the test.

Worth checking next session if it reproduces with HMD strictly on-head and stable.

## Resume sequence for next session

```powershell
# 1. Open the latency clock fresh
Start-Process msedge -ArgumentList '--kiosk','file:///C:/Users/mtsch/WindowStream/tools/latency-clock.html'
# (if Edge WGC still broken, swap msedge for chrome)

# 2. Start fresh server (build is already cached at Release)
$env:WINDOWSTREAM_SKIP_NVENC = $null  # ensure NVENC is allowed
& '.\src\WindowStream.Cli\bin\Release\net8.0-windows10.0.19041.0\windowstream.exe' serve > .claude\scripts\feasibility-server.log 2>&1
# (run in background or new terminal)

# 3. HMD on; run the script (auto-discovers HWND, fires viewer + 15s screenrecord)
.claude\scripts\record-latency-clock.bat
```

If the script's screen-recording captures the WindowStream panel content (not just passthrough) — green-light methodology, write the spec, run vintage A/B.

If the panel is black in the recording but visible on-head — confirms SurfaceView/MediaCodec hardware-overlay theory; fall back to HMD built-in screen recorder for the real measurement.

## Open work (in order)

1. Re-validate end-to-end (steps above) — fresh state should clear the Edge WGC bug.
2. Write the input→present spec at `docs/superpowers/specs/2026-05-10-input-present-measurement.md` (or similar). Sections: methodology, source/sink, calibration caveat, how vintage backport works (cherry-pick clock-reader is N/A — clock is a simple HTML file, vintage server captures it transparently; just need vintage viewer build, see `~/.claude/notes/reference_windowstream_swimmy_vintage_gotchas.md`).
3. Run current-main measurement: 15–30 s HMD video × ≥3 trials.
4. Run swimmy-vintage measurement at `83384b6` (per gotchas note: `dotnet run` not `dotnet <dll>`).
5. Land 5th row in `docs/superpowers/specs/2026-04-25-frame-counter-and-pipeline-lag-fix-design.md` "Latency timeline" section.
6. (Lower priority) File Gitea issues for the 3 bugs surfaced above; refine "Server-side reliability" section in multi-window-followups spec to mention the orphan-worker WGC-lock consequence.

## Useful artifacts left from this session

- `.claude/scripts/feasibility-recording-20260510-025204.jpg` (HMD built-in screenshot showing the 82 ms delta — the headline measurement)
- `.claude/scripts/feasibility-recording-20260510-032546.mp4` and `-frame.jpg` — adb screenrecord with passthrough capturing cleanly, no virtual panel (pipeline failed)
- `.claude/scripts/feasibility-server.log` — full server-side history of this session, including all `[openstream]` traces and the `WindowCaptureException` patterns

These can stay in `.claude/scripts/` (gitignored) or be moved/deleted as you prefer.
