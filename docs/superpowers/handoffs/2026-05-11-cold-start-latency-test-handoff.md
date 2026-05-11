# Handoff: implement the cold-start latency-clock script

**Date:** 2026-05-11
**For:** the fresh session that picks this up
**From:** brainstorm + plan session (this one)

## What's queued

A complete implementation plan at:

```
docs/superpowers/plans/2026-05-11-cold-start-latency-test.md
```

It builds **one new file** — `tools/record-latency-clock.ps1` —
which replaces the existing `tools/record-latency-clock.bat`. The
script takes the HMD-camera latency-clock test from a true cold start
(HMD on, nothing else running) to a 15-second screenrecord on disk,
with fail-fast diagnostics on every step that has historically
failed.

## Why this exists

The 2026-05-11 Tier 1a measurement session lost ~30 min of HMD wear
time to setup failures the user could not predict:

- adb daemon needed warming
- `adb connect` ip:port had to be entered manually (mDNS sometimes
  lists the GXR, sometimes doesn't)
- background bash didn't preserve cwd
- `cmd /c` quoting mangled through bash tool
- Chrome `--kiosk` lost focus mid-recording (WGC source-window class
  of failure)

The existing `.bat` skipped four of the cold-start steps. The new
`.ps1` does them all and aborts off-head with a clear next-action
when any fails.

Memory `feedback_preflight_before_hmd.md` was the catalyst.

## How to pick this up

1. **Open the plan** at `docs/superpowers/plans/2026-05-11-cold-start-latency-test.md`.
2. **Pick an execution mode:**
   - `subagent-driven-development` (recommended) — one fresh subagent
     per task, two-stage review between tasks. Fast iteration. Note
     that the smoke tests in Task 10 need YOU in the room — they
     involve putting the HMD on. The subagent can implement Tasks 1-9
     and stop at Task 10 for human-driven validation.
   - `executing-plans` — execute inline, batch with checkpoints.
3. Each task in the plan is bite-sized (2-5 min) with exact code
   blocks. The first 7 tasks each end with a commit; the last 3 are
   cleanup/docs/smoke-test.

## Hard prerequisites for the smoke test (Task 10)

These are required for the script to actually work — not just to test
it. If any are missing, fix them before kicking off Task 10:

- HMD reachable over adb (verify with `adb devices`)
- `dotnet build -c Release src/WindowStream.Cli/` has been run at
  least once (produces the windowstream.exe at the expected path)
- FFmpeg DLLs are next to windowstream.exe (OBS install or manual
  drop; CLAUDE.md "Toolchain and runtime dependencies" has the list)
- The latency-clock browser open at
  `file:///C:/Users/mtsch/WindowStream/tools/latency-clock.html`
  in Edge (NOT Chrome --kiosk — known WGC conversion bug,
  see `project_chrome_kiosk_wgc_conversion_fail.md`)

## Design decisions worth remembering

The brainstorm settled four design points the implementer should NOT
re-litigate without cause:

- **Single combined script** (not orchestrator + recorder pair). User
  picked this to have one entry point and one source of truth.
- **HMD adb discovery via `adb devices` first** (which uses adb's
  built-in mDNS), then `adb kill-server; adb start-server` to kick
  it, then cached ip:port, then prompted ip:port. PowerShell's
  `Resolve-DnsName` does NOT do mDNS on stock Windows and is not in
  the design.
- **Browser launch stays manual.** User said "the browser always
  starts no problem; it's the window server attaching that fails."
- **Frame-flow probe is 4 seconds off-head, threshold dec ≥ 20.**
  Three branches: dec=enc=0 (WGC capture failed), dec=0 enc>0
  (network/firewall), 0<dec<20 (low-rate / unfocused source).

## Banner format gotcha (verified)

`serve` writes to **stdout**, not stderr:
```
windowstream: serving on TCP <port>, UDP <port>
```
Source: `src/WindowStream.Core/Hosting/CoordinatorLauncher.cs:154`.

FRAMECOUNT and other diagnostics go to **stderr**. The script
redirects both to separate temp files so the probe (Step 5) can grep
stderr for `stage=enc` lines and the banner parser (Step 4) can grep
stdout for the port. Don't merge the streams — splitting them is
load-bearing for the probe's diagnostic branches.

## Files this session created

- `docs/superpowers/plans/2026-05-11-cold-start-latency-test.md`
  (committed in `b44fb90`)
- `docs/superpowers/handoffs/2026-05-11-cold-start-latency-test-handoff.md`
  (this file)

No code was written this session. The spec
(`docs/superpowers/specs/2026-05-11-cold-start-latency-test-design.md`)
was created and then deleted per the writing-plans skill's lifecycle
— its content lives in the plan's header.

## Lifecycle reminder

The plan is **scaffolding**. When the implementation is complete and
the smoke tests pass, the plan gets deleted at branch-finish time —
any durable insight (e.g. the banner-stream split, the dec/enc
diagnostic table) folds into `CLAUDE.md` or inline comments in the
script itself.
