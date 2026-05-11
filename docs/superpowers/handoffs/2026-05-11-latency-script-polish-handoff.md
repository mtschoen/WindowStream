# Handoff: two polish items on the cold-start latency script

**Date:** 2026-05-11
**For:** the fresh session that picks this up
**From:** the implementation + first-round-debugging session

## Status

`tools/record-latency-clock.ps1` and its wrapper `tools/latency-test.bat`
landed in this session (commits `f19656c..c2193a0` on `main`). The
end-to-end flow now works -- user confirmed a clean run after the
"keep viewer alive across the on-head gate" fix in `c2193a0`. Two
small polish items remain before the script is truly done, plus the
deferred Task 10 smoke test from the original plan.

The plan at `docs/superpowers/plans/2026-05-11-cold-start-latency-test.md`
is still in tree. Per the writing-plans / executing-plans lifecycle it
should be deleted at branch-finish time once these two polish items
land and the smoke-test memory is saved.

## Issue 1: Chrome window lingers after teardown

Step 8's teardown branch kills the server process and removes the
session firewall rules but does NOT close the Chrome `--kiosk` window
the script launched in Step 3. The user has to alt-F4 / taskkill it
manually.

**Fix:** call the existing `Stop-LatencyClockBrowsers` helper from
the `[Yy]` branch of teardown. That helper already filters by
CommandLine (`latency-clock.html`), not title, so it respects
`feedback_never_kill_by_window_title.md` and won't touch the user's
other Chrome / Edge tabs.

**Site:** the teardown block at the bottom of the script:

```powershell
if ($tearDown -match '^[Yy]') {
    Stop-Process -Id $ServerProcess.Id -Force -ErrorAction SilentlyContinue
    Get-NetFirewallRule -DisplayName 'WindowStream-Session-*' -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
    Ok "Server stopped, firewall rules removed."
}
```

becomes:

```powershell
if ($tearDown -match '^[Yy]') {
    Stop-Process -Id $ServerProcess.Id -Force -ErrorAction SilentlyContinue
    Get-NetFirewallRule -DisplayName 'WindowStream-Session-*' -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
    $reaped = Stop-LatencyClockBrowsers
    Ok "Server stopped, firewall rules removed, $reaped browser process(es) closed."
}
```

The `Stop-LatencyClockBrowsers` function is already defined in Step 3
(returns the kill count). No new logic needed.

## Issue 2: HMD has no signal when the recording is done

Right now `screenrecord --time-limit $Duration` blocks for the
configured 15s, the file lands on /sdcard, the script pulls it -- and
the HMD keeps showing the live clock the whole time. The user has no
way to tell, from inside the HMD, that the recording is locked in and
they can take it off.

**Fix:** force-stop the viewer immediately after `screenrecord`
returns. The recording is already saved on the device at that point,
so killing the viewer doesn't affect the file -- it just gives the
on-head user a "screen goes black" signal that means "done, take it
off". The subsequent `adb pull` and ffmpeg frame extraction proceed
normally because they don't depend on the viewer.

**Site:** Step 7, around the `screenrecord` call:

```powershell
Info "  recording NOW -- position both clocks in your gaze"
& adb -s $DeviceId shell screenrecord --time-limit $Duration --bit-rate 20M $RemoteMp4

& adb -s $DeviceId pull $RemoteMp4 $RecordingMp4 *> $null
```

becomes:

```powershell
Info "  recording NOW -- position both clocks in your gaze"
& adb -s $DeviceId shell screenrecord --time-limit $Duration --bit-rate 20M $RemoteMp4

# Recording is locked in on /sdcard. Force-stop the viewer to give the
# on-head user a black-screen "done" signal while we pull and process.
& adb -s $DeviceId shell am force-stop $ViewerPkg *> $null

& adb -s $DeviceId pull $RemoteMp4 $RecordingMp4 *> $null
```

This is the UX the claude-driven test had in one earlier iteration that
the user explicitly liked.

## Suggested commit shape

Both fixes are tiny and independent. Two commits is fine; one bundled
commit is also fine -- judgment call. Suggested messages:

- `tools(latency-test): close Chrome --kiosk on teardown`
- `tools(latency-test): force-stop viewer after record so HMD signals done`

## Verification

No HMD wear time needed for either fix:

- **Issue 1:** run `tools\latency-test`, complete the flow, answer
  `y` to teardown, verify the Chrome --kiosk window closes. (User can
  pre-stage a non-latency Chrome window with other tabs to verify
  those AREN'T touched -- the CommandLine filter should leave them
  alone, but worth a sanity check.)
- **Issue 2:** during the next real on-head run, confirm the HMD goes
  black exactly when the 15s screenrecord ends, before the script
  prints "Recording: ...mp4". User reports this as the desired UX.

## After these two fixes

Once both fixes are in and verified:

1. Run the deferred **Task 10** smoke test from the plan -- the
   cold-start happy path + the three failure-injection cases (no source
   window, low frame rate, HMD unreachable). The script has been
   end-to-end verified once, but Task 10 is the systematic sweep.
2. Save a memory `project_cold_start_latency_script.md` summarizing
   what the script does, what it auto-discovers, the WGC retry
   behavior, and the four failure-injection cases proven to work.
   Add a line to `MEMORY.md`. Update `project_cold_start_script_queued.md`
   to mark it done (or delete and replace with the new memory).
3. **Delete the plan** at
   `docs/superpowers/plans/2026-05-11-cold-start-latency-test.md` per
   the plan-as-scaffolding lifecycle.
4. **Delete this handoff** -- it's also scaffolding.

## Bug-fix breadcrumbs from this session (worth keeping)

These were learned-the-hard-way and the commit messages capture the
why, but flagging here so the next session doesn't re-step on them:

- **PS 5.1 `2>&1` on native exes is a foot-gun** -- combined with
  `$ErrorActionPreference = 'Stop'` it halts on benign adb info like
  `* daemon not running; starting now`. The script now uses
  `*> $null` for discard and `2>$null` for capture, and EAP is
  `Continue` globally. (Commit `803f853`; see
  `~/.claude/notes/idioms_powershell_native_exe_launchers.md`.)
- **Em-dashes inside double-quoted strings in a UTF-8-without-BOM
  `.ps1`** become `a-eur-"` under PS 5.1's cp1252 default, and the
  inline `"` terminates the string. The script no longer contains
  em-dashes for that reason. (Commit `43ad719`; see
  `~/.claude/notes/feedback_ps51_emdash_cp1252.md`.)
- **`>=` inside a double-quoted string** also confused the PS 5.1
  parser. Reworded as "at or above threshold N" in the success
  message. (Same commit `43ad719`.)
- **Browser fullscreen / `--kiosk` triggers the WGC frame-conversion
  bug** intermittently. Workaround is in-script: detect dec=0+enc=0
  signature, kill the browser by CommandLine, relaunch, retry. Up to
  3 attempts by default. (Commit `43ad719`; see
  `project_chrome_kiosk_wgc_conversion_fail.md` and
  `project_edge_kiosk_wgc_session_bust.md`.)
- **Force-stopping the viewer between probe and record re-attaches
  WGC** to the same source HWND, which can re-trigger the bust state
  and produce a black recording even after a healthy probe. The
  script now keeps the viewer alive across the on-head gate.
  (Commit `c2193a0`.)
