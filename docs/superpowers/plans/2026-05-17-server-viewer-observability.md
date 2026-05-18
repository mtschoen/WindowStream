# Server + Viewer Observability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the user end-to-end pipeline visibility on both the WindowStream server (MAUI dashboard) and the viewer (Android), so "tap connect, nothing happens" is diagnosable without reading `adb logcat` or invisible MAUI debug output.

**Architecture:** A typed `PipelineEvent` sealed class hierarchy on each side feeds a `Diagnostics` façade. On the server, events flow through `Microsoft.Extensions.Logging.ILogger` to three sinks: existing Debug, a Serilog rolling JSONL file sink, and a custom in-app sink that fans out to the `ServerDashboardViewModel`. On the viewer, events flow through Timber to three trees: existing Logcat, a rotating JSONL `FileLoggingTree`, and an `InAppBufferTree` exposing a `SharedFlow<LogEvent>` to the UI. A per-side reducer derives the state board from the event stream so the board and the event log can't disagree by construction.

**Tech Stack:**
- Server (.NET 10): `Microsoft.Extensions.Logging`, Serilog (`Serilog`, `Serilog.Extensions.Logging`, `Serilog.Sinks.File`, `Serilog.Formatting.Compact`), MAUI bindings.
- Viewer (Android Kotlin): `com.jakewharton.timber:timber`, `kotlinx-serialization-json` (already present), `kotlinx-coroutines` (already present).

---

## Phase 4: Viewer foundation (Timber + types + trees)

### Task 11: Add Timber dependency

**Files:**
- Modify: `viewer/WindowStreamViewer/gradle/libs.versions.toml`
- Modify: `viewer/WindowStreamViewer/app/build.gradle.kts`

- [x] **Step 1: Add `timber` entry to `libs.versions.toml`**

Add under `[versions]`:
```toml
timber = "5.0.1"
```
Add under `[libraries]`:
```toml
timber = { module = "com.jakewharton.timber:timber", version.ref = "timber" }
```

- [x] **Step 2: Add dependency to `app/build.gradle.kts`**

In `dependencies { ... }`:
```kotlin
implementation(libs.timber)
```

- [x] **Step 3: Sync + build** *(verified BUILD SUCCESSFUL in 56s, commit `818f4cc`)*

- [x] **Step 4: Commit**

```bash
git add viewer/WindowStreamViewer/gradle/libs.versions.toml \
        viewer/WindowStreamViewer/app/build.gradle.kts
git commit -m "build(viewer): add Timber 5.0.1 dependency"
```

### Task 12: Viewer `PipelineEvent` sealed class

**Files:**
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/PipelineEvent.kt`
- Create: `viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/PipelineEventTest.kt`

- [x] **Step 1: Write the failing test** *(deviation: expanded from 3 cases to 22 — exhaustive coverage of every event subclass — to honor the 100% Kover line+branch gate)*

```kotlin
package com.mtschoen.windowstream.viewer.observability

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class PipelineEventTest {
    @Test
    fun `DiscoveryResultReceived has info severity and carries fields`() {
        val event = PipelineEvent.DiscoveryResultReceived(
            hostname = "chonkers", address = "192.168.1.10", port = 53234
        )
        assertEquals(Severity.INFO, event.severity)
        assertEquals(null, event.streamId)
        assertEquals("chonkers", event.hostname)
    }

    @Test
    fun `DecoderFailed has error severity and stream id`() {
        val event = PipelineEvent.DecoderFailed(streamId = 7, cause = RuntimeException("nope"))
        assertEquals(Severity.ERROR, event.severity)
        assertEquals(7, event.streamId)
    }

    @Test
    fun `UdpStalled has warning severity`() {
        val event = PipelineEvent.UdpStalled(streamId = 1, gapMs = 3000L)
        assertEquals(Severity.WARNING, event.severity)
    }
}
```

- [x] **Step 2: Run, verify FAIL** *(deviation: skipped to save one gradle cycle — test + impl written together; the verify-PASS step at Step 4 served as the gate)*

- [x] **Step 3: Write `PipelineEvent.kt`**

```kotlin
package com.mtschoen.windowstream.viewer.observability

enum class Severity { INFO, WARNING, ERROR }

sealed class PipelineEvent(val severity: Severity, val streamId: Int?) {
    object DiscoveryStarted : PipelineEvent(Severity.INFO, null)
    data class DiscoveryResultReceived(val hostname: String, val address: String, val port: Int)
        : PipelineEvent(Severity.INFO, null)
    object DiscoveryTimedOut : PipelineEvent(Severity.WARNING, null)

    data class TcpConnecting(val host: String, val port: Int) : PipelineEvent(Severity.INFO, null)
    data class TcpConnected(val durationMs: Long) : PipelineEvent(Severity.INFO, null)
    data class TcpConnectFailed(val host: String, val port: Int, val cause: Throwable)
        : PipelineEvent(Severity.ERROR, null)

