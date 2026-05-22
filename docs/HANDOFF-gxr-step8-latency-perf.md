# Handoff: GXR latency-clock perf-regression check (encoder pool-ordering fix, Step 8)

**Status (2026-05-22, end of session):** Gitea [#6](https://gitea.llamabox.internal/schoen/WindowStream/issues/6) closed. The encoder pool-ordering fix is verified end-to-end on Fold 3 (portable APK, picker UI, 3 concurrent windows, zero `EncoderException`). The original implementation plan's **Task 7 Step 8** — HMD-camera latency-clock cold-start on Galaxy XR, comparing against the 2026-05-11 baseline — was not run this session because no GXR time was available.

This handoff exists so the next on-head GXR session can finish the verification in one short pass without re-deriving context from the (deleted) plan file.

---

## Why this is still worth doing

The dict-swap (`ConcurrentQueue` → `ConcurrentDictionary<(nint, int), nint>` in `FFmpegNvencEncoder`) introduces per-frame `TryAdd` / `TryRemove` on the encode hot path. The risk section of the original spec called perf regression out specifically:

- Dictionary ops are O(1) average but allocate on growth; if the pool grew during the test, p95 could shift.
- The new `ReleaseFrameTexture` path now fires on pause-skip; if that path is unexpectedly hot, it could add work.

Neither is *expected* to move the number — these are off the latency-critical path under steady-state — but the original plan called for empirical confirmation on the actual measurement vehicle (HMD camera) before declaring victory on the perf axis. The functional verification is complete; only the perf-regression check is open.

## The one-command test

```cmd
tools\latency-test
```

That's `tools\latency-test.bat`, a one-line wrapper around `tools\record-latency-clock.ps1 -ExecutionPolicy Bypass`. It handles the cold-start happy path end-to-end (Chrome `--kiosk` source, adb wifi connect, server launch, 4-second frame-flow probe, on-head gate, viewer-kill on record-end). Validated 2026-05-11 (`project_cold_start_latency_script`).

## What "no regression" means

Compare against the **2026-05-11 baseline** that landed with the M5/observability work:

| Metric | 2026-05-11 baseline | Fail bar |
|---|---|---|
| HMD-camera p50 | ~28 ms | regression > 5 ms p50 |
| HMD-camera p95 | ~40 ms | regression > 5 ms p95 |
| FRAMECOUNT reasm→dec p50 | ~9 ms | regression > 5 ms p50 |
| FRAMECOUNT reasm→dec p95 | ~13 ms | regression > 5 ms p95 |

Source: `project_input_present_2026_05_11_measurement` memory + `docs/superpowers/specs/2026-04-25-frame-counter-and-pipeline-lag-fix-design.md`.

A clean pass: HMD-camera p50 inside ~28 ± 5 ms with no obvious p95 widening.

## Source window selection — gotchas

The script defaults to Chrome `--kiosk` with a query-param cap target. Avoid the following per known WGC issues:

- **Firefox** — silent WGC fail (`project_firefox_wgc_silent_fail`); `ProbeCaptureSizeAsync` exits with no output.
- **Chrome `--kiosk`** — WGC frame-conversion fault (`project_chrome_kiosk_wgc_conversion_fail`). The script's cold-start path retries through the bust, but if it fails twice in a row, switch host: Terminal, Fork, or Unity all work.
- **Edge kiosk** — mid-session WGC bust (`project_edge_kiosk_wgc_session_bust`). Don't use as latency-clock host.

Terminal with a running spinner (e.g. `python -c "while True: print('.', end='', flush=True); time.sleep(0.033)"`) is the most reliable latency-clock source.

## GXR-specific preflight

- **Launcher icon crashes on Jetpack XR alpha04 × current Galaxy XR OS** (`project_gxr_jetpack_xr_alpha04_broken`). The latency-test script targets DemoActivity via adb intent, which works fine on GXR (`project_gxr_demoactivity_lifecycle_regression`). Don't try to launch from the launcher.
- **Proximity-card vs on-head TCP** is empirically variable per `project_xr_test_fleet`. Plan to do the test fully on-head; don't rely on the card for the recording window.
- **Wear-time discipline** per `feedback_hmd_test_explicit_cues` and `feedback_preflight_before_hmd` — verify cwd, source-window WGC probe, server port, viewer alive *before* signaling on-head. The latency-test script already encapsulates the preflight; if it greenlights the on-head gate, the rest of the run should be HMD time only.

## If perf regresses

The dict ops are the obvious suspect. Confirm by reading the `[FRAMECOUNT]` stream:

- If `stage=enc` is the regressing stage, the dict path is the cause. Capture a perf trace with `dotnet-trace` against the worker process.
- If `stage=convert` or `stage=cap` regress, look elsewhere — they're not on the dict path.
- If `reasm→dec` widens but `enc` is flat, the regression is downstream (network or decoder), not the encoder fix.

The dict approach is correct architecturally; rolling back would re-introduce the FIFO assertion under multi-worker contention. If the test shows a small but real regression (1-3 ms p50), don't roll back — document and accept, or look for an optimization (e.g. struct-keyed dict, pre-sized capacity).

## Closing the loop

If the latency clock passes the bar:

1. Add a comment to the (now-closed) Gitea #6 with the measurement.
2. Delete this handoff doc + the corresponding memory entry.
3. Update `project_encoder_pool_ordering_root_cause.md` to remove the "Step 8 still pending" note in the 2026-05-22 verification section.

If it fails: file a new Gitea issue with the measurement and a perf trace.
