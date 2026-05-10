# Swimmy-Era Baseline Test Handoff

**Status:** 🟡 **Pending — deferred from 2026-05-09 M5 measurement session.**

The M5 GXR measurement landed (cap → present **34 ms p50 / 51 ms p95** on Unity 4K @ 60 fps; spec section "Result (measured 2026-05-09, post-M5 GPU-resident pipeline)"). What's still outstanding is a **measured pre-perf-fix Unity 4K data point** so the latency timeline includes the actual subjective "swimmy and borderline" era rather than only the post-perf-fix typing-source approximation.

## Goal

Capture cap → dec p50 / p95 on Unity 4K @ 60 fps at a server vintage that pre-dates the 2026-04-26 NVENC-pipeline-depth fix series, so the project record has a directly comparable Unity-source baseline for the "swimmy" era.

Why "cap → dec" and not "cap → present": `stage=present` was added in commit `b9fc7f6`; commits before that lack the viewer-side present-stage instrumentation. Today's cap → dec figure is **≈23 ms p50 / 34 ms p95** (computable from the spec's per-stage breakdown: convert → reasm → dec deltas summed). The swimmy-era measurement gets compared against that, not against the cap → present number.

## Target vintage

Earliest commit with full server-side FRAMECOUNT (cap, enc, frag, reasm, dec) is **`83384b6`** ("feat(session): add stage=cap FRAMECOUNT site at WGC frame arrival"). At that point:

- NVENC input-surface queue depth = 3 (pre-`09515ff`)
- No `tune=ull` (pre-`a148243`)
- No `WIFI_MODE_FULL_LOW_LATENCY` viewer lock (pre-`4cc0fdf`)
- No `KEY_LOW_LATENCY` decoder hint (pre-`5dd97cc`)
- Default fps in encoder is 30 (pre-`0b347ed`)

That's the actual swimmy-era stack.

## Plan

1. **Pin a clean main checkpoint.** Already done — commits `724cf9b` (M5 measurement) and `f75076c` (timeline framing) are on main. `git stash` any uncommitted work before checkout.
2. **Build the server at `83384b6`.** `git checkout 83384b6`; `dotnet build src/WindowStream.Cli -f net8.0-windows10.0.19041.0`. NuGet packages should still resolve from cache.
3. **Build a matching old viewer APK at `83384b6`.** ⚠ Wire-protocol gotcha — see below.
4. **Run the same measurement harness as today.** Unity 4K @ 60 fps source on chonkers (`192.168.50.75`), GXR sink (`R3GYB04E2WB`), 150 s capture window (under the GXR sustained-4K lockup ceiling per `project_gxr_wifi_sustained_4k_lockup`).
5. **Analyze with `tools/framecount-analyze.py`.** The script parses lines that match `FRAMECOUNT[^a-z]*stage=...ptsUs=...wallMs=...`; the 83384b6 server emits this format already (no modification needed). Note the script's per-stage table will still produce reasonable output — the cap → dec sum will appear as the chain `convert → enc + enc → reasm + reasm → dec` (substituting `cap` for `convert` since the swimmy-era server emits `stage=cap` not `stage=convert`).
6. **Update the spec timeline.** Add a 5th row between rows 2 and 3 of the "Latency timeline" subsection in `docs/superpowers/specs/2026-04-25-frame-counter-and-pipeline-lag-fix-design.md`, or replace row 1 (the typing-source pre-fix) with the Unity-source equivalent. User decides framing.

## ⚠ Wire-protocol vintage gotcha

At `83384b6`, the server is **v1 single-window**: invoked as `serve --hwnd <handle>`, emits the v1 ServerHello (no windows array). The current portable viewer APK expects the **v2 ServerHello with windows array** (multi-window protocol from commit `d685fc5` and the v2 coordinator that landed sometime between `b9fc7f6` and current main).

The handshake will likely **silently fail** between v1 server and current v2 viewer — the protocol bytes won't deserialize and the viewer will sit on a 10s timeout with no actionable error (per `project_v2_server_silent_failure_modes`). This is easy to misdiagnose as a network or firewall issue.

