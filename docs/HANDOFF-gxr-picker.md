# Handoff: GXR window picker — frames decode but spatial panel shows nothing

**Status (2026-05-17, end of session):** picker UI works on-head, server-side stream open succeeds, the MediaCodec decoder is running and configured against the SpatialExternalSurface, but the user reports they see no video in the spatial panel.

The earlier deadlock and the stream-refusal handling are both fixed. The remaining issue is purely "frames are flowing into the surface but the spatial panel either isn't visible or isn't rendering them."

---

## What was built this session

`viewer/WindowStreamViewer/app/src/main/.../app/MainActivity.kt` was rewritten from the auto-discover-and-try-each-window prototype into a small state machine:

- `Connecting` → mDNS discover (or honor `--es streamHost` / `--ei streamPort`) and open a `MultiStreamControlConnection`.
- `Picking(viewModel)` → renders the existing portable-flavor `WindowPickerScreen` as the activity's 2D Compose content, fed by `WindowPickerViewModel` so live `WINDOW_ADDED/REMOVED/UPDATED` events keep the catalogue current.
- `Streaming(sink)` → renders a 2D `Surface` containing a "Pick another window" button *plus* a `Subspace { SpatialExternalSurface { ... } }`, with its own `XrPanelSink` driving the spatial surface.
- `Failed(message)` → simple error screen (currently only reachable via startup-connect failure; stream-refusal now pops back to the picker with a banner).

`viewer/WindowStreamViewer/app/build.gradle.kts` got an extra kover-excludes entry for the Compose-generated `ComposableSingletons$MainActivityKt` peer holder.

`viewer/WindowStreamViewer/app/src/main/.../xr/XrPanelSink.kt` was switched from a RENDEZVOUS channel to `Channel.CONFLATED` + `trySend` so `provideSurfaceFromXrSystem` can never block the main thread. Unit tests still pass.

---

## What works (confirmed in logcat)

From the most recent successful-pipeline run (pid 23242, 2026-05-17 00:24):

```
00:24:18.415 XrMain: connecting to CHONKERS at 192.168.50.75:53349
00:24:18.480 XrMain: connected: 9 window(s) advertised
00:24:28.825 XrMain: opening stream for windowId=106
00:24:29.104 SplitEngineSubspaceManager: Creating Subspace with ID 1
00:24:29.174 XrMain: SpatialExternalSurface created: Surface(name=)/@0x1903ad3
00:24:29.263 XrMain: stream opened: streamId=1 3840x1050
00:24:29.265 XrMain: UDP bound on port 39178
00:24:29.384 MediaCodecDecoder: Found explicit low_latency codec variant: c2.qti.avc.decoder.low_latency
00:24:29.431 SurfaceUtils: connecting to surface 0xb400007e3ec70540, reason connectToSurface
00:24:29.519 XrMain: decoder started, rendering through XR compositor
00:24:30.375 MediaCodecDecoder: onOutputFormatChanged: width=3840 height=1056 frame-rate=30
```

So:
- Connection + handshake works.
- Picker displays the live catalogue and the user can tap a window.
- `MultiStreamControlConnection.openStream` returns `Opened` correctly.
- UDP receiver binds.
- Decoder finds `c2.qti.avc.decoder.low_latency`, connects to the spatial surface, configures, and starts.
- `onOutputFormatChanged` fires — frames are decoding.

Despite all of that, the user says "nothing." So frames are being submitted to MediaCodec, the decoder is alive, but visually the spatial panel is not showing video.

---

## The two earlier failure modes and their fixes (so the next session doesn't re-debug them)

### A. Main-thread deadlock at stream-open time
Original symptom: pipeline got as far as "opening stream for windowId=X", then nothing until ANR/SIGQUIT.

Root cause: `MultiStreamControlConnection.openStream(windowId, scope)` launches its mailbox-await coroutine on the scope you pass in. We were passing `lifecycleScope`, whose default dispatcher is `Main`. Meanwhile `XrPanelSink.provideSurfaceFromXrSystem` was doing `runBlocking { pendingSurfaceChannel.send(surface) }` on the main thread when `SpatialExternalSurface` finished initializing. That blocked the main thread waiting for someone to call `acquireSurface`. `acquireSurface` would have been called by `decoder.start()`, but that couldn't happen because `openStream` was sitting on the (blocked) main thread waiting for `STREAM_STARTED`. Classic.

Fix:
- `MainActivity` now uses a private `activityScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)` for everything streaming/picker-related — same pattern as `UnifiedStreamingActivity`. `lifecycleScope` import was removed.
- `XrPanelSink` switched to `Channel.CONFLATED` + `trySend` as defence-in-depth so this can't happen to any future caller either.

### B. Refused-stream ANR
Original symptom: server refused with `WINDOW_NOT_FOUND — window 141 encoder options unavailable` (the same "this window can't be captured" pattern documented for Discord/Firefox/Chrome-kiosk). The activity then ANR'd.

Root cause: the Refused handler set `uiState = UiState.Failed(...)`, but by that time `SpatialExternalSurface` had already fired `onSurfaceCreated` and was sitting in `runBlocking.send` on the rendezvous channel. Same shape as bug A.

Fix:
- Conflated channel from above eliminated the blocking-send.
- Refused now sets `pickerErrorBanner` and calls `showPicker(liveConnection)` to return the user to the picker so they can choose a different window.

---