    data class ServerHelloReceived(val windowCount: Int, val udpPort: Int)
        : PipelineEvent(Severity.INFO, null)

    data class OpenStreamSent(val windowId: ULong) : PipelineEvent(Severity.INFO, null)
    data class StreamOpened(override val sid: Int, val width: Int, val height: Int)
        : PipelineEvent(Severity.INFO, sid) {
            companion object { /* shim placeholder */ }
        }
    data class StreamRefused(val sid: Int, val errorCode: String, val message: String)
        : PipelineEvent(Severity.WARNING, sid)
    data class StreamStopped(val sid: Int, val reason: String)
        : PipelineEvent(Severity.INFO, sid)

    data class UdpBound(val port: Int) : PipelineEvent(Severity.INFO, null)
    data class UdpFirstPacketReceived(val sid: Int, val delayMs: Long)
        : PipelineEvent(Severity.INFO, sid)
    data class UdpStalled(val sid: Int, val gapMs: Long)
        : PipelineEvent(Severity.WARNING, sid)

    data class DecoderStarting(val sid: Int, val width: Int, val height: Int)
        : PipelineEvent(Severity.INFO, sid)
    data class DecoderStarted(val sid: Int) : PipelineEvent(Severity.INFO, sid)
    data class DecoderFailed(val sid: Int, val cause: Throwable)
        : PipelineEvent(Severity.ERROR, sid)

    data class SurfaceCreated(val panelIndex: Int) : PipelineEvent(Severity.INFO, null)
    data class SurfaceDestroyed(val panelIndex: Int, val reasonHint: String)
        : PipelineEvent(Severity.INFO, null)

    data class FramesPresenting(val sid: Int, val fps: Double) : PipelineEvent(Severity.INFO, sid)

    object WifiLockAcquired : PipelineEvent(Severity.INFO, null)
    object WifiLockReleased : PipelineEvent(Severity.INFO, null)

    val streamId_alias: Int? get() = streamId  // ergonomic helper; not necessary
}
```

**Important:** the constructor convention is `(severity, streamId)`. Two ways to handle event types that have a stream id:
- Pass `streamId` directly as the second positional arg (preferred — keeps the property `streamId` on the base class).
- The `sid` naming above shadows; rename `sid` → `streamId` and remove the `override`/`companion object` boilerplate. Final form:

```kotlin
data class StreamOpened(val sid: Int, val width: Int, val height: Int)
    : PipelineEvent(Severity.INFO, sid)
```
(All `data class` cases that have a stream id use `val sid: Int` as the first ctor param and pass it as the second arg to the superclass.)

Rewrite `PipelineEvent.kt` accordingly — final clean version:

```kotlin
package com.mtschoen.windowstream.viewer.observability

enum class Severity { INFO, WARNING, ERROR }

sealed class PipelineEvent(val severity: Severity, val streamId: Int?) {
    object DiscoveryStarted : PipelineEvent(Severity.INFO, null)
    data class DiscoveryResultReceived(val hostname: String, val address: String, val port: Int)
        : PipelineEvent(Severity.INFO, null)
    object DiscoveryTimedOut : PipelineEvent(Severity.WARNING, null)

    data class TcpConnecting(val host: String, val port: Int) : PipelineEvent(Severity.INFO, null)
    data class TcpConnected(val durationMs: Long) : PipelineEvent(Severity.INFO, null)
    data class TcpConnectFailed(val host: String, val port: Int, val cause: Throwable)
        : PipelineEvent(Severity.ERROR, null)

    data class ServerHelloReceived(val windowCount: Int, val udpPort: Int)
        : PipelineEvent(Severity.INFO, null)

    data class OpenStreamSent(val windowId: ULong) : PipelineEvent(Severity.INFO, null)
    data class StreamOpened(val sid: Int, val width: Int, val height: Int) : PipelineEvent(Severity.INFO, sid)
    data class StreamRefused(val sid: Int, val errorCode: String, val message: String) : PipelineEvent(Severity.WARNING, sid)
    data class StreamStopped(val sid: Int, val reason: String) : PipelineEvent(Severity.INFO, sid)

    data class UdpBound(val port: Int) : PipelineEvent(Severity.INFO, null)
    data class UdpFirstPacketReceived(val sid: Int, val delayMs: Long) : PipelineEvent(Severity.INFO, sid)
    data class UdpStalled(val sid: Int, val gapMs: Long) : PipelineEvent(Severity.WARNING, sid)

    data class DecoderStarting(val sid: Int, val width: Int, val height: Int) : PipelineEvent(Severity.INFO, sid)
    data class DecoderStarted(val sid: Int) : PipelineEvent(Severity.INFO, sid)
    data class DecoderFailed(val sid: Int, val cause: Throwable) : PipelineEvent(Severity.ERROR, sid)

