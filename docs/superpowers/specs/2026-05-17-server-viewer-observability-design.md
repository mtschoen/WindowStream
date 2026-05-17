# Server + Viewer Observability Design

**Date:** 2026-05-17
**Status:** Working draft (brainstorm → plan handoff). Will be distilled into the implementation plan header and deleted.

## Problem

The user can't diagnose why "tap connect, nothing happens" without falling back on `adb logcat` and reading scattered `Console.Error.WriteLine` debug output (the MAUI server app currently has no log surface at all).

The pipeline crosses two processes and a network — a stall can happen at: discovery, TCP handshake, ServerHello, OPEN_STREAM, worker spawn, WGC capture, NVENC, UDP send, UDP receive, decoder start, surface presentation. Today, only the last stage's exception (or none) is visible.

## Goal

End-to-end pipeline visibility on both server and viewer, with:

1. **State board** — per-stage status at a glance (✓ / ⚠ / ✗ / pending), per-stream collapsible rows on the server, with the last error inline.
2. **Event log pane** — scrollable in-app timeline of timestamped events with severity. Last 500 events in memory.
3. **Rotating JSON file log** — daily rotation, 7-day retention, on both sides. Survives app restart for forensic debugging.

Bound by: error visibility for protocol/capture/encode/decoder/surface failures the user currently can't see without me reading log output for them.

## Architecture

**Approach:** typed `PipelineEvent` sealed class hierarchy emitted through each platform's idiomatic logging library. State board is **derived** from the event stream (reducer pattern) so the board and the event log cannot disagree.

### Server (.NET MAUI)

```
                ┌────────────────────────────────────────┐
                │ Microsoft.Extensions.Logging (ILogger) │
                └────────────────────────────────────────┘
                                  │
                ┌─────────────────┼─────────────────────────┐
                ▼                 ▼                         ▼
         Debug provider    Serilog provider           InAppDashboardSink
         (existing)        ├ CompactJsonFormatter     (custom ILogEventSink)
                           │ → File sink              ring buffer (500)
                           │   rollingInterval=Day    OnEvent → dashboard VM
                           │   retainedFileCountLimit=7
                           │   path=%LOCALAPPDATA%\
                           │        WindowStream\logs\
                           │        server-.jsonl
```

- `PipelineEvent` is a sealed record hierarchy in `WindowStream.Core.Observability`.
- A `Diagnostics` static façade with `Report(PipelineEvent event)` routes through `ILogger` with structured properties (`EventType`, `StreamId`, plus event-specific fields). The same façade is used by Core code (no MAUI dependency) and by the MAUI dashboard subscriber.
- `InAppDashboardSink` is thread-safe (`ConcurrentQueue` + lock for the event boundary). `Emit` is non-blocking; UI marshalling via `MainThread.BeginInvokeOnMainThread`.
- The existing `CoordinatorLauncher(int tcpPort, TextWriter output)` constructor changes to `CoordinatorLauncher(int tcpPort, ILogger<CoordinatorLauncher> logger)`. CLI usage routes to `Microsoft.Extensions.Logging.Console` provider; MAUI usage routes to the in-app sink + Serilog file.
- `ServerDashboardViewModel` subscribes to `InAppDashboardSink.OnEvent`, runs the reducer, raises `PropertyChanged` for state-board bindings, and maintains an `ObservableCollection<LogEntryViewModel>` for the event-log pane.

### Viewer (Android Kotlin)

```
        ┌────────────────────────────┐
        │ Timber                      │
        └────────────────────────────┘
                  │
        ┌─────────┼─────────┬──────────────────────┐
        ▼         ▼         ▼                      ▼
   LogcatTree  FileLoggingTree   InAppBufferTree     DebugTree
   (existing   rotating JSONL    MutableSharedFlow   (formats stack
    Log.*      under app's       (replay=200,         traces)
    behavior)  externalFilesDir   extraBufferCapacity=64)
               daily rotation
               retention: 7 files
```