## The current open issue — "frames decode but spatial panel shows nothing"

This is the symptom that remains as of session end. Pipeline runs to completion, decoder confirms frames, but the user reports no visible video.

### Strongest hypothesis: the 2D Compose `Surface` overlay occludes the spatial panel

In `StreamingScene` I render BOTH a 2D `Surface(...)` (containing the "Pick another window" button) AND `Subspace { SpatialExternalSurface { ... } }` inside the same `setContent` call. In Jetpack XR full-space mode, the activity's 2D Compose content becomes the **main panel** — a flat panel anchored in space — and the Subspace becomes additional spatial entities.

The 2D Surface uses `color = MaterialTheme.colorScheme.background` which is opaque dark gray, and the inner Box has `Modifier.fillMaxSize()`. So the entire main panel surface is opaque.

If the main panel and the spatial panel happen to coincide spatially (both end up roughly in front of the user at the default activity position), the opaque main panel could be hiding the spatial panel. The original prototype `MainActivity` and `XrDemoActivity` both work because they have **only** the Subspace inside `setContent` — no 2D Compose content at all, so no main panel.

### Things to check first in the next session

1. **Remove the 2D Surface entirely** and replace the "Pick another window" affordance with a spatial primitive (`SpatialPanel` inside the same `Subspace`, or just rely on hardware back to exit the activity). If the video appears as soon as the 2D Surface goes away, that confirms occlusion.

2. **Use a transparent / `Color.Transparent` background** on the 2D Surface and see if the spatial panel becomes visible through it. Jetpack XR may still render the main panel chrome but at least content underneath could show.

3. **Render the picker as a spatial panel too** (`SpatialPanel` inside `Subspace`) instead of as 2D main-panel content. Then there's only spatial content and no main panel, matching the working `XrDemoActivity` pattern. The Compose tree you pass to `SpatialPanel` is regular 2D Compose, so `WindowPickerScreen` would drop in unchanged.

4. **Sanity-check frame delivery**: add a periodic log of `framesRendered` from `XrPanelSink` in `StreamingScene`. If it's incrementing, frames really are being submitted to the surface and the issue is purely visual occlusion. If it stays at 0, the decoder thinks it's drawing but `releaseOutputBuffer` is failing or going to the wrong surface.

5. **Look at the head-pose / origin**: Galaxy XR full-space mode may place the user origin somewhere unexpected. The spatial panel is at `offset(z = -PanelPlacement.DEFAULT_DISTANCE_METERS)` which is -1.5m. If the user is positioned facing away from the origin, the panel could be behind them. The `.movable()` modifier should let the user grab and reposition it once they find it.

### Lower-probability hypotheses worth ruling out if (1)-(5) don't pan out

6. **Surface generation mismatch** — the logs show `setOutputSurface -- failed to set consumer usage (6/BAD_INDEX)` followed by `Surface configure completed`. The BAD_INDEX is suspicious but seems to be tolerated downstream (decoder reaches `onOutputFormatChanged`). Worth comparing to `XrDemoActivity`'s logs to see if they show the same warning.

7. **Recomposition replacing the surface mid-stream** — when state transitions Picking → Streaming, Compose builds the Subspace fresh. If anything triggers recomposition during streaming, `onSurfaceDestroyed` could fire and the decoder would lose its surface. No `onSurfaceDestroyed` log was seen in the successful-pipeline run, so this is unlikely the active cause, but watch for it during follow-up tests.

8. **Decoder writing frames before the surface is fully attached** — `XrPanelSink` now uses CONFLATED with `trySend`, which means if `provideSurfaceFromXrSystem` runs and no one is listening yet, the surface sits in the buffer. Then `acquireSurface` pulls it. There's a small window where the decoder could acquire a stale surface or one that the XR system has not yet completed initializing. Unlikely given the timing (surface created 89ms before decoder started, plenty of time for the XR system to be ready), but worth a try with a small `delay()` before `decoder.start` if the simpler fixes don't work.

---

## Files touched this session

- `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/MainActivity.kt` — full rewrite
- `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/xr/XrPanelSink.kt` — rendezvous → conflated channel
- `viewer/WindowStreamViewer/app/build.gradle.kts` — added `ComposableSingletons$MainActivityKt[$*]` to kover excludes

Coverage gate (`:app:koverVerifyGxrDebug`) and unit tests (`:app:testGxrDebugUnitTest`, `:app:testPortableDebugUnitTest`) all pass. `:app:lintGxrDebug` fails on an `IntentFilterExportedReceiver` warning for the gxr `MainActivity` declaration, but that fails on clean `main` too (verified via `git stash`) — pre-existing, unrelated, and trivially fixed with `android:exported="true"` in `app/src/gxr/AndroidManifest.xml`.

## How to reproduce / verify

Plug GXR (or `adb connect <ip>:5555` over Wi-Fi), then:

```
cd viewer/WindowStreamViewer
./gradlew.bat :app:installGxrDebug
```

Launch from the spatial home, wait for the picker, tap a window that's known to be capturable (Terminal, Fork, Chrome non-kiosk, Unity Editor). Watch logcat:

```
adb logcat -d | grep -E 'XrMain|MediaCodec|stream refused|FATAL'
```

If you see `decoder started, rendering through XR compositor` followed by `onOutputFormatChanged`, the pipeline succeeded — the remaining work is visibility/placement of the spatial panel.