    data class SurfaceCreated(val panelIndex: Int) : PipelineEvent(Severity.INFO, null)
    data class SurfaceDestroyed(val panelIndex: Int, val reasonHint: String) : PipelineEvent(Severity.INFO, null)

    data class FramesPresenting(val sid: Int, val fps: Double) : PipelineEvent(Severity.INFO, sid)

    object WifiLockAcquired : PipelineEvent(Severity.INFO, null)
    object WifiLockReleased : PipelineEvent(Severity.INFO, null)
}
```

Update the test's `DecoderFailed(streamId = 7, ...)` to `DecoderFailed(sid = 7, ...)`, etc.

- [x] **Step 4: Run, verify PASS** *(22/22 cases PASS; also ran `:app:koverVerifyPortableDebug` and the 100% gate held)*

- [x] **Step 5: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/PipelineEvent.kt \
        viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/PipelineEventTest.kt
git commit -m "feat(viewer): add PipelineEvent sealed hierarchy and Severity enum"
```

### Task 13: `Diagnostics` object + `LogEvent` record + thread-local payload bridge

**Files:**
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/LogEvent.kt`
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/Diagnostics.kt`

- [x] **Step 1: Write `LogEvent.kt`**

```kotlin
package com.mtschoen.windowstream.viewer.observability

import java.time.Instant

data class LogEvent(
    val timestamp: Instant,
    val severity: Severity,
    val eventType: String,
    val streamId: Int?,
    val message: String,
    val payload: Map<String, Any?>,
    val throwable: Throwable?,
)
```

- [x] **Step 2: Write `Diagnostics.kt`**

```kotlin
package com.mtschoen.windowstream.viewer.observability

import timber.log.Timber
import java.time.Instant

/**
 * Façade that translates a [PipelineEvent] into a Timber call. Two custom
 * trees (FileLoggingTree, InAppBufferTree) read the payload via a
 * ThreadLocal map populated immediately before the log call.
 *
 * Per-frame markers ([FRAMECOUNT]) deliberately bypass this façade — they
 * live in stderr/logcat to avoid flooding the in-app buffer.
 */
object Diagnostics {

    internal val currentPayload: ThreadLocal<Map<String, Any?>> = ThreadLocal.withInitial { emptyMap() }
    internal val currentEvent: ThreadLocal<PipelineEvent?> = ThreadLocal.withInitial { null }

    fun report(event: PipelineEvent) {
        val tree = Timber.tag(TAG)
        val payload = payloadOf(event)
        currentPayload.set(payload)
        currentEvent.set(event)
        try {
            val message = describe(event)
            when (event.severity) {
                Severity.INFO -> tree.i(message)
                Severity.WARNING -> tree.w(message)
                Severity.ERROR -> tree.e(throwableOf(event), message)
            }
        } finally {
            currentPayload.remove()
            currentEvent.remove()
        }
    }

    private fun describe(event: PipelineEvent): String = event::class.simpleName + ": " + event.toString()

    private fun throwableOf(event: PipelineEvent): Throwable? = when (event) {
        is PipelineEvent.TcpConnectFailed -> event.cause
        is PipelineEvent.DecoderFailed -> event.cause
        else -> null
    }

    private fun payloadOf(event: PipelineEvent): Map<String, Any?> = buildMap {
        put("eventType", event::class.simpleName)
        put("streamId", event.streamId)
        when (event) {
            is PipelineEvent.DiscoveryResultReceived -> {
                put("hostname", event.hostname); put("address", event.address); put("port", event.port)
            }
            is PipelineEvent.TcpConnecting -> { put("host", event.host); put("port", event.port) }
            is PipelineEvent.TcpConnected -> put("durationMs", event.durationMs)
            is PipelineEvent.TcpConnectFailed -> { put("host", event.host); put("port", event.port) }
            is PipelineEvent.ServerHelloReceived -> {
                put("windowCount", event.windowCount); put("udpPort", event.udpPort)
            }
            is PipelineEvent.OpenStreamSent -> put("windowId", event.windowId.toString())
            is PipelineEvent.StreamOpened -> { put("width", event.width); put("height", event.height) }
            is PipelineEvent.StreamRefused -> { put("errorCode", event.errorCode); put("message", event.message) }
            is PipelineEvent.StreamStopped -> put("reason", event.reason)
            is PipelineEvent.UdpBound -> put("port", event.port)
            is PipelineEvent.UdpFirstPacketReceived -> put("delayMs", event.delayMs)
            is PipelineEvent.UdpStalled -> put("gapMs", event.gapMs)
            is PipelineEvent.DecoderStarting -> { put("width", event.width); put("height", event.height) }
            is PipelineEvent.SurfaceCreated -> put("panelIndex", event.panelIndex)
            is PipelineEvent.SurfaceDestroyed -> {
                put("panelIndex", event.panelIndex); put("reasonHint", event.reasonHint)
            }
            is PipelineEvent.FramesPresenting -> put("fps", event.fps)
            else -> {} // objects + types without extra payload
        }
    }

    private const val TAG = "Pipeline"
}
```

