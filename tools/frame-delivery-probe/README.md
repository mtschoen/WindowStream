# WGC Frame-Delivery Probe

Standalone measurement tool for WGC (Windows.Graphics.Capture) frame delivery
behavior across window states. Validates the premises behind the
`SourceFrameMonitor` stall-detection thresholds.

## What it measures

For a target window (by title substring or HWND), captures WGC frames for a
fixed duration and records:

- Total frames delivered
- Time to first frame (ms)
- Window state at capture time (normal / minimized / offscreen)
- Any exceptions

## Usage

Single probe:

```bash
dotnet run --project tools/frame-delivery-probe -- --title "Notepad" --action none --seconds 6
dotnet run --project tools/frame-delivery-probe -- --hwnd 12345 --action minimize --seconds 6 --output results.tsv
```

Arguments:

- `--title <substring>` or `--hwnd <handle>` (one required): target window
- `--action none|minimize|offscreen` (default `none`): window state to apply before capture
- `--seconds <n>` (default `6`): capture duration
- `--label <name>` (default: title or hwnd): label for the TSV output row
- `--output <path>` (default `probe-results.tsv`): append-mode TSV output file

## Harness scripts

**`run-probe-matrix.ps1`** drives a full matrix of window states (animated
foreground, animated occluded, static normal, static offscreen, animated
minimized, static minimized) using the latency clock + Notepad:

```powershell
pwsh tools/frame-delivery-probe/run-probe-matrix.ps1
```

**`run-throttle-aging.ps1`** tests whether Chromium background throttling is
time-progressive by aging a backgrounded Edge window before capturing, with
and without the anti-throttle flags:

```powershell
pwsh tools/frame-delivery-probe/run-throttle-aging.ps1 -AgeSeconds 30
```

## Expected results

| State | Frames/6s | Meaning |
| --- | --- | --- |
| animated foreground | ~280+ | healthy |
| animated occluded (recent) | ~280+ | occlusion alone does NOT throttle |
| animated occluded (aged 30s) | 1 | Chromium time-progressive throttle |
| animated aged 30s + flags | ~300+ | anti-throttle flags defeat it |
| static (Notepad, normal) | 1-2 | idle window, not a failure |
| static offscreen | 1 | composes fine |
| minimized (any) | 0 | no DWM surface |

These results validate the `SourceFrameMonitor` design: "never got frame 1"
catches minimized; "cadence cliff" catches the throttle case; idle windows
(which never establish a cadence) correctly do not false-trigger.
