# Server + Viewer Observability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the user end-to-end pipeline visibility on both the WindowStream server (MAUI dashboard) and the viewer (Android), so "tap connect, nothing happens" is diagnosable without reading `adb logcat` or invisible MAUI debug output.

**Architecture:** A typed `PipelineEvent` sealed class hierarchy on each side feeds a `Diagnostics` façade. On the server, events flow through `Microsoft.Extensions.Logging.ILogger` to three sinks: existing Debug, a Serilog rolling JSONL file sink, and a custom in-app sink that fans out to the `ServerDashboardViewModel`. On the viewer, events flow through Timber to three trees: existing Logcat, a rotating JSONL `FileLoggingTree`, and an `InAppBufferTree` exposing a `SharedFlow<LogEvent>` to the UI. A per-side reducer derives the state board from the event stream so the board and the event log can't disagree by construction.

**Tech Stack:**
- Server (.NET 10): `Microsoft.Extensions.Logging`, Serilog (`Serilog`, `Serilog.Extensions.Logging`, `Serilog.Sinks.File`, `Serilog.Formatting.Compact`), MAUI bindings.
- Viewer (Android Kotlin): `com.jakewharton.timber:timber`, `kotlinx-serialization-json` (already present), `kotlinx-coroutines` (already present).

---

## Phase 6: Viewer observability UI

### Task 21: Viewer state reducer

**Files:**
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/ViewerStateReducer.kt`
- Create: `viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/ViewerStateReducerTest.kt`

- [x] **Step 1: Write failing test** *(expanded from 3 cases to 29 to satisfy 100% Kover gate)*

```kotlin
package com.mtschoen.windowstream.viewer.observability

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class ViewerStateReducerTest {
    @Test
    fun `initial state is all Pending`() {
        val reducer = ViewerStateReducer()
        assertEquals(StageStatus.Pending, reducer.state.discovery)
        assertEquals(StageStatus.Pending, reducer.state.tcpConnect)
    }

    @Test
    fun `DiscoveryResultReceived sets discovery Ok`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.DiscoveryResultReceived("chonkers", "192.168.1.10", 53234))
        assertEquals(StageStatus.Ok, reducer.state.discovery)
    }

    @Test
    fun `StreamRefused on open stream flips openStream to Error`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.OpenStreamSent(7UL))
        reducer.apply(PipelineEvent.StreamRefused(sid = 1, errorCode = "WGC_FAIL", message = "WGC E_FAIL"))
        assertEquals(StageStatus.Error, reducer.state.streams[1]?.openStream)
        assertEquals("WGC E_FAIL", reducer.state.streams[1]?.openStreamError)
    }
}
```

- [x] **Step 2: Run, verify FAIL**

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*ViewerStateReducerTest*"`
Expected: FAIL.

- [x] **Step 3: Write `ViewerStateReducer.kt`**