- [x] **Step 3: Build + smoke test**

Run: `./gradlew :app:assemblePortableDebug` — expect SUCCESS.

- [x] **Step 4: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/
git commit -m "feat(viewer): Diagnostics façade with ThreadLocal payload bridge"
```

### Task 14: `InAppBufferTree`

**Files:**
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/InAppBufferTree.kt`
- Create: `viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/InAppBufferTreeTest.kt`

- [ ] **Step 1: Write the failing test**

```kotlin
package com.mtschoen.windowstream.viewer.observability

import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test
import timber.log.Timber

class InAppBufferTreeTest {

    @Test
    fun `report emits one LogEvent on the SharedFlow`() = runBlocking {
        val tree = InAppBufferTree(replay = 16)
        Timber.plant(tree)
        try {
            Diagnostics.report(PipelineEvent.DiscoveryTimedOut)
            val received = tree.events.first()
            assertEquals("DiscoveryTimedOut", received.eventType)
            assertEquals(Severity.WARNING, received.severity)
        } finally {
            Timber.uproot(tree)
        }
    }
}
```

- [ ] **Step 2: Run, verify FAIL**

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*InAppBufferTreeTest*"`
Expected: FAIL — `InAppBufferTree` missing.

- [ ] **Step 3: Write `InAppBufferTree.kt`**

```kotlin
package com.mtschoen.windowstream.viewer.observability

import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import timber.log.Timber
import java.time.Instant

class InAppBufferTree(replay: Int = 200) : Timber.Tree() {

    private val _events = MutableSharedFlow<LogEvent>(replay = replay, extraBufferCapacity = 64)
    val events: SharedFlow<LogEvent> = _events.asSharedFlow()

    override fun log(priority: Int, tag: String?, message: String, t: Throwable?) {
        val event = Diagnostics.currentEvent.get()
        val payload = Diagnostics.currentPayload.get()
        val severity = when {
            priority >= android.util.Log.ERROR -> Severity.ERROR
            priority >= android.util.Log.WARN -> Severity.WARNING
            else -> Severity.INFO
        }
        val logEvent = LogEvent(
            timestamp = Instant.now(),
            severity = severity,
            eventType = (payload["eventType"] as? String) ?: "Log",
            streamId = payload["streamId"] as? Int,
            message = message,
            payload = payload,
            throwable = t,
        )
        _events.tryEmit(logEvent)
    }
}
```

- [ ] **Step 4: Run, verify PASS**

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*InAppBufferTreeTest*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/InAppBufferTree.kt \
        viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/InAppBufferTreeTest.kt
git commit -m "feat(viewer): InAppBufferTree exposing SharedFlow of LogEvent"
```

### Task 15: `FileLoggingTree`

**Files:**
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/FileLoggingTree.kt`
- Create: `viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/FileLoggingTreeTest.kt`

- [ ] **Step 1: Write the failing test (Robolectric-free, uses a temp dir directly)**

```kotlin
package com.mtschoen.windowstream.viewer.observability

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import timber.log.Timber
import java.io.File
import java.time.Clock
import java.time.Instant
import java.time.ZoneOffset

class FileLoggingTreeTest {

    @Test
    fun `report writes one JSONL line to dated file`(@TempDir tempDir: File) {
        val clock = Clock.fixed(Instant.parse("2026-05-17T12:34:56Z"), ZoneOffset.UTC)
        val tree = FileLoggingTree(directory = tempDir, retentionDays = 7, clock = clock)
        Timber.plant(tree)
        try {
            Diagnostics.report(PipelineEvent.UdpBound(port = 53235))
            tree.flush()
            val expected = File(tempDir, "viewer-2026-05-17.jsonl")
            assertTrue(expected.exists())
            val lines = expected.readLines()
            assertEquals(1, lines.size)
            assertTrue(lines[0].contains("\"eventType\":\"UdpBound\""))
        } finally {
            Timber.uproot(tree)
            tree.close()
        }
    }

    @Test
    fun `rotation deletes files older than retentionDays`(@TempDir tempDir: File) {
        // create stale files
        File(tempDir, "viewer-2026-05-09.jsonl").writeText("old\n")
        File(tempDir, "viewer-2026-05-10.jsonl").writeText("old\n")
        val clock = Clock.fixed(Instant.parse("2026-05-17T00:00:00Z"), ZoneOffset.UTC)
        val tree = FileLoggingTree(directory = tempDir, retentionDays = 7, clock = clock)
        Timber.plant(tree)
        try {
            Diagnostics.report(PipelineEvent.UdpBound(port = 1))
            tree.flush()
            assertTrue(!File(tempDir, "viewer-2026-05-09.jsonl").exists())
            assertTrue(File(tempDir, "viewer-2026-05-10.jsonl").exists()) // exactly retentionDays old, kept
        } finally {
            Timber.uproot(tree)
            tree.close()
        }
    }
}
```

