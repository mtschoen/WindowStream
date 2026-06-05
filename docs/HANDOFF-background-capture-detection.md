# Handoff - background/offscreen capture frame-starvation detection (DESIGN IN PROGRESS)

Status as of 2026-06-04: mid-brainstorm. Premises validated by a spike; the two-signal
detection model is essentially designed; **next step is to finalize the design and write
the spec** (then `writing-plans`). This is live in-progress work, not a completed-work
relic - resume here.

## What we're building

Production-side detection for when a capture **source window stops producing frames** -
because it is minimized, content-protected, or (the subtle one) a background app that
self-throttles its rendering. Motivated by the `WorkerEmitsChunksThroughPipe` failure
(fixed in commit 22ba4e9 with Edge anti-throttle flags) and the realization that the same
class of bug hits arbitrary production capture targets, where we cannot relaunch the app
with flags.

## Decisions locked during the brainstorm

- **v1 scope = detection + surface-only.** Emit a typed diagnostic when the source stalls;
  do NOT auto-manipulate the user's window (no auto-foreground / restore) in v1. (User
  picked "Surface-only" over auto-foreground / viewer-actions / full-auto-ladder.)
- **Detection-first, then validate mitigations.** The same detector is the instrument for
  later answering "is just-focus-the-window sufficient?" - so build the signal before
  building any mitigation ladder.
- **No state polling, no FPS thresholds** (user preference). Use event-driven signals.

## Spike ground truth (see ~/.claude/notes/spike_wgc-frame-delivery-map.md)

WGC frames delivered over 6s by window state, measured on this machine:

| State | frames/6s | meaning |
| --- | --- | --- |
| minimized (static AND animated) | **0** | clean "broken" signal - no DWM surface |
| idle/static (Notepad) | 1-2 | gets initial frame then quiet |
| offscreen (not minimized) | 1 | composes fine - NOT a failure mode |
| animated foreground | ~282 | healthy |
| animated occluded, recent (~6s) | ~295 | occlusion alone does NOT throttle |
| animated occluded, **aged 30s background** | **1** | throttle is TIME-PROGRESSIVE |
| animated aged 30s + anti-throttle flags | ~320 | flags defeat it |

Key takeaways:

- Minimized => 0 frames (deterministic). Idle => exactly 1-2. So "got >=1 frame" separates
  idle from broken; "0 frames ever" is unambiguous.
- The throttle case is **"1 frame then silence" - identical to idle by count.** Frame count
  alone cannot tell throttle-stall from idle.
- Chromium background throttling ramps with TIME backgrounded (validated the 22ba4e9 fix's
  root cause; the worker's `dotnet run` compile backgrounds Edge >30s before capture).

## Refined design (the two-signal model)

1. **Signal 1 - "never got frame 1":** `StartCapture` succeeded but no frame arrived within
   a one-shot startup grace (~1-2s). Catches minimized-at-start / content-protected /
   capture-creation-failed. Near-time-independent (single grace timer, not continuous
   sampling). This is the strong, clean signal.
2. **Signal 2 - "sharp cadence cliff":** frames were arriving at an established rate, then a
   gap many multiples of the interval. Catches throttle / mid-stream minimize. The spike
   proved the cliff is SHARP (53fps -> 0), not a gradual sag, so the threshold is forgiving
   and idle (never climbs to a cadence) cannot false-trigger it. This is the only
   irreducibly time-based piece.
3. **Enrichment (event-driven, no polling):** classify the cause with WinEvent hooks
   (`SetWinEventHook` EVENT_SYSTEM_MINIMIZESTART/MINIMIZEEND, EVENT_OBJECT_LOCATIONCHANGE,
   filtered to the source window's pid/thread, WINEVENT_OUTOFCONTEXT) plus
   `GraphicsCaptureItem.Closed` (documented-unreliable - signal, not sole source). Tells
   minimized vs focus-loss-throttle vs target-closed apart.
4. Emit as a typed `PipelineEvent` through the existing `Diagnostics` facade; the viewer
   surfaces it as a banner ("stream stalled: source not rendering"). Idle windows (1 frame
   then quiet) are correctly NOT flagged by either signal.

Dropped from scope (YAGNI): offscreen mitigation (it composes fine; it's actually a good
future *non-intrusive parking* strategy - offscreen-but-not-minimized keeps streaming).

## Open questions for the fresh session (resume here)

1. Event type design: one `SourceStalled` event with a cause enum, or distinct
   `SourceNeverStarted` / `SourceStalled`? Fields (cause, lastFrameAgeMs, windowState)?
   Where in the `PipelineEvent` closed DU?
2. Where the detector lives: a wrapper around the worker's `capture.Frames` loop in
   `WorkerCommandHandler` (src/WindowStream.Cli/Commands/WorkerCommandHandler.cs:93).
3. Startup-grace duration + cliff threshold constants (spike: idle first frame <60ms;
   healthy ~50fps; throttle cliff to 0). Pick defensible defaults.
4. WinEvent hook lifetime/threading (needs a message pump; out-of-context callback).
5. Viewer-side rendering of the new event (banner in which activities/flavors).
6. Validation harness: promote the spike probe (archived at
   `.claude/spikes/wgc-frame-delivery-map/FrameDeliveryProbe.cs.txt`) into a proper
   measurement tool? It already maps frames-by-state and could answer the focus-sufficiency
   question rigorously.
7. Then: finalize design -> write spec at `docs/superpowers/specs/2026-06-04-background-
   capture-detection-design.md` -> `writing-plans`.

## "Is just-focus-the-window sufficient?" - partial answer from the spike

- Focus/foreground DOES prevent the throttle case (a foreground window never throttled).
- But focus does NOT help a **minimized** window (0 frames regardless), and in production we
  cannot always foreground the source. So focus is necessary-sometimes, never sufficient -
  which is exactly why detection (this work) comes first and a mitigation ladder later.

## Reference pointers

- Test fix + root cause: commit 22ba4e9; `WorkerProcessIntegrationTests.cs` (Edge anti-
  throttle flag comment block).
- Spike: `~/.claude/notes/spike_wgc-frame-delivery-map.md` + scratch at
  `.claude/spikes/wgc-frame-delivery-map/` (gitignored).
- Pipeline/Diagnostics: AGENTS.md "Diagnostics" section; `WindowStream.Core/Observability/`.
- WGC capture path: `WgcCaptureSource` / `WgcFrameConverter` (src/WindowStream.Core/Capture/Windows/).