**Two paths forward, in order of preference:**

### Path A — match server and viewer vintages

Build BOTH server and viewer at `83384b6`:

```bash
git checkout 83384b6
dotnet build src/WindowStream.Cli -f net8.0-windows10.0.19041.0
cd viewer/WindowStreamViewer
./gradlew :app:assembleDebug    # NB: pre-`211bc15` flavor split, APK output path differs
```

Risks:
- Gradle dependency rotation since 2026-04 — versions in `libs.versions.toml` may not resolve from current Maven mirrors.
- Pre-`211bc15` portable-flavor split: the APK will be at `viewer/WindowStreamViewer/app/build/outputs/apk/debug/app-debug.apk`, not the post-split portable path.
- Old viewer lacks `KEY_LOW_LATENCY` decoder hint (added in `5dd97cc`), so viewer-side decode will be slightly slower. Scientifically this **is** what we want — the swimmy-era baseline is the swimmy-era viewer too. The handoff doc just needs to explicitly note that the comparison is "swimmy server + swimmy viewer vs M5 server + M5 viewer."

### Path B — pick a vintage where v2 is in but GPU pipeline isn't

The v2 coordinator/worker split landed between the perf series and M3. Search:

```bash
git log --oneline 83384b6..1f96896 -- src/WindowStream.Cli/Hosting/  # likely WorkerCommandHandler appears here
```

If a commit exists where v2 protocol is implemented but the GPU pipeline (`D3D11VideoProcessorColorConverter`) hasn't landed, that vintage works against the current viewer APK with no rebuild. Then the comparison is "v2 sws_scale CPU-readback pipeline vs v2 GPU-resident pipeline" — strictly the encoder-stage delta, not the full swimmy era.

Path B is faster but measures a narrower delta. Path A measures the full subjective transition.

## Reference materials

- **Local scratch:** `.claude/scripts/timeline-reference.md` (gitignored) — full numerical timeline, methodology notes, today's setup recipe (CLI invocation, adb intent, logcat filter), and HWND/IP/serial pinned for repeatability.
- **Spec:** `docs/superpowers/specs/2026-04-25-frame-counter-and-pipeline-lag-fix-design.md`, sections "Result (measured 2026-04-26)" (row 1+2 of the timeline) and "Result (measured 2026-05-09, post-M5 GPU-resident pipeline)" (row 4 + per-stage breakdown).
- **Tool:** `tools/framecount-analyze.py` — joins server+viewer FRAMECOUNT logs by ptsUs, estimates server↔viewer clock skew from the floor of `enc → reasm`, prints per-stage p50/p95.
- **Earlier handoff with the same comparison target:** `docs/superpowers/handoffs/2026-05-04-m4-smoke-results-and-gxr-followup.md` already records the pre-M1 GXR baseline (cap → present 51 ms p50 / 66 ms p95 / 92 ms max) which corresponds to row 3 of the timeline. The b9fc7f6 commit message records the same numbers — they are the same measurement.

## Run conditions

- Both runs (today's M5 + future swimmy) ideally on an idle host. Today's was contended (concurrent batch-mode play tests on chonkers); the swimmy-era run should aim for the cleaner conditions to make the comparison fair.
- Same 150 s capture window. Same Unity 4K source window (HWND drifts across Unity restarts; re-run `windowstream list` to grab the current one).
- Per `feedback_hmd_test_explicit_cues`: pre-stage all commands, fire on user "GXR ON" confirm, signal "HMD OFF NOW" immediately after teardown.

## Open questions for the next session

1. Path A or Path B above? Path A is the proper swimmy-era measurement; Path B is cheaper and isolates the encoder-stage contribution.
2. Replace or extend? When the new row lands in the spec timeline, does it replace the typing-source row 1 (Unity is more comparable) or extend the table to 5 rows (preserves both measurements)? Recommend extend.