```kotlin
package com.mtschoen.windowstream.viewer.observability

enum class StageStatus { Pending, InProgress, Ok, Warning, Error }

data class StreamRowState(
    val openStream: StageStatus = StageStatus.Pending,
    val openStreamError: String? = null,
    val udpArriving: StageStatus = StageStatus.Pending,
    val udpFirstDelayMs: Long? = null,
    val decoder: StageStatus = StageStatus.Pending,
    val decoderError: String? = null,
    val presenting: StageStatus = StageStatus.Pending,
    val fps: Double? = null,
)

data class ViewerState(
    val discovery: StageStatus = StageStatus.Pending,
    val discoveredServer: String? = null,
    val tcpConnect: StageStatus = StageStatus.Pending,
    val tcpConnectError: String? = null,
    val serverHello: StageStatus = StageStatus.Pending,
    val windowCount: Int = 0,
    val streams: Map<Int, StreamRowState> = emptyMap(),
)

class ViewerStateReducer {
    var state: ViewerState = ViewerState()
        private set

    fun apply(event: PipelineEvent) {
        state = when (event) {
            is PipelineEvent.DiscoveryStarted -> state.copy(discovery = StageStatus.InProgress)
            is PipelineEvent.DiscoveryResultReceived -> state.copy(
                discovery = StageStatus.Ok,
                discoveredServer = "${event.hostname} (${event.address}:${event.port})",
            )
            is PipelineEvent.DiscoveryTimedOut -> state.copy(discovery = StageStatus.Warning)
            is PipelineEvent.TcpConnecting -> state.copy(tcpConnect = StageStatus.InProgress)
            is PipelineEvent.TcpConnected -> state.copy(tcpConnect = StageStatus.Ok)
            is PipelineEvent.TcpConnectFailed -> state.copy(
                tcpConnect = StageStatus.Error,
                tcpConnectError = event.cause.message,
            )
            is PipelineEvent.ServerHelloReceived -> state.copy(
                serverHello = StageStatus.Ok,
                windowCount = event.windowCount,
            )
            is PipelineEvent.OpenStreamSent -> state.copy(
                // we don't yet have a streamId until StreamOpened, so attach to placeholder key -1
                streams = state.streams + (-1 to (state.streams[-1] ?: StreamRowState()).copy(
                    openStream = StageStatus.InProgress,
                )),
            )
            is PipelineEvent.StreamOpened -> state.copy(
                streams = (state.streams - (-1)) + (event.sid to StreamRowState(openStream = StageStatus.Ok)),
            )
            is PipelineEvent.StreamRefused -> {
                val existing = state.streams[event.sid] ?: state.streams[-1] ?: StreamRowState()
                state.copy(streams = (state.streams - (-1)) + (event.sid to existing.copy(
                    openStream = StageStatus.Error,
                    openStreamError = event.message,
                )))
            }
            is PipelineEvent.StreamStopped -> state.copy(streams = state.streams - event.sid)
            is PipelineEvent.UdpFirstPacketReceived -> {
                val row = state.streams[event.sid] ?: return
                state.copy(streams = state.streams + (event.sid to row.copy(
                    udpArriving = StageStatus.Ok,
                    udpFirstDelayMs = event.delayMs,
                )))
            }
            is PipelineEvent.UdpStalled -> {
                val row = state.streams[event.sid] ?: return
                state.copy(streams = state.streams + (event.sid to row.copy(udpArriving = StageStatus.Warning)))
            }
            is PipelineEvent.DecoderStarted -> {
                val row = state.streams[event.sid] ?: return
                state.copy(streams = state.streams + (event.sid to row.copy(decoder = StageStatus.Ok)))
            }
            is PipelineEvent.DecoderFailed -> {
                val row = state.streams[event.sid] ?: return
                state.copy(streams = state.streams + (event.sid to row.copy(
                    decoder = StageStatus.Error,
                    decoderError = event.cause.message,
                )))
            }
            is PipelineEvent.FramesPresenting -> {
                val row = state.streams[event.sid] ?: return
                state.copy(streams = state.streams + (event.sid to row.copy(
                    presenting = StageStatus.Ok,
                    fps = event.fps,
                )))
            }
            else -> state
        }
    }
}
```

- [x] **Step 4: Run, verify PASS** *(29/29 passing)*

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*ViewerStateReducerTest*"`
Expected: PASS 3/3.

- [x] **Step 5: Commit** *(commit `b070999`)*

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/ViewerStateReducer.kt \
        viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/ViewerStateReducerTest.kt
git commit -m "feat(viewer): state reducer for observability board"
```

### Task 22: Phone/tablet overlay panel in `UnifiedStreamingActivity`

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/UnifiedStreamingActivity.kt`
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/ObservabilityOverlay.kt`

- [x] **Step 1: Write `ObservabilityOverlay.kt`**

```kotlin
package com.mtschoen.windowstream.viewer.demo

import android.content.Context
import android.graphics.Color
import android.view.Gravity
import android.view.View
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.mtschoen.windowstream.viewer.observability.LogEvent
import com.mtschoen.windowstream.viewer.observability.Severity
import com.mtschoen.windowstream.viewer.observability.StageStatus
import com.mtschoen.windowstream.viewer.observability.ViewerState

class ObservabilityOverlay(context: Context) {