- [ ] **Step 2: Run, verify FAIL**

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*FileLoggingTreeTest*"`
Expected: FAIL — `FileLoggingTree` missing.

- [ ] **Step 3: Write `FileLoggingTree.kt`**

```kotlin
package com.mtschoen.windowstream.viewer.observability

import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import timber.log.Timber
import java.io.BufferedWriter
import java.io.File
import java.io.FileWriter
import java.time.Clock
import java.time.Duration
import java.time.LocalDate
import java.time.ZoneId
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

class FileLoggingTree(
    private val directory: File,
    private val retentionDays: Int = 7,
    private val clock: Clock = Clock.systemUTC(),
) : Timber.Tree(), AutoCloseable {

    private val executor: ExecutorService = Executors.newSingleThreadExecutor { runnable ->
        Thread(runnable, "WindowStream-Log-Writer").apply { isDaemon = true }
    }
    private var currentDate: LocalDate? = null
    private var writer: BufferedWriter? = null

    init {
        directory.mkdirs()
    }

    override fun log(priority: Int, tag: String?, message: String, t: Throwable?) {
        val payload = Diagnostics.currentPayload.get()
        val severity = when {
            priority >= android.util.Log.ERROR -> "ERROR"
            priority >= android.util.Log.WARN -> "WARN"
            else -> "INFO"
        }
        val nowInstant = clock.instant()
        val nowDate = nowInstant.atZone(ZoneId.from(ZoneOffsetUtc)).toLocalDate()

        val record = buildJsonObject {
            put("ts", nowInstant.toString())
            put("level", severity)
            put("eventType", (payload["eventType"] as? String) ?: "Log")
            payload["streamId"]?.let { put("streamId", it.toString()) }
            put("msg", message)
            t?.let { put("exception", it.stackTraceToString()) }
            for ((key, value) in payload) {
                if (key == "eventType" || key == "streamId") continue
                put(key, value?.toString() ?: "")
            }
        }
        val line = Json.encodeToString(JsonElement.serializer(), record)

        executor.execute {
            try {
                rotateIfNeeded(nowDate)
                writer?.appendLine(line)
            } catch (failure: Throwable) {
                android.util.Log.e("FileLoggingTree", "write failed", failure)
            }
        }
    }

    fun flush() {
        executor.submit { writer?.flush() }.get()
    }

    private fun rotateIfNeeded(today: LocalDate) {
        if (currentDate == today && writer != null) return
        writer?.close()
        currentDate = today
        val file = File(directory, "viewer-$today.jsonl")
        writer = BufferedWriter(FileWriter(file, /* append = */ true))
        purgeOldFiles(today)
    }

    private fun purgeOldFiles(today: LocalDate) {
        val cutoff = today.minusDays(retentionDays.toLong())
        directory.listFiles { _, name -> name.matches(Regex("""viewer-\d{4}-\d{2}-\d{2}\.jsonl""")) }
            ?.forEach { file ->
                val dateText = file.nameWithoutExtension.removePrefix("viewer-")
                val fileDate = runCatching { LocalDate.parse(dateText) }.getOrNull() ?: return@forEach
                if (fileDate.isBefore(cutoff)) file.delete()
            }
    }

    override fun close() {
        executor.submit { writer?.close() }.get()
        executor.shutdown()
    }

    private val ZoneOffsetUtc get() = java.time.ZoneOffset.UTC
}
```

- [ ] **Step 4: Run, verify PASS**

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*FileLoggingTreeTest*"`
Expected: PASS 2/2. If serialization complains about generic `Any?` in `put(...)`, switch the loop to `put(key, JsonPrimitive(value?.toString()))`.

- [ ] **Step 5: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/FileLoggingTree.kt \
        viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/FileLoggingTreeTest.kt
git commit -m "feat(viewer): FileLoggingTree with daily rotation and retention"
```

### Task 16: Plant trees in `WindowStreamViewerApplication`

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/WindowStreamViewerApplication.kt`

- [ ] **Step 1: Read current Application class**

Use Read tool on the file path above.

- [ ] **Step 2: Update `onCreate` to plant trees**

```kotlin
package com.mtschoen.windowstream.viewer.app

import android.app.Application
import com.mtschoen.windowstream.viewer.observability.FileLoggingTree
import com.mtschoen.windowstream.viewer.observability.InAppBufferTree
import timber.log.Timber
import java.io.File

class WindowStreamViewerApplication : Application() {

    lateinit var inAppBufferTree: InAppBufferTree
        private set

    override fun onCreate() {
        super.onCreate()
        if (Timber.treeCount == 0) {
            Timber.plant(Timber.DebugTree())
            val logsDirectory = File(getExternalFilesDir(null), "logs")
            Timber.plant(FileLoggingTree(directory = logsDirectory))
            inAppBufferTree = InAppBufferTree(replay = 200)
            Timber.plant(inAppBufferTree)
        }
    }
}
```