- `PipelineEvent` is a `sealed class` in `com.mtschoen.windowstream.viewer.observability`.
- A `Diagnostics` object (Kotlin) with `report(event: PipelineEvent)` translates to `Timber.tag("Pipeline").log(...)` plus attaches the structured payload via a `ThreadLocal<Map<String, Any?>>` that `FileLoggingTree` and `InAppBufferTree` read.
- `FileLoggingTree`: `context.getExternalFilesDir(null) / "logs" / "viewer-YYYY-MM-DD.jsonl"`. Single-thread executor for writes so the UI thread never blocks. On first write of a new day, deletes files older than 7 days.
- `InAppBufferTree`: holds the `SharedFlow<LogEvent>` the UI panel collects.

### Reducer (shared semantics, separate implementations per language)

State board cells use a finite state machine:

```
Pending  → InProgress  → Ok
                      ↘  Warning  → Ok
                      ↘  Error    (terminal until reset)
```

Stages on the server:
- `Listening` — green on `Listening`, never returns to gray.
- `ViewerConnected` — green on `ViewerAccepted`, gray on `ViewerDisconnected`.
- `Windows` — green if `WindowAppeared` count > 0.
- For each active stream: `WorkerSpawn`, `Capture`, `Encode`, `UdpSend` — advance through `Started` events, error on matching `Failed` events.

Stages on the viewer:
- `Discovery` — green on `DiscoveryResultReceived`.
- `TcpConnect` — green on `TcpConnected`, error on `TcpConnectFailed`.
- `ServerHello` — green on `ServerHelloReceived`.
- For each open stream: `OpenStream`, `UdpArriving`, `Decoder`, `Surface`, `Presenting`. The `UdpArriving` stage has a 2 s timeout from `StreamOpened` — flips to warning ("no UDP since open") if no `UdpFirstPacketReceived` event in time.

## Event taxonomy

### Server (`WindowStream.Core.Observability.PipelineEvent`)

```
abstract record PipelineEvent(Severity Severity, int? StreamId);

record Listening(int TcpPort, int UdpPort);
record ViewerAccepted(string Endpoint);
record ViewerDisconnected(string Endpoint, string Reason);
record ServerHelloSent(int WindowCount);
record WindowAppeared(ulong WindowId, string Title, string ProcessName, int Width, int Height);
record WindowDisappeared(ulong WindowId);
record WindowChanged(ulong WindowId, string? NewTitle, int? NewWidth, int? NewHeight);
record OpenStreamReceived(int StreamId, ulong WindowId);
record WorkerSpawning(int StreamId, ulong WindowId);
record WorkerSpawned(int StreamId, int Pid);
record WorkerSpawnFailed(int StreamId, Exception Exception);
record CaptureStarted(int StreamId, int Width, int Height);
record CaptureFailed(int StreamId, Exception Exception);
record EncodeStarted(int StreamId, int Fps, int Kbps);
record EncodeFailed(int StreamId, Exception Exception);
record FramesFlowing(int StreamId, double Fps, int Kbps);  // ~1 Hz heartbeat
record StreamRefused(int StreamId, string ErrorCode, string Message);
record StreamStopped(int StreamId, string Reason);
record ProbeFailed(ulong WindowId, long Hwnd, Exception Exception);
record EnumerationFailed(Exception Exception);
```

### Viewer (`com.mtschoen.windowstream.viewer.observability.PipelineEvent`)

