# Handoff: Latency regression check — 5/11 baseline A/B rollback

**Status (2026-05-23):** User maintains the GXR stream latency has regressed
since the 2026-05-11 baseline. Today's post-reboot re-test on current `main`
did **not** show a regression in on-device telemetry — but the perception
persists, so the next step is a direct **same-session A/B** of the **5/11
code** vs **current `main`** under identical conditions. Same session matters:
LG-TV display path and Wi-Fi conditions drift between sessions, so historical
cross-session comparison is unreliable (this is the whole reason the morning's
34 ms vs a historical 28 ms was inconclusive).

## The dispute — what today's re-test found

Current `main` (server rebuilt today; viewer APK = current `main`,
clean-installed), GXR `R3GYB04E2WB`, `tools\latency-test -Duration 30`,
1730 paired frames:

| Stage (skew-corrected where cross-device) | 5/11 baseline | Today 5/23 | Δ |
|---|---|---|---|
| convert→enc (NVENC)   | 5 / 11 | 4 / 5  | −1 / −6 |
| enc→reasm (network)   | 3 / 7  | 3 / 7  | 0 |
| reasm→dec (decode)    | 9 / 13 | 7 / 11 | −2 / −2 |
| dec→present (vsync)   | 10 / 17| 9 / 18 | ~flat |
| convert→present (E2E) | 28 / 40| 24 / 35| −4 / −5 |

FRAMECOUNT = **no regression, slightly faster**. HMD-camera hand-reads
(n≈15, de-aliased) = **~37 ms median**, consistent with the morning run's
34 ms — NOT the 28 ms it was compared against. The 28 ms HMD-camera "baseline"
is from a session whose own memory ([[input-present-2026-05-11-measurement]])
flags its camera reads as unreliable (same session produced 76 ms reads).

**Telemetry says no regression; the user perceives one.** This A/B reconciles
that directly, and catches anything the content-latency clock does NOT measure:
this clock measures **source→panel content latency only**, NOT
**motion-to-photon / reprojection** (head-move → panel). If the perceived
sluggishness is head-motion-related, this test won't see it — that would need
a separate motion-to-photon measurement.

## Arm A (current main) — data already captured today
- Recording: `tools/feasibility-recording-20260523-185623.mp4` (30 s passthrough)
- Viewer FRAMECOUNT: `tools/framecount-20260523-185741.log`
- Server stderr (convert/enc): `%TEMP%\windowstream-serve-20260523-185623.err.log` (re-capture if cleared)
- Parser: `python tools/parse-framecount.py <viewer.log> <server.err.log>`

## Arm B (5/11) — build & run

**Rollback target: `d62b950`** (last commit on 2026-05-11). It is post-Tier-1a
viewer hints (`8f22294`) and **pre** the two viewer-side suspects:
`e00a607` Tier-1b decoder (5/14) and `8f41469` FileLoggingTree per-write
flush (5/19). Verified: no `src/` or `viewer/` changes between `8f22294` and
`d62b950`, so `d62b950`'s latency code == the measured baseline.

```
git worktree add C:\Users\mtsch\WindowStream-5-11 d62b950
cd C:\Users\mtsch\WindowStream-5-11
dotnet build -c Release src/WindowStream.Cli/
cd viewer/WindowStreamViewer && ./gradlew :app:assemblePortableDebug
adb -s <gxr> uninstall com.mtschoen.windowstream.viewer
adb -s <gxr> install app/build/outputs/apk/portable/debug/app-portable-debug.apk
```

**Run with the CURRENT fixed harness, not the 5/11 one**, so the test rig is
identical across arms (only the binaries differ). The current
`tools/record-latency-clock.ps1` (this session's fixes) already:
- parses BOTH the new structured `TcpPort = N, UdpPort = N` banner AND the old
  `windowstream: serving on TCP N, UDP N` banner — **the 5/11 server emits the
  OLD one**, and the regex now handles it; and
- launches a screen-filling chromeless `--app` source (WGC-clean) instead of
  `--kiosk`.

Easiest: copy the current fixed `tools/record-latency-clock.ps1`,
`tools/latency-clock.html`, `tools/latency-test.bat` into the worktree
(overwriting 5/11's `--kiosk`/old-banner copies), then run
`tools\latency-test -Duration 30` from the worktree so `$CliExe` resolves to
the 5/11-built `windowstream.exe`. Parse both arms with `tools/parse-framecount.py`.

## Suspects (if a regression IS confirmed)
Both viewer-side, both landed after 5/11:
- **`8f41469` FileLoggingTree per-write flush (5/19)** — STRONGEST suspect.
  Flushes the log to disk on every write; if FRAMECOUNT logs per-frame on the
  viewer render thread, that is per-frame disk I/O that could push actual
  present later — **visible to the HMD camera, invisible to the reasm/dec/present
  stage deltas** (those are timestamped before the flush). This cleanly explains
  "FRAMECOUNT clean but perceptible feels worse."
- **`e00a607` Tier-1b low-latency decoder selection (5/14)**.

### Faster surgical probe (try BEFORE the full worktree rollback)
~10 min: on current `main`, revert ONLY `8f41469` (or gate the per-write flush
behind a flag / make it buffered), rebuild + reinstall the viewer APK, re-run.
If HMD-camera drops, the flush is the culprit — skip the rollback entirely.

## Decisive interpretation
- **5/11 also reads ~37 ms** HMD-camera under identical conditions → NO
  regression; the 28 ms was always camera noise. Close the investigation.
- **5/11 reads clearly lower (~28 ms)** under identical conditions → real
  regression. Bisect FileLoggingTree-flush vs Tier-1b-decoder by reverting each
  on current `main` and re-measuring.

## Pre-flight gotchas (don't burn HMD time)
- **GXR wireless-debugging pairing does NOT survive a reboot.** mDNS advertises
  `_adb-tls-connect` but `adb connect` fails the TLS handshake while plain TCP
  to the port succeeds → re-pair via Wireless debugging → "Pair device with
  pairing code" (pairing port + 6-digit code; **ports rotate on reboot**).
  See [[gxr-adb-pairing-lost-on-reboot]].
- **Verify it's the GXR**: `adb -s <id> shell getprop ro.product.model` ==
  `SM_I610` (guards the [[gxr-serial-misattributed-2026-05-14-to-23]] Fold-3 mixup).
- **GXR app-TCP is gated off-head** — be on-head/carded from the step-5 probe
  through the record.
- **HMD-camera hand-reads: sample CONSECUTIVE native frames, NOT `fps=1`.**
  fps=1 aliases to one vsync phase (panel/monitor ms read identical every
  second). Use `ffmpeg -ss T -t 0.7 rec.mp4 frame-%02d.jpg` bursts; ~27 ms
  steps walk the phase. n≥15 across phases.
- portable-flavor split (`211bc15`) predates 5/11 → APK name/launch identical
  to today; no special handling.

## Tooling state (this session's fixes, on current main)
- `tools/record-latency-clock.ps1`: `--kiosk`→screen-filling `--app`; banner
  regex handles both formats. WGC-clean. (Why not `--kiosk`/`--start-fullscreen`:
  true fullscreen bypasses DWM composition → WGC `CreateForWindow` busts. See
  [[wgc-fullscreen-app-capture-future-goal]].)
- `tools/parse-framecount.py`: FRAMECOUNT stage-latency percentile parser
  (within-device deltas are skew-immune; cross-device floored to network=0).