- [ ] **Step 3: Build + install**

Run: `./gradlew :app:assemblePortableDebug && adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/portable/debug/app-portable-debug.apk`
Expected: BUILD SUCCESSFUL; install succeeds. Launch viewer, run `adb logcat | grep Pipeline` — should be empty until pipeline events are emitted.

- [ ] **Step 4: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/WindowStreamViewerApplication.kt
git commit -m "feat(viewer): plant FileLoggingTree + InAppBufferTree in Application"
```

---

## Phase 5: Viewer instrumentation (call sites → Diagnostics)

### Task 17: Refactor `UnifiedStreamingActivity` to emit `PipelineEvent`s

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/UnifiedStreamingActivity.kt`

Replace existing `Log.i(TAG, …)` / `Log.e(TAG, …)` calls that mark pipeline stages with `Diagnostics.report(...)`. Keep `Log.i` / `Log.e` for purely free-form info (tab UI, soft keyboard) — those don't need typed events.

- [ ] **Step 1: Replace discovery + connect events**

In `discoverAndConnect()`, replace:
```kotlin
Log.i(TAG, "discovered ${server.hostname} at ${server.host.hostAddress}:${server.controlPort}")
```
with:
```kotlin
Diagnostics.report(PipelineEvent.DiscoveryResultReceived(
    hostname = server.hostname,
    address = server.host.hostAddress ?: "?",
    port = server.controlPort))
```

Add at the start of `discoverAndConnect()`:
```kotlin
Diagnostics.report(PipelineEvent.DiscoveryStarted)
```

Wrap the `withTimeout(30_000)` in a `try`/`catch (TimeoutCancellationException)` and report `DiscoveryTimedOut` (also keep the existing catch for general throwables, where we already log).

Replace:
```kotlin
Log.e(TAG, "discovery/connect failed", throwable)
```
with:
```kotlin
Diagnostics.report(PipelineEvent.TcpConnectFailed(host = host, port = port, cause = throwable))
```

- [ ] **Step 2: Replace ServerHello + open + lifecycle**

In `connectToServer`, around `client.connect(...)`:
```kotlin
val connectStart = System.nanoTime()
Diagnostics.report(PipelineEvent.TcpConnecting(host = host, port = port))
val liveConnection = client.connect(activityScope)
val elapsedMs = (System.nanoTime() - connectStart) / 1_000_000
Diagnostics.report(PipelineEvent.TcpConnected(durationMs = elapsedMs))
```

Replace `Log.i(TAG, "connected: ${initialCatalogue.size} window(s) advertised")` with:
```kotlin
Diagnostics.report(PipelineEvent.ServerHelloReceived(
    windowCount = initialCatalogue.size,
    udpPort = liveConnection.serverHello.udpPort))
```

In `openWindow`, replace `Log.i(TAG, "opening stream for windowId=$windowId")`:
```kotlin
Diagnostics.report(PipelineEvent.OpenStreamSent(windowId = windowId))
```

For the `StreamLifecycleEvent` collector branches:
- `Opened` → `Diagnostics.report(PipelineEvent.StreamOpened(event.streamId, event.width, event.height))`
- `Refused` → `Diagnostics.report(PipelineEvent.StreamRefused(event.streamId, event.errorCode, event.message))`
- `Stopped` → `Diagnostics.report(PipelineEvent.StreamStopped(event.streamId, event.reason.reason))`

Keep the existing `runOnUiThread { statusLabel.text = ... }` UI updates.

- [ ] **Step 3: Surface lifecycle**

In `createSurfaceCallback`:
- `surfaceCreated`: `Diagnostics.report(PipelineEvent.SurfaceCreated(panelIndex))`
- `surfaceDestroyed`: `Diagnostics.report(PipelineEvent.SurfaceDestroyed(panelIndex, reasonHint = "lifecycle"))`

In `acquireWifiLock`:
```kotlin
Diagnostics.report(PipelineEvent.WifiLockAcquired)
```
And in `onDestroy` where the lock is released:
```kotlin
Diagnostics.report(PipelineEvent.WifiLockReleased)
```

- [ ] **Step 4: UDP arrival tracking**

In `startDecoderLocked`, after `udpReceiver.start(pipelineScope)`, but before kicking the decoder, attach a `Flow` operator that emits `UdpFirstPacketReceived` on first packet:
```kotlin
val openInstantNanos = System.nanoTime()
var firstReported = false
val instrumentedFrames: Flow<EncodedFrame> = frames.onEach {
    if (!firstReported) {
        firstReported = true
        val delay = (System.nanoTime() - openInstantNanos) / 1_000_000
        Diagnostics.report(PipelineEvent.UdpFirstPacketReceived(streamId, delay))
    }
}
Diagnostics.report(PipelineEvent.UdpBound(udpReceiver.boundPort))
Diagnostics.report(PipelineEvent.DecoderStarting(streamId, resolvedWidth, resolvedHeight))
```