```
sealed class PipelineEvent { val severity, val streamId }

object DiscoveryStarted : PipelineEvent()
data class DiscoveryResultReceived(val hostname, val address, val port)
object DiscoveryTimedOut
data class TcpConnecting(val host, val port)
data class TcpConnected(val durationMs)
data class TcpConnectFailed(val host, val port, val cause)
data class ServerHelloReceived(val windowCount, val udpPort)
data class OpenStreamSent(val windowId)
data class StreamOpened(val streamId, val width, val height)
data class StreamRefused(val streamId, val errorCode, val message)
data class StreamStopped(val streamId, val reason)
data class UdpBound(val port)
data class UdpFirstPacketReceived(val streamId, val delayMs)
data class UdpStalled(val streamId, val gapMs)        // emitted on >2 s gap
data class DecoderStarting(val streamId, val width, val height)
data class DecoderStarted(val streamId)
data class DecoderFailed(val streamId, val cause)
data class SurfaceCreated(val panelIndex)
data class SurfaceDestroyed(val panelIndex, val reasonHint)
data class FramesPresenting(val streamId, val fps)    // ~1 Hz heartbeat
object WifiLockAcquired
object WifiLockReleased
```

## UI

### Server dashboard (`MainPage.xaml`)

```
┌────────────────────────────────────────────────┐
│ WindowStream Server                            │
│                                                │
│ ● Serving        TCP 53234  UDP 53235          │
│ Viewer: 192.168.1.42:48512                     │
│ Windows: 12                                    │
│                                                │
│ ┌─ Active streams (2 — 1 errored) ──────────┐  │
│ │ ▼ #1 windowId=7 "Firefox"     ✗           │  │
│ │     Worker spawn  ✓ PID 18420             │  │
│ │     Capture       ✓ 1920×1080             │  │
│ │     Encode        ✗ WGC E_FAIL            │  │
│ │     UDP send      —                       │  │
│ │ ▶ #2 windowId=3 "Terminal"    ✓ 60fps     │  │
│ └───────────────────────────────────────────┘  │
│                                                │
│ ┌─ Recent events ─────────────── [Open log] ┐  │
│ │ 14:02:11.880 INFO  OPEN_STREAM windowId=7 │  │
│ │ 14:02:12.107 ERROR [s#1] WGC E_FAIL       │  │
│ │ 14:02:12.108 WARN  STREAM_REFUSED s#1     │  │
│ └───────────────────────────────────────────┘  │
└────────────────────────────────────────────────┘
```