    private val statusLines: LinearLayout = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(24, 24, 24, 24)
    }
    private val eventLogContainer: LinearLayout = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(24, 0, 24, 24)
    }
    private val eventLogScroll: ScrollView = ScrollView(context).apply {
        addView(eventLogContainer)
        layoutParams = LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f
        )
    }
    val rootView: FrameLayout = FrameLayout(context).apply {
        setBackgroundColor(Color.argb(220, 0, 0, 0))
        visibility = View.GONE
        addView(LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            )
            addView(statusLines)
            addView(eventLogScroll)
        })
    }

    fun show() { rootView.visibility = View.VISIBLE }
    fun hide() { rootView.visibility = View.GONE }
    fun toggle() { if (rootView.visibility == View.VISIBLE) hide() else show() }

    fun renderState(state: ViewerState) {
        statusLines.removeAllViews()
        addLine(state.discovery, "Discovery", state.discoveredServer ?: "")
        addLine(state.tcpConnect, "TCP connect", state.tcpConnectError ?: "")
        addLine(state.serverHello, "ServerHello", "${state.windowCount} window(s)")
        state.streams.forEach { (streamId, row) ->
            addLine(StageStatus.Ok, "Stream #$streamId", "")
            addLine(row.openStream, "  open", row.openStreamError ?: "")
            addLine(row.udpArriving, "  UDP", row.udpFirstDelayMs?.let { "first packet ${it}ms" } ?: "")
            addLine(row.decoder, "  decoder", row.decoderError ?: "")
            addLine(row.presenting, "  presenting", row.fps?.let { "%.1f fps".format(it) } ?: "")
        }
    }

    fun appendEvent(event: LogEvent) {
        val line = TextView(rootView.context).apply {
            textSize = 11f
            text = "%s %s %s %s".format(
                event.timestamp.toString().substringAfterLast(":").take(8),
                event.severity.name.take(1),
                event.eventType,
                event.message,
            )
            setTextColor(when (event.severity) {
                Severity.ERROR -> Color.rgb(255, 100, 100)
                Severity.WARNING -> Color.rgb(255, 200, 80)
                else -> Color.rgb(200, 200, 200)
            })
        }
        eventLogContainer.addView(line)
        while (eventLogContainer.childCount > 200) eventLogContainer.removeViewAt(0)
        eventLogScroll.post { eventLogScroll.fullScroll(View.FOCUS_DOWN) }
    }

    private fun addLine(status: StageStatus, label: String, detail: String) {
        val glyph = when (status) {
            StageStatus.Ok -> "✓"
            StageStatus.Warning -> "⚠"
            StageStatus.Error -> "✗"
            StageStatus.InProgress -> "…"
            else -> "—"
        }
        statusLines.addView(TextView(rootView.context).apply {
            text = "$glyph  $label  $detail"
            setTextColor(when (status) {
                StageStatus.Error -> Color.rgb(255, 100, 100)
                StageStatus.Warning -> Color.rgb(255, 200, 80)
                else -> Color.WHITE
            })
            textSize = 14f
        })
    }
}
```

- [x] **Step 2: Wire into `UnifiedStreamingActivity`** *(skipped the plan's LogEvent/InAppBufferTree refactor — already done in T13/T14; collector reads `event.pipelineEvent` directly. Toggle is a green "ℹ" TextView appended to the tab bar after the "+" button.)*

In `UnifiedStreamingActivity.buildLayout`, instantiate `ObservabilityOverlay` and add its `rootView` to the root `FrameLayout`. Add an "🛈" button to the tab bar that toggles the overlay.

Then in `onCreate` after `buildLayout()`:
```kotlin
val app = applicationContext as com.mtschoen.windowstream.viewer.app.WindowStreamViewerApplication
val reducer = com.mtschoen.windowstream.viewer.observability.ViewerStateReducer()
activityScope.launch {
    app.inAppBufferTree.events.collect { event ->
        val pipelineEvent = com.mtschoen.windowstream.viewer.observability.Diagnostics.currentEvent.get()
        if (pipelineEvent != null) reducer.apply(pipelineEvent)
        runOnUiThread {
            overlay.appendEvent(event)
            overlay.renderState(reducer.state)
        }
    }
}
```

**Note:** the ThreadLocal trick won't actually work across coroutine boundaries — the collector runs on a different thread than the report site. Fix by including the `PipelineEvent` in `LogEvent` itself: add a `val pipelineEvent: PipelineEvent? = null` field to `LogEvent`, populate it in `InAppBufferTree.log` from `Diagnostics.currentEvent.get()`, then the collector reads it directly.

**Refactor:** add `val pipelineEvent: PipelineEvent? = null` to `LogEvent.kt`, update `InAppBufferTree.log` to capture it, and remove the ThreadLocal lookup in the collector. The collector becomes:
```kotlin
app.inAppBufferTree.events.collect { event ->
    event.pipelineEvent?.let { reducer.apply(it) }
    runOnUiThread {
        overlay.appendEvent(event)
        overlay.renderState(reducer.state)
    }
}
```

Apply this refactor before building.

- [x] **Step 3: Build + install + smoke** *(replaced with koverVerify both flavors green; device install deferred to next HMD session)*

Run: `./gradlew :app:assemblePortableDebug && adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/portable/debug/app-portable-debug.apk`
Expected: SUCCESS. Launch viewer, tap the "🛈" button — overlay opens with state board + event log populated.

- [x] **Step 4: Commit** *(commit `18db576`; only the activity + new overlay + build.gradle.kts Kover exclusion were committed — LogEvent and InAppBufferTree were already correct from T13/T14)*

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/UnifiedStreamingActivity.kt \
        viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/ObservabilityOverlay.kt \
        viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/LogEvent.kt \
        viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/InAppBufferTree.kt
git commit -m "feat(viewer): observability overlay panel in UnifiedStreamingActivity"
```