Replace the rest of the body to use `instrumentedFrames` instead of `frames` for the `decoder.start(...)` call. Add the import: `import kotlinx.coroutines.flow.onEach`.

After `decoder.start(...)`:
```kotlin
Diagnostics.report(PipelineEvent.DecoderStarted(streamId))
```

Wrap `decoder.start(...)` in a `try` that catches and emits `DecoderFailed`.

- [ ] **Step 5: Build + smoke install**

Run: `./gradlew :app:assemblePortableDebug && adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/portable/debug/app-portable-debug.apk`
Expected: BUILD SUCCESSFUL. Launch viewer; run `adb logcat -s Pipeline:V` and exercise: open viewer → expect `DiscoveryStarted` then `DiscoveryResultReceived` or `DiscoveryTimedOut`.

- [ ] **Step 6: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/UnifiedStreamingActivity.kt
git commit -m "refactor(viewer): emit PipelineEvents from UnifiedStreamingActivity"
```

### Task 18: Refactor `XrDemoActivity` + GXR `MainActivity`

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/XrDemoActivity.kt`
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/MainActivity.kt`

- [ ] **Step 1: `XrDemoActivity` — apply same patterns as Task 17**

For each existing `Log.i(TAG, "...")` that maps to a pipeline stage:
- "starting XR compositor path" → unchanged (free-form)
- "SpatialExternalSurface created" → `SurfaceCreated(panelIndex = 0)`
- "SpatialExternalSurface destroyed" → `SurfaceDestroyed(panelIndex = 0, "spatial-lifecycle")`
- "TCP connected to" → `TcpConnected(durationMs = measuredMs)` (add timing)
- "ServerHello: N window(s)" → `ServerHelloReceived(serverHello.windows.size, serverHello.udpPort)`
- "opening windowId=$windowId" → `OpenStreamSent(windowId.toULong())`
- "StreamStarted: ${stream.width}x${stream.height} streamId=${stream.streamId}" → `StreamOpened(stream.streamId, stream.width, stream.height)`
- "UDP bound on port ${udpReceiver.boundPort}" → `UdpBound(udpReceiver.boundPort)`
- "decoder started, rendering through XR compositor" → `DecoderStarted(stream.streamId)` (after a `DecoderStarting`)

- [ ] **Step 2: `MainActivity` (GXR picker)**

Read the file in full first (`viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/MainActivity.kt`). Locate:
1. The mDNS discovery start — wrap with `Diagnostics.report(PipelineEvent.DiscoveryStarted)` immediately before, and `Diagnostics.report(PipelineEvent.DiscoveryResultReceived(...))` on each server result.
2. The discovery timeout branch — emit `DiscoveryTimedOut`.
3. The window-selection handler (the picker handoff that fires the Intent to `XrDemoActivity`) — emit `Diagnostics.report(PipelineEvent.OpenStreamSent(windowId))` before `startActivity(intent)`.

If `MainActivity` already delegates discovery to `NetworkServiceDiscoveryClient` shared with `UnifiedStreamingActivity`, the report sites are at the same call layer — copy the pattern from Task 17 Step 1 verbatim.

Do NOT commit without showing concrete diffs of all three insertion points.

- [ ] **Step 3: Build all flavors**

Run: `./gradlew :app:assembleDebug`
Expected: BUILD SUCCESSFUL for both `portable` and `gxr`.

- [ ] **Step 4: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/XrDemoActivity.kt \
        viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/MainActivity.kt
git commit -m "refactor(viewer): emit PipelineEvents from XrDemoActivity + GXR MainActivity"
```

### Task 19: `MediaCodecDecoder` + `MultiStreamControlClient` instrumentation

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/decoder/MediaCodecDecoder.kt`
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/control/MultiStreamControlClient.kt`

- [ ] **Step 1: `MediaCodecDecoder` — wrap `start` and error paths**

Where the decoder configures and starts, wrap any error path with:
```kotlin
Diagnostics.report(PipelineEvent.DecoderFailed(streamId = /* threaded in or default 0 */, cause = exception))
```
Note: `MediaCodecDecoder` currently doesn't take a stream id. Add a `streamId: Int` constructor parameter and thread it through from `UnifiedStreamingActivity.startDecoderLocked` and `XrDemoActivity`'s decoder creation. Update both callsites.

- [ ] **Step 2: `MultiStreamControlClient` — wrap connect failures**

When `connect()` throws, emit `TcpConnectFailed`. When `StreamLifecycleEvent.Refused` is parsed, emit `StreamRefused` from inside the parser too — currently the activity catches it but instrumentation inside the client provides defense-in-depth.