- "Open log" button opens `%LOCALAPPDATA%\WindowStream\logs\` in Explorer.
- State indicators use a `StatusGlyphConverter` similar to the existing `StatusColorConverter`.
- Per-stream rows use a `CollectionView` with collapsible `Expander` (community-toolkit) or a manual `IsVisible` toggle on a state-binding flag.

### Viewer phone/tablet panel (`UnifiedStreamingActivity`)

- A `FrameLayout` overlay anchored to the bottom of the activity root, semi-transparent (matches `inputPreviewTextView` `Color.argb(200, 0, 0, 0)`).
- Toggled by a small "🛈" button in the tab bar (next to the existing `≡` and `+`).
- Sections: state board (compact stages list) + scrollable event log (auto-scroll to newest).
- "Export log" entry in the panel fires a `ACTION_SEND` intent with the latest JSONL file (`FileProvider` URI).

### Viewer GXR (`MainActivity` picker + `XrDemoActivity` streaming)

- `MainActivity` is a 2-D activity in full-space-managed mode before streaming starts — overlay is a normal `FrameLayout` like the phone case.
- `XrDemoActivity` is immersive; we add a second `SpatialPanel` (300×400 dp) anchored to the right of the main streaming panel, hosting the same observability composables / views.

## File log format

JSON Lines, one event per line. Common fields:

```json
{
  "ts": "2026-05-17T14:02:11.880Z",
  "level": "INFO|WARN|ERROR",
  "eventType": "OpenStreamReceived",
  "streamId": 1,
  "msg": "OPEN_STREAM windowId=7",
  "scope": "CoordinatorControlServer"
}
```

Event-specific fields (`windowId`, `pid`, `width`, `height`, `exception`, etc.) live alongside. Grep with `jq`:

```bash
jq 'select(.eventType=="WorkerSpawnFailed")' server-2026-05-17.jsonl
```

**Paths and retention:**
- Server: `%LOCALAPPDATA%\WindowStream\logs\server-YYYY-MM-DD.jsonl`, retain 7 days.
- Viewer: `<app-external-files>/logs/viewer-YYYY-MM-DD.jsonl`, retain 7 days.

## Boundaries (explicitly out of scope)

- **`[FRAMECOUNT]` per-frame markers** stay on stderr / logcat — NOT routed through this system. They're hot-path and would flood the in-app log + balloon the file. The diagnostic boundary of this system is **stage transitions and errors**, not per-frame. Documented inline in `Diagnostics.cs` and `Diagnostics.kt`.
- **`DemoActivity` and `PanelSwitcherActivity`** are not instrumented. The former is an adb-launch latency-test rig; the latter is being superseded by `UnifiedStreamingActivity`.
- **Cross-process trace IDs** (server-side event ↔ viewer-side event for the same OPEN_STREAM) are not implemented in v1. The user-facing pairing is via `streamId`, which is already shared.
- **OpenTelemetry / structured tracing** was considered (approach C) and rejected as overkill for a personal-project scope.

## Test coverage

- Unit tests for the reducer: initial state → transitions through each event type → terminal error state.
- Unit tests for `InAppDashboardSink` (server) / `InAppBufferTree` (viewer): bounded ring buffer eviction, thread-safety under concurrent emit.
- Unit test for the file-rotation boundary using a mocked clock: emit on 2026-05-17, advance clock to 2026-05-18, emit again, verify two files exist and the old-than-7-days file was deleted.
- Integration test (server): start coordinator, inject a `WorkerSpawnFailed`, assert dashboard VM's state-board reflects the error within 1 s.
- Integration test (viewer): use Robolectric / instrumented test to verify `UdpStalled` fires after `StreamOpened` + 2 s with no `UdpFirstPacketReceived`.

## Migration of existing call sites

- `CoordinatorLauncher.ProbeCaptureSizeAsync` `Console.Error.WriteLine` → `Diagnostics.Report(new ProbeFailed(...))`.
- `CoordinatorLauncher` "serving on TCP …" banner → `Diagnostics.Report(new Listening(...))`.
- `CoordinatorLauncher` enumeration catch → `Diagnostics.Report(new EnumerationFailed(...))`.
- Existing viewer `Log.i/Log.e` for stream lifecycle in `UnifiedStreamingActivity`, `XrDemoActivity`, `MainActivity` (GXR), `MediaCodecDecoder`, `MultiStreamControlClient` get refactored: still `Log.i` for free-form info, but pipeline-stage transitions go through `Diagnostics.report(...)`.

## Dependencies to add

- Server: `Serilog`, `Serilog.Extensions.Logging`, `Serilog.Sinks.File`, `Serilog.Formatting.Compact`.
- Viewer: `com.jakewharton.timber:timber` (likely already present transitively; if not, add to `libs.versions.toml`).

## Risks

- **CompactJsonFormatter field naming** — Serilog's compact format uses `@t`, `@l`, `@mt` field names by default; my spec shows `ts`, `level`, `eventType`. Implementation needs to either accept Serilog defaults (and adjust `jq` examples) or use a custom formatter. **Decision deferred to implementation:** start with `CompactJsonFormatter`'s defaults to minimize custom code; if grep ergonomics suffer, write a thin custom formatter.
- **MAUI thread marshalling** — `InAppDashboardSink.Emit` is called from arbitrary threads; the dashboard VM raises `PropertyChanged` synchronously. Need `MainThread.BeginInvokeOnMainThread` around the VM update or the bindings will throw. Easy to get wrong — covered by an integration test.
- **GXR `SpatialPanel`** — Jetpack XR alpha04 is the version pinned because of the broken `createSplitEngineBridge` bug (see memory `project_gxr_jetpack_xr_alpha04_broken`). Need to verify `SpatialPanel` works on alpha04, and that adding a second panel doesn't trip a different ABI mismatch.