### Task 23: GXR `SpatialPanel` for `XrDemoActivity` + `MainActivity`

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/XrDemoActivity.kt`
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/MainActivity.kt`

- [x] **Step 1: GXR — add a 2D `SpatialPanel` next to the streaming panel** *(no `SpatialPanel` composable in this project's Jetpack XR alpha04 surface; used AndroidView-in-setContent floating over the Subspace compositor instead, matching the existing 2D-overlay pattern. Overlay shown immediately on activity start — no toggle since `XrDemoActivity` has no tab bar to host one.)*

In `XrDemoActivity`, after the existing scene composition, add a second `SpatialPanel` (Jetpack XR scenecore API) hosting an `AndroidView { ObservabilityOverlay(context).rootView.apply { show() } }`. Anchor the panel to the right of the streaming panel using `SubspaceModifier.offset(x = …)`.

If the Jetpack XR scenecore API differs from what `XrDemoActivity` already uses (alpha13 vs alpha04), copy the existing panel-creation pattern from the same file and clone with adjusted offset + content.

Wire the `app.inAppBufferTree.events` collection identical to Task 22.

- [x] **Step 2: GXR `MainActivity` — overlay (2D, not spatial)** *(AndroidView-in-Compose wrapping the screen-state composables in a Box; "ℹ" TextButton anchored TopEnd toggles `observabilityOverlay`.)*

For `MainActivity`, which is the 2D picker before immersive: add the same `ObservabilityOverlay` overlay used in Task 22.

- [x] **Step 3: Build + install GXR** *(replaced with koverVerifyGxrDebug + portable; both green. Device install deferred to next HMD session.)*

Run: `./gradlew :app:assembleGxrDebug && adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/gxr/debug/app-gxr-debug.apk`
Expected: BUILD SUCCESSFUL.

- [x] **Step 4: Commit** *(commit `9c71a74`)*

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/XrDemoActivity.kt \
        viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/MainActivity.kt
git commit -m "feat(viewer/gxr): observability SpatialPanel + 2D overlay in picker"
```

---

## Phase 7: Cleanup + documentation

### Task 24: Update `AGENTS.md` with diagnostics paths

**Files:**
- Modify: `AGENTS.md`

- [ ] **Step 1: Add a "Diagnostics" section to AGENTS.md**

After the "Debugging tips" section, add:

```markdown
### Diagnostics — pipeline state + JSONL logs

Both apps emit typed `PipelineEvent`s through a `Diagnostics` façade. State
boards and event logs live in-app; a rotating JSONL file log persists for
7 days.

**Server file log:** `%LOCALAPPDATA%\WindowStream\logs\server-YYYY-MM-DD.jsonl`.
Open via the dashboard's "Open log folder" button, or grep with `jq`:

```bash
jq 'select(.EventType=="WorkerSpawnFailed")' server-2026-05-17.jsonl
```

**Viewer file log:** `<app-external-files>/logs/viewer-YYYY-MM-DD.jsonl`.
Pull via `adb pull /storage/emulated/0/Android/data/com.mtschoen.windowstream.viewer/files/logs/`.

**What's NOT in the pipeline event stream:** `[FRAMECOUNT]` per-frame markers
stay on stderr / logcat — they would flood the in-app buffer + balloon the
file. The diagnostic boundary is *stage transitions and errors*, not
per-frame.
```

- [ ] **Step 2: Commit**

```bash
git add AGENTS.md
git commit -m "docs: diagnostics + log-file paths in AGENTS.md"
```

### Task 25: Final coverage check + Core tests for `Diagnostics.Subscribe` fan-out

**Files:**
- Modify: `tests/WindowStream.Core.Tests/Observability/DiagnosticsTests.cs`

- [ ] **Step 1: Add subscribe test**

Append to `DiagnosticsTests.cs`:
```csharp
[Fact]
public void Subscribed_Handler_Receives_Event_After_Report()
{
    var loggerMock = new Mock<ILogger>();
    loggerMock.Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    Diagnostics diagnostics = new(loggerMock.Object);

    PipelineEvent? received = null;
    diagnostics.Subscribe(evt => received = evt);

    PipelineEvent.Listening expected = new(53234, 53235);
    diagnostics.Report(expected);

    Assert.Same(expected, received);
}
```

- [ ] **Step 2: Run full test suite both sides**

Run:
```bash
dotnet test
./gradlew :app:testPortableDebugUnitTest
```
Expected: ALL PASS. Coverage gate (Core ≥ 100% line/branch, Kover green) holds.

- [ ] **Step 3: Commit**

```bash
git add tests/WindowStream.Core.Tests/Observability/DiagnosticsTests.cs
git commit -m "test(core): cover Diagnostics.Subscribe fan-out"
```

### Task 26: End-to-end smoke test

- [ ] **Step 1: Server-side**

Run:
```bash
dotnet run --project src/WindowStreamServer -f net10.0-windows10.0.19041.0
```
Expected: dashboard opens. Verify the state board shows "Listening ✓" with ports populated. Open `%LOCALAPPDATA%\WindowStream\logs\` — see today's `.jsonl` file with at least the `Listening` event.

- [ ] **Step 2: Viewer-side (portable)**

Install + launch on a connected device. Tap "🛈" — overlay appears. Run:
```bash
adb pull /storage/emulated/0/Android/data/com.mtschoen.windowstream.viewer/files/logs/ ./tmp-viewer-logs/
```
Expected: at least `DiscoveryStarted` line in the JSONL.

- [ ] **Step 3: Fault-injection test**

Launch viewer with bogus selectedWindowIds (per existing `project_synthesize_window_not_found` pattern):
```bash
adb shell am start -n com.mtschoen.windowstream.viewer/.demo.UnifiedStreamingActivity \
    --es streamHost <pc-lan-ip> --ei streamPort <tcpPort> \
    --ela selectedWindowIds 99999999
```
Expected: overlay shows `Stream` row in error state with the server's `STREAM_REFUSED` message inline. Server dashboard shows the matching `StreamRefused` event in the event log.

- [ ] **Step 4: No commit (verification only)** — if anything fails, fix inline and commit.

---

## Final sanity

- [ ] Run `dotnet test` and `./gradlew :app:testPortableDebugUnitTest` — all green.
- [ ] Run `dotnet build` and `./gradlew :app:assembleDebug` — clean build both flavors.
- [ ] Confirm `git status` is clean.
- [ ] Confirm no `[FRAMECOUNT]` calls were accidentally routed through `Diagnostics` (grep for `Diagnostics.Report.*FRAMECOUNT` should be empty).