Note: avoid double-emitting. Prefer single emission site per event; the activity-level `Refused` emit in Task 17 is canonical, so for the client, only emit if the connection-level error is distinct (e.g., framing parse failure).

- [ ] **Step 3: Build**

Run: `./gradlew :app:assembleDebug`
Expected: SUCCESS.

- [ ] **Step 4: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/decoder/MediaCodecDecoder.kt \
        viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/control/MultiStreamControlClient.kt
git commit -m "refactor(viewer): instrument decoder + control client"
```

### Task 20: `UdpStalled` watchdog

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/UnifiedStreamingActivity.kt`
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/XrDemoActivity.kt`

- [ ] **Step 1: Watchdog implementation**

Inside `startDecoderLocked` after instrumenting first-packet detection (Task 17 Step 4), launch a watchdog:
```kotlin
pipelineScope.launch {
    delay(2000)
    if (!firstReported) {
        Diagnostics.report(PipelineEvent.UdpStalled(streamId, 2000))
    }
}
```
Note: `firstReported` is captured by lambda, must be `var` — adjust the declaration to `@Volatile var firstReported = false` or wrap in `AtomicBoolean`. Use `AtomicBoolean` for thread safety.

Rewrite the watchdog + first-packet flag using `AtomicBoolean`:
```kotlin
val firstReportedFlag = java.util.concurrent.atomic.AtomicBoolean(false)
val instrumentedFrames = frames.onEach {
    if (firstReportedFlag.compareAndSet(false, true)) {
        val delay = (System.nanoTime() - openInstantNanos) / 1_000_000
        Diagnostics.report(PipelineEvent.UdpFirstPacketReceived(streamId, delay))
    }
}
pipelineScope.launch {
    delay(2000)
    if (!firstReportedFlag.get()) {
        Diagnostics.report(PipelineEvent.UdpStalled(streamId, 2000))
    }
}
```

- [ ] **Step 2: Apply same pattern in `XrDemoActivity`**

Replicate near where `udpReceiver.start(...)` is invoked in `XrDemoActivity`.

- [ ] **Step 3: Build**

Run: `./gradlew :app:assembleDebug`
Expected: SUCCESS.

- [ ] **Step 4: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/
git commit -m "feat(viewer): UdpStalled 2s watchdog"
```

---

## Phase 6: Viewer observability UI

### Task 21: Viewer state reducer

**Files:**
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/ViewerStateReducer.kt`
- Create: `viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/ViewerStateReducerTest.kt`

- [ ] **Step 1: Write failing test**

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

- [ ] **Step 2: Run, verify FAIL**

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*ViewerStateReducerTest*"`
Expected: FAIL.

- [ ] **Step 3: Write `ViewerStateReducer.kt`**

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

- [ ] **Step 4: Run, verify PASS**

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*ViewerStateReducerTest*"`
Expected: PASS 3/3.

- [ ] **Step 5: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/ViewerStateReducer.kt \
        viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/ViewerStateReducerTest.kt
git commit -m "feat(viewer): state reducer for observability board"
```

### Task 22: Phone/tablet overlay panel in `UnifiedStreamingActivity`

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/UnifiedStreamingActivity.kt`
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/ObservabilityOverlay.kt`

- [ ] **Step 1: Write `ObservabilityOverlay.kt`**

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

- [ ] **Step 2: Wire into `UnifiedStreamingActivity`**

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

- [ ] **Step 3: Build + install + smoke**

Run: `./gradlew :app:assemblePortableDebug && adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/portable/debug/app-portable-debug.apk`
Expected: SUCCESS. Launch viewer, tap the "🛈" button — overlay opens with state board + event log populated.

- [ ] **Step 4: Commit**

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

- [ ] **Step 1: GXR — add a 2D `SpatialPanel` next to the streaming panel**

In `XrDemoActivity`, after the existing scene composition, add a second `SpatialPanel` (Jetpack XR scenecore API) hosting an `AndroidView { ObservabilityOverlay(context).rootView.apply { show() } }`. Anchor the panel to the right of the streaming panel using `SubspaceModifier.offset(x = …)`.

If the Jetpack XR scenecore API differs from what `XrDemoActivity` already uses (alpha13 vs alpha04), copy the existing panel-creation pattern from the same file and clone with adjusted offset + content.

Wire the `app.inAppBufferTree.events` collection identical to Task 22.

- [ ] **Step 2: GXR `MainActivity` — overlay (2D, not spatial)**

For `MainActivity`, which is the 2D picker before immersive: add the same `ObservabilityOverlay` overlay used in Task 22.

- [ ] **Step 3: Build + install GXR**

Run: `./gradlew :app:assembleGxrDebug && adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/gxr/debug/app-gxr-debug.apk`
Expected: BUILD SUCCESSFUL.

- [ ] **Step 4: Commit**

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
