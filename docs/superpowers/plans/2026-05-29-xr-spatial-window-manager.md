# Plan: OpenXR spatial window manager (gxr flavor)

**Date:** 2026-05-29
**Branch:** `xr-spatial-window-manager`
**Author:** Claude (autonomous overnight session)

## Goal (user's ask, verbatim intent)

> "Launch into OpenXR, see a list of windows, and be able to spawn windows
> with chrome (close, minimize, resize affordances)."

The OpenXR build is the **gxr Gradle flavor** (Samsung Galaxy XR / Android XR
immersive spatial panels via Jetpack XR / `androidx.xr.compose`). Today it is
light on UI: the gxr launcher (`MainActivity`) connects, shows a 2D window
picker, then streams **one** window into **one** `SpatialExternalSurface`
panel (replace-on-pick). There is no way to have several windows open at once
and no per-window chrome.

## Where things stand before this work

- **Entry point:** gxr `MainActivity` IS the launcher (`FULL_SPACE_MANAGED`).
  The stale `alpha04` icon-crash memory no longer applies — dep is now
  `1.0.0-alpha13` and the picker→stream path runs end-to-end (per recent
  memories). So "can you get to OpenXR from the launcher" = yes, by installing
  the gxr flavor; this plan makes that landing screen a real window manager.
- **Protocol already supports multi-stream:** `MultiStreamControlConnection`
  exposes `openStream` (per-window `Flow<StreamLifecycleEvent>`),
  `closeStream`, `pauseStream`, `resumeStream`, `focusWindow`. The portable
  `UnifiedStreamingActivity` already drives N concurrent streams in 2D (tabs +
  drawer). We mirror that capability spatially.
- **Baseline was red** (fixed in commit 1 of this branch): `2b6b595` added a
  static `Log.isLoggable` call that broke 14 JVM unit tests; CI never runs the
  viewer suite so it went unnoticed. Fixed via `isReturnDefaultValues = true`
  + a coverage test.

## Jetpack XR alpha13 API facts (researched, not guessed)

- `SpatialExternalSurface` **does not capture input** → chrome buttons cannot
  live on the surface. Chrome must be a sibling `SpatialPanel` (which DOES
  capture input) or an `Orbiter`.
- `movable` modifier is public and works on `SpatialExternalSurface`. Resize
  via the `resizePolicy` parameter (native drag handles).
- `Orbiter` anchors to the nearest parent entity inside `SpatialBox`/`Spatial
  Row`/`SpatialColumn`; it cannot attach directly to a `SpatialExternalSurface`.
- Multiple panels compose via `SpatialRow` / `SpatialColumn` / `SpatialBox`.
- **Chosen chrome approach (lowest hardware-iteration risk):** wrap each
  window in a `SpatialColumn` — a chrome `SpatialPanel` (Material3 buttons)
  on top, the `SpatialExternalSurface` below — and make the whole column
  `movable()`. The column is a group entity, so move/place affects both
  together. No reliance on Orbiter-with-ExternalSurface (ambiguous in docs).

## Design

Keep the strict 100% line+branch coverage gate intact: **all logic goes in
plain testable classes; the Compose-XR rendering + Activity stay thin and are
Kover-excluded** (same pattern as `WindowPickerViewModel` tested vs.
`MainActivity`/`WindowStreamSceneKt` excluded).

### New testable types (100% covered by unit tests)

1. `xr/SpatialPanelState.kt` — immutable per-panel UI state:
   `windowId, streamId, title, contentWidthPx, contentHeightPx, minimized:
   Boolean, scale: Float`.
2. `xr/SpatialWindowManager.kt` — owns `StateFlow<List<SpatialPanelState>>`
   and a derived `StateFlow<Set<ULong>> openWindowIds`. Methods:
   `addPanel`, `removePanel(windowId)`, `toggleMinimize(windowId)`,
   `setScale`/`adjustScale(windowId, delta)` (clamped),
   `updateDimensions(windowId, w, h)`, `isOpen(windowId)`. Pure logic; no
   Android imports.
