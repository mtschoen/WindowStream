# Handoff: OpenXR spatial window manager — HMD verification

**Branch:** `xr-spatial-window-manager`
**Built + unit-tested:** 2026-05-29 (overnight autonomous session)
**On-head verified:** ❌ not yet — this is the morning checklist.

## What was built

The gxr (Android XR / OpenXR) flavor's launcher (`MainActivity`) is now a
**spatial window manager** instead of a single-panel viewer:

- Launches into immersive full-space, auto-discovers the server.
- A persistent **drawer** panel lists the server's capturable windows; each row
  has an **Open** / **Close** button.
- Tapping **Open** spawns that window as its own movable `SpatialExternalSurface`
  panel. Several windows stream at once.
- Each panel has a **chrome bar** above it: **×** (close), **Minimize/Restore**,
  and **– / +** (resize = viewer-side scale).
- Minimize pauses the stream (`PAUSE_STREAM`) and shrinks the panel; restore
  resumes (`RESUME_STREAM`). Close sends `CLOSE_STREAM` and tears the panel down.
- Grab a panel's column to **move** it (`.movable()`).

## Commits on the branch

1. `fix(viewer): restore Android unit-test baseline broken by static Log call`
   — main's `testGxrDebugUnitTest` was red (14 failures) since `2b6b595`; CI
   never runs the viewer suite so it was unnoticed. See below.
2. `feat(xr): add spatial window manager state + layout logic (P1)` — tested.
3. `feat(xr): multi-panel spatial window manager with window chrome (P2/P3)`.
4. `docs(...)` — this handoff + AGENTS.md.

## ⚠ Pre-existing baseline bug found & fixed

`2b6b595` added `Log.isLoggable("FRAMECOUNT", …)` in `MediaCodecDecoder`'s
companion-object initializer. Any JVM unit test that loads that class hit
`ExceptionInInitializerError` ("android.util.Log not mocked"), failing all 14
`ViewerPipelineTest` cases. **CI (`.gitea/workflows/ci.yml`) only runs the
.NET tests — it never builds or tests the Gradle viewer**, so the viewer's
kover gate has been effectively unenforced. Fixed via
`testOptions.unitTests.isReturnDefaultValues = true` + a coverage test.
**Worth doing separately:** add an Android job to CI so the viewer is actually
gated (otherwise the next viewer regression slips through the same gap).

## Verify on-head (do these in order)

Pre-flight (off-head, per `feedback_preflight_before_hmd`):

1. Server up on the PC: `dotnet run --project src/WindowStream.Cli -f net8.0-windows10.0.19041.0 -- serve` — note LAN IP + TCP port. Network profile **Private**.
2. Have ≥2 windows with **active content** (terminal w/ spinner, video) and even dimensions. `… -- list` to get HWNDs.
3. Confirm adb targets the **GXR** (`R3GYB04E2WB`), not the Fold 3 (`RFCRB0G5DLW`) — see `project_gxr_serial_misattributed…`. `adb devices`.
4. Build + install: `./gradlew :app:assembleGxrDebug && adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/gxr/debug/app-gxr-debug.apk`
5. Keep screen awake: `adb shell svc power stayon true`. Block the proximity sensor / wear it (radio parks off-head).

On-head checks:

1. **Launch** — icon-tap *or* `adb shell am start -n com.mtschoen.windowstream.viewer/.app.MainActivity`. Confirm you land in immersive space (not a 2D window) and the **drawer** panel appears with the window list. *(If icon-tap still crashes, the adb intent is the fallback; note which.)*
2. **Spawn one** — tap **Open** on a window. A video panel with a chrome bar appears and streams. ✅ video visible, not black.
3. **Spawn a second** — Open another. Two panels stream concurrently, fanned left/right, not overlapping.
4. **Move** — grab a panel and reposition it. Both chrome + video move together (they share a `SpatialColumn`).
5. **Resize** — tap **+** / **–** on a panel. It scales up/down in steps; stays put otherwise.
6. **Minimize / Restore** — tap **Minimize**: panel shrinks, stream pauses (watch PC-side send counts drop / `wlan0` RX flatten for that stream). **Restore**: full size, stream resumes.
7. **Close** — tap **×**: panel disappears, stream stops; drawer row flips back to **Open**.
8. **Drawer Close** — Close a window from the drawer too; same teardown.

## Known risks / things most likely to need a fix on-head

- **`SpatialExternalSurface` inside a `SpatialColumn`** — compiles, but if the
  video won't render inside the column (vs. as a top-level Subspace child),
  fallback is a separate chrome `SpatialPanel` at a computed y-offset above each
  surface (no shared column). This is the #1 thing to watch.
- **Coordinate units** — reused the existing `meters * 1000f → dp` idiom and
  `PanelPlacement.DEFAULT_DISTANCE_METERS`; slot fan spacing is `1.4 m`. If
  panels render too close/far/overlapping, tune `SpatialPanelLayout
  .SLOT_SPACING_METERS` and the drawer offset.
- **Minimize keeps the surface mounted** (tiny) on purpose, to avoid
  decoder/surface-lifecycle churn. If a paused+shrunk surface misbehaves, the
  alternative is to unmount it and `setOutputSurface` on restore (more complex).
- **2D overlay occlusion** — the observability overlay is `GONE` by default so it
  shouldn't occlude (the old picker-handoff occlusion issue). If the spatial
  panels are still occluded by a 2D layer, check the `AndroidView` in
  `MainActivity` isn't drawing fullscreen.
- **Input on chrome buttons** — `SpatialExternalSurface` doesn't take input;
  chrome lives in a `SpatialPanel` which does. If buttons don't respond, verify
  the chrome panel (not the surface) is receiving the ray.

## If something needs changing

- Pure logic + placement: `viewer/.../xr/SpatialWindowManager.kt`,
  `SpatialPanelLayout.kt`, `SpatialPanelState.kt` (all unit-tested — keep the
  gate at 100%).
- Rendering: `viewer/.../xr/SpatialWindowManagerScene.kt` (coverage-excluded).
- Wiring/lifecycle: `viewer/.../app/MainActivity.kt` (coverage-excluded).
- Fast loop: `./gradlew :app:testGxrDebugUnitTest :app:koverVerifyGxrDebug`.
- APK: `./gradlew :app:assembleGxrDebug`.