3. `xr/SpatialPanelLayout.kt` — pure placement math:
   `computeSlotOffsetMeters(index, count): Float` (horizontal fan so spawned
   panels don't overlap), `MIN_SCALE`/`MAX_SCALE`, `clampScale(value)`,
   `SCALE_STEP`.

### New XR rendering (Kover-excluded)

4. `xr/SpatialWindowManagerScene.kt` — `@Composable` rendering, from manager
   state: a persistent **drawer** `SpatialPanel` (window list, tap to
   spawn/close) + one `SpatialColumn` per open panel (chrome `SpatialPanel`
   with ×/–/＋/－ buttons over a `SpatialExternalSurface`), positioned by
   `SpatialPanelLayout`, `movable()`, surface `resizePolicy` enabled.
   Callbacks up to the activity: `onSpawn(windowId)`, `onClose(windowId)`,
   `onToggleMinimize(windowId)`, `onScale(windowId, delta)`,
   `onSurfaceProvided(windowId, Surface)`.

### Modified

5. `app/MainActivity.kt` (gxr launcher) — replace the single-panel `Streaming`
   state with the manager-driven scene. Own per-window **runtime** resources
   (`XrPanelSink`, `MediaCodecDecoder`, `UdpTransportReceiver`, pipeline scope)
   in a `Map<ULong, …>`. Spawn on drawer tap (`openStream` flow); teardown on
   close. **Minimize → `pauseStream`; restore → `resumeStream`** (saves
   bandwidth). Keep the observability overlay (toggled, as an Orbiter/side
   panel so it does not occlude — addresses the picker-handoff occlusion note).
6. `app/build.gradle.kts` — add Kover exclusions for the new XR scene class(es)
   + any new `ComposableSingletons$…`. Stay on `alpha13` (no dep bump in an
   unattended session).
7. `AGENTS.md` — document the spatial window manager + how to launch it.

## Build / verify sequence (each phase: green before commit)

- `./gradlew :app:testGxrDebugUnitTest :app:koverVerifyGxrDebug` — logic +
  gate (fast JVM loop; **the** signal I can run unattended).
- `./gradlew :app:assembleGxrDebug` — proves the Compose-XR scene + Activity
  compile against the real Jetpack XR API (catches API guesses).
- **Cannot verify on HMD tonight** (user asleep). Hardware verification is the
  morning handoff: install `app-gxr-debug.apk`, confirm launch → drawer →
  spawn 2+ panels → move/close/minimize/resize.

## Phased execution

- **P0 ✅** Fix red baseline (committed).
- **P1** TDD the logic: `SpatialPanelState`, `SpatialWindowManager`,
  `SpatialPanelLayout` + full unit tests. Gate green.
- **P2** XR scene composable (`SpatialWindowManagerScene`) + chrome; add Kover
  exclusions; `assembleGxrDebug` compiles.
- **P3** Wire into `MainActivity`: per-window runtime resource map, spawn/close
  /minimize(pause)/restore(resume)/resize; persistent drawer; non-occluding
  overlay. `assembleGxrDebug` green.
- **P4** Docs (AGENTS.md), final gate + assemble, commit, write HMD handoff.

## Risks / open questions for the morning

- **Coordinate units:** existing code uses the `(meters * 1000f).dp` idiom for
  `SpatialExternalSurface` sizing — reused verbatim (known-good on HMD). Slot
  offsets use the same convention.
- **`SpatialColumn` + `SpatialExternalSurface` child:** expected to work
  (spatial layouts contain spatial composables) but unverified on HMD. If the
  surface won't render inside a column, fallback = separate chrome panel at a
  computed y-offset above each surface (documented in handoff).
- **Resize semantics:** "resize" implemented as panel **scale** (viewer-side),
  not resizing the source Windows window (would need new protocol messages).
  This is the natural spatial-window-manager meaning; note for user sign-off.
- **Movable group vs. per-panel offset:** initial fan layout + per-column
  `movable()` lets the user rearrange; the layout helper only sets the
  *initial* slot.
