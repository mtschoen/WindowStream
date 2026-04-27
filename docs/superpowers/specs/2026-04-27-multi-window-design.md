# Multi-Window v2 — Design

**Date**: 2026-04-27
**Status**: Approved (brainstorming phase complete)
**Author**: mtschoen
**Supersedes**: parts of `2026-04-20-multi-window-followups.md` ("One-server-many-streams" follow-up; "Firewall rule proliferation" follow-up). Builds on `2026-04-19-windowstream-design.md`.

## Vision

Treat each Windows window as a first-class object the viewer can open, close, pause, and focus independently. One server process per machine; one panel per window in the viewer. The current "N CLI processes for N windows" hack retires; firewall rules, mDNS instances, and TCP control collapse to one each per server.

The browser-tab analogy holds: one **coordinator process** owns discovery, control, and I/O; each active stream runs in its own **worker process** so a native FFmpeg crash takes down only that stream, not the whole server.

## v2 Scope

### In scope

- One coordinator process advertises all eligible windows on the host. Trust model unchanged from v1: trust the LAN, no auth, no encryption, anyone who connects can pick any eligible window.
- Viewer enumerates and picks; server captures and encodes only the selected windows.
- One worker process per active stream — independent WGC + NVENC pipeline; native crash isolation.
- Protocol-level multiplexing: one TCP control connection, one UDP video socket on the server, multiple streams discriminated by the existing `streamId` packet header.
- Pause / resume per stream (viewer-driven; pause = stop encoding, capture stays warm, viewer freezes on last decoded frame).
- Focus relay: viewer signals "this panel is focused" → coordinator brings underlying HWND to foreground and routes input there.
- Coordinator-side load shedding: drop pre-fragment encoded chunks under pressure (queue depth, send-buffer back-pressure).
- NVENC session cap enforced at the protocol layer with `ERROR{ENCODER_CAPACITY}`.
- Portable Android viewer flavor end-to-end on the new protocol.
- Clean wire-format break: bump protocol `version=2`; v1 viewers rejected with `VERSION_MISMATCH`.

### Out of scope (deferred to follow-up specs)

- GXR multi-panel scene (separate `WindowStreamScene` design once portable lands).
- Encoder pooling / sharing for unfocused streams ("only the focused one needs a dedicated encoder").
- Multiple viewers per server (v1's one-viewer rule inherits).
- Server-side per-stream approval gate ("PC user confirms each new stream").
- Window allowlist on the server side.
- Window pose persistence across reconnects.
- Adaptive bitrate, codec negotiation, software fallback.
- Server-side single picker UX changes — `WindowStreamServer` MAUI app stays as-is in v2; the CLI is the production server.

### Known limitations carried forward from v1

All v1 limitations apply unless explicitly retired below. Notably retained: minimized/occluded windows still don't stream; DRM content still streams as black; NVENC still required; FFmpeg DLLs still come from OBS or manual placement.

### Limitations retired by v2

- "v1 hard-codes one active stream and rejects additional streams from the same connection" — removed; multi-stream is the headline feature.
- "Firewall rule proliferation" — removed; one TCP + one UDP rule per server install, not per session.
- "mDNS instance name collision when N servers run on one host" — removed; one server per host.

## Architecture

```
┌──────────────────────── Windows PC ─────────────────────────┐
│                                                              │
│   ┌────────────────── Coordinator process ───────────────┐   │
│   │  windowstream serve                                  │   │
│   │                                                      │   │
│   │  • mDNS advertise (one instance per machine)         │   │
│   │  • TCP control listener (one socket)                 │   │
│   │  • UDP video sender    (one socket)                  │   │
│   │  • Window enumerator (EnumWindows poll @ 500ms)      │   │
│   │  • Worker supervisor (spawn / restart / kill)        │   │
│   │  • Stream router      (streamId → pipe → wire)       │   │
│   │  • Focus relay        (SetForegroundWindow + input)  │   │
│   │  • Load shedder       (drop chunks under pressure)   │   │
│   └─┬────────────────────────────────────────────────┬───┘   │
│     │ named pipe                       named pipe   │       │
│     │ \\.\pipe\windowstream-<pid>-1   ...-<pid>-N   │       │
│     ▼                                               ▼       │
│   ┌──────────────────┐                  ┌──────────────────┐│
│   │ Worker process 1 │                  │ Worker process N ││
│   │ windowstream     │       . . .      │ windowstream     ││
│   │   worker         │                  │   worker         ││
│   │ • WGC capture    │                  │ • WGC capture    ││
│   │ • NVENC encode   │                  │ • NVENC encode   ││
│   │ • emit chunks    │                  │ • emit chunks    ││
│   │   over pipe      │                  │   over pipe      ││
│   └──────────────────┘                  └──────────────────┘│
└──────────────────────────────────────────────────────────────┘
                               │ TCP control (JSON)
                               │ UDP video   (multiplexed by streamId)
                               ▼
                ┌─────────────── Viewer ───────────────┐
                │  one ControlClient                   │
                │  one UdpTransportReceiver            │
                │   ├── decoder for streamId 1         │
                │   ├── decoder for streamId 2         │
                │   └── decoder for streamId N         │
                │  one panel/surface per stream        │
                └──────────────────────────────────────┘
```

### Key design decisions

- **Coordinator + worker isolation.** Native FFmpeg / NVENC crashes are unrecoverable in .NET (no AppDomains in .NET 5+). Per-stream worker processes contain the blast radius. Coordinator survives any worker death and emits a clean `STREAM_STOPPED{ENCODER_FAILED}` to the viewer.
- **Coordinator owns all I/O and policy.** Workers are dumb capture+encode pumps. They don't see sockets, don't talk to other workers, don't make policy decisions. Pause, focus, load-shedding, capacity gating, restart all live in the coordinator.
- **Single TCP + single UDP per server.** Protocol header's `streamId` does the multiplex. Retires the v1 firewall-rule sprawl. Retires the `MachineName-<TcpPort>` mDNS name discriminator added in commit `d685fc5`.
- **Worker → coordinator IPC = named pipe.** Length-prefixed `(ptsUs, flags, payload)` frames at ~60 chunks/sec/stream. Negligible latency cost; far simpler than shared-memory ring buffers. Swap-out path to shared memory exists if profiling ever fingers the pipe.
- **Push-based window enumeration with pull as escape hatch.** Server pushes `WINDOW_ADDED`/`WINDOW_REMOVED`/`WINDOW_UPDATED` deltas; viewer can also force-refresh with `LIST_WINDOWS`. Diff is computed against a 500ms `EnumWindows` poll; `SetWinEventHook` may replace polling later if profiling justifies it.
- **Server-assigned opaque `windowId`.** Stable for the life of an HWND; never reused. HWND, PID, process name, title travel as descriptive fields. Viewer pins picker selections to `windowId`. Stream IDs are a *separate* monotonic counter assigned at `OPEN_STREAM` time.
- **Pause = freeze last frame, viewer-driven, encode-only.** Viewer sends `PAUSE_STREAM` when its UI considers a panel inactive. Coordinator forwards to worker; worker stops calling `EncodeAsync` on captured frames. WGC stays attached so resume is one-frame-fast (`RESUME_STREAM` triggers an immediate IDR). Decoder on the viewer holds the last frame.
- **Focus relay = explicit `FOCUS_WINDOW` + tagged `KEY_EVENT`.** Viewer signals focus changes; `KeyEventMessage` carries `streamId`. Coordinator runs the `AttachThreadInput` dance to defeat focus-stealing prevention before `SetForegroundWindow`, then `SendInput`. On `KEY_EVENT` whose `streamId` doesn't match current foreground, coordinator re-focuses then sends (logs warning).
- **Hard version break.** v2 viewers and v2 server only. v1 viewer connecting to v2 server gets `ERROR{VERSION_MISMATCH}`. The new protocol is incompatible enough that compatibility shims are dead weight.
- **Portable viewer first.** GXR multi-panel work blocked on the alpha04 launcher crash and on the spatial-UX rethink that comes with N free-floating panels. Portable platform is the iteration surface; GXR catches up in a follow-up spec.

## Components

### Coordinator (`windowstream serve`)

Replaces today's CLI invocation. New CLI shape:

```
windowstream serve                       # advertise + wait for viewer; pick happens viewer-side
windowstream worker --hwnd <h>           # internal — spawned by coordinator; not invoked by users
                    --stream-id <n>
                    --pipe-name <p>
                    --encoder-options <json>
windowstream list                        # unchanged — local enumeration for debugging
```

Today's `serve --hwnd <handle>` and `serve --title-matches <pattern>` retire. The CLI subcommand surface for v1 dev-mode auto-open (`--auto-stream <hwnd>` shortcut that pre-opens a stream when the viewer connects) is captured as a v2.x followup, not in v2.0.

Coordinator components:

- **`WindowEnumerator`** (new, `WindowStream.Core.Capture.Windows.WindowEnumerator` extends today's static helper). 500ms timer ticks; calls `EnumWindows` with v1's filter set. Diffs against last snapshot keyed by HWND. Emits `WindowEvent` (Added/Removed/Updated) on a channel. Assigns monotonic `WindowIdentifier` (uint64) on first observation. WindowId stays stable across title changes for the same HWND; a destroyed-then-recreated HWND gets a fresh id.
- **`WorkerSupervisor`** (new, `WindowStream.Core.Hosting.WorkerSupervisor`). API: `Task<StreamHandle> StartStreamAsync(WindowIdentifier, EncoderOptions, CancellationToken)`, `Task StopStreamAsync(StreamIdentifier)`, `event StreamEnded`. Uses `IWorkerProcessLauncher` abstraction so unit tests substitute a fake. Tracks `Process.Exited`; on unexpected exit, raises `StreamEnded(streamId, EncoderFailed)`. Enforces `MaximumConcurrentStreams` (default 8); refuses past cap with a typed exception that maps to `ENCODER_CAPACITY`.
- **`StreamRouter`** (new, `WindowStream.Core.Hosting.StreamRouter`). One `NamedPipeServerStream` reader task per worker. Reads framed `EncodedChunk`, tags with stream's `streamId`, hands to `LoadShedder`. Owns the named-pipe protocol.
- **`LoadShedder`** (new, `WindowStream.Core.Hosting.LoadShedder`). Sits between router and `NalFragmenter`. Watches per-stream pipe queue depth + UDP-send back-pressure signal; when threshold tripped, drops the oldest non-keyframe chunk for the offending stream. Tunable thresholds; concrete values land in implementation, not in this spec.
- **`ControlServer`** (new, replaces today's accept loop in `SessionHost`). Single TCP listener. One viewer at a time (v1 `VIEWER_BUSY` rule inherits). Handles HELLO/SERVER_HELLO, viewer endpoint registration via `VIEWER_READY`, heartbeat, and per-stream control routing.
- **`FocusRelay`** (new, `WindowStream.Core.Session.Input.FocusRelay`). Methods: `BringToForeground(HWND)`, `RouteKeyEvent(StreamIdentifier, KeyEventMessage)`. The `BringToForeground` implementation: `GetWindowThreadProcessId(target)` + `GetWindowThreadProcessId(GetForegroundWindow())` → `AttachThreadInput(srcThread, targetThread, true)` → `SetForegroundWindow(target)` → `AttachThreadInput(..., false)`. Logs success/failure. `RouteKeyEvent` validates current foreground HWND matches `streamId`'s HWND; mismatch triggers a re-focus attempt before `Win32InputInjector.InjectKey`.
- **`ServerAdvertiser`** (existing). mDNS instance name reverts to `Environment.MachineName` (drop the `-<TcpPort>` suffix from `SessionHostLauncherAdapter`). TXT records: `version=2`, `hostname=<MachineName>`, `protocolRev=0`. Service type unchanged: `_windowstream._tcp.local.`

### Worker (`windowstream worker`)

New CLI subcommand. Windows-only (uses WGC). Lifecycle:

1. Parse argv: `--hwnd`, `--stream-id`, `--pipe-name`, `--encoder-options <json>`.
2. Connect named pipe (client) to coordinator. Pipe must be ready when worker is spawned — coordinator opens server side first, hands name on argv.
3. Configure `FFmpegNvencEncoder` with the provided `EncoderOptions` (probe already done by coordinator).
4. Attach `WgcCaptureSource` to the HWND.
5. Drain `WgcCaptureSource.Frames` → `FFmpegNvencEncoder.EncodeAsync` → write `EncodedChunk` over pipe (frame format: `uint32 length | uint64 ptsUs | uint8 flags | byte[] nalUnit`).
6. Read coordinator-→-worker control frames (pause/resume/keyframe-request) on the same pipe (bidirectional). Frame format: `uint8 commandTag | optional payload`. Commands in v2.0: `Pause`, `Resume`, `RequestKeyframe`, `Shutdown`.
7. On `Shutdown` or pipe close (coordinator gone), tear down cleanly. Exit codes: `0` = clean, `1` = encoder failure, `2` = capture failure, `3` = unexpected exception.

Worker code largely reuses today's `SessionHost` capture+encode pump with the TCP/UDP/heartbeat logic stripped out. The capture pump and encoder live in `WindowStream.Core` (Windows TFM) where they already are; the worker-specific entry point and IPC plumbing land in the existing `WindowStream.Cli` project as a new subcommand. One CLI binary, three verbs (`serve`, `worker`, `list`).

### Worker ↔ coordinator IPC

New module `WindowStream.Core.Hosting`:

- **`WorkerChunkPipe`** — typed bidirectional wrapper around `NamedPipeServerStream` (coordinator side) / `NamedPipeClientStream` (worker side).
- **Pipe naming**: `\\.\pipe\windowstream-<coordinatorPid>-<streamId>`. PID prefix avoids collisions if multiple coordinators ever run on the same machine (dev iteration).
- **Worker → coordinator frames** (encoded chunks):

  | Field | Size | Meaning |
  |---|---|---|
  | `length` | 4 | `uint32` BE — payload byte count |
  | `ptsUs` | 8 | `uint64` BE — presentation timestamp (microseconds) |
  | `flags` | 1 | bit 0 = IDR; bits 1–7 reserved |
  | `payload` | `length` | NAL unit bytes (Annex-B framed) |

- **Coordinator → worker frames** (control commands):

  | Tag | Meaning | Payload |
  |---|---|---|
  | `0x01` | Pause | none |
  | `0x02` | Resume | none |
  | `0x03` | RequestKeyframe | none |
  | `0xFF` | Shutdown | none |

- **Backpressure**: pipe has a bounded buffer; if the coordinator is slow to drain, worker's pipe writes block. That's acceptable — load-shedding will catch up via the LoadShedder dropping stale chunks before they hit the wire.

### Viewer-side changes (portable)

- **`ControlClient`** — extended for multiplexed streams. One TCP connection serves all streams. Per-stream state lives in a map keyed by `streamId`.
- **`UdpTransportReceiver`** — emits `EncodedFrame { streamId, pts, payload }`. Demultiplex happens in `ViewerPipeline` which routes to the correct decoder.
- **`MediaCodecDecoder`** — unchanged; one instance per active stream, fed by the demultiplexed frame flow.
- **Picker UI** (replaces today's multi-server picker). Connect to one server; receive snapshot + push events; show window list with title + process name + thumbnail (if available; v2.0 = no thumbnail, just text). Multi-select to open. Replaces `ServerPickerScreen` and `ServerSelectionActivity`'s multi-server UX with a one-server-many-windows picker.
- **`DemoActivity`** (or its v2 successor) — drops the `GridLayout` of `SurfaceView`s. Replacement layout: one full-screen panel at a time, with a tab/swipe switcher across the top that lists active streams. Switcher tap = focus + active-input target. The exact UX details land in the implementation plan, not this spec.
- **Removed**: server-multi-select UI (`ServerSelectionActivity`'s parallel-array intent shape). The viewer talks to one server.
- **Renamed/added control messages** (full table in §Protocol).

### Followup parking lot

Captured here so the implementation plan doesn't grow them:

- GXR multi-panel scene (one `PanelEntity` per stream, gaze-driven focus signal).
- Encoder pooling / sharing for unfocused streams.
- Server-side per-stream approval gate (toast on the PC).
- Window allowlist.
- `serve --auto-stream <hwnd>` dev shortcut.
- `SetWinEventHook` replacement for `EnumWindows` polling.
- Multi-viewer support.
- Backpressure-driven dynamic bitrate.

## Data Flow

### Connect + initial enumeration

```
Viewer                                 Coordinator                       Workers
─────                                  ───────────                       ───────
                                       (running, no workers,
                                        WindowEnumerator polling)
TCP connect ──────────────────────────▶
                                       accept; one-viewer guard
HELLO {viewerVersion=2, capabilities} ▶
                                                                          (idle)
                                       ◀── SERVER_HELLO {serverVersion=2,
                                                         windows: [snapshot]}
VIEWER_READY {viewerUdpPort} ─────────▶
                                       register UDP endpoint for connection
                                       (subsequent enumerator ticks emit deltas)
                                       ◀── WINDOW_UPDATED {windowId, title}
                                       ◀── WINDOW_ADDED {windowId, hwnd, ...}
                                       ◀── WINDOW_REMOVED {windowId}
```

`SERVER_HELLO` carries the initial window snapshot. After that, the coordinator pushes deltas as `EnumWindows` diff produces them. Viewer never has to ask, but `LIST_WINDOWS` is available as a manual refresh hook.

### Open a stream

```
Viewer                                 Coordinator                       Worker
─────                                  ───────────                       ──────
OPEN_STREAM {windowId} ───────────────▶
                                       validate windowId still alive
                                       check NVENC capacity
                                       probe WGC dimensions (~50–200ms)
                                       allocate streamId
                                       open named pipe (server side)
                                       Process.Start(worker, argv...) ──▶
                                                                          connect pipe
                                                                          configure NVENC
                                                                          attach WGC
                                                                          first encoded chunk ──▶
                                       receive over pipe; route to UDP
                                       ◀── STREAM_STARTED {streamId,
                                                           windowId,
                                                           width, height,
                                                           framesPerSecond}
REQUEST_KEYFRAME {streamId} ──────────▶
                                       forward over pipe ──────────────▶
                                                                          force IDR
                                       ◀────── encoded IDR chunk over pipe
                                       fragment, UDP send to viewer
                                  ════ UDP NAL units (streamId in header) ══▶
                                  decoder hot, panel rendering
```

`VIEWER_READY` is sent exactly once, immediately after `HELLO`/`SERVER_HELLO`, before any `OPEN_STREAM`. The UDP endpoint registers for the duration of the TCP connection; all streams opened later reuse it.

### Pause / resume

```
Viewer                                 Coordinator                       Worker
─────                                  ───────────                       ──────
PAUSE_STREAM {streamId} ──────────────▶
                                       send Pause over pipe ───────────▶
                                                                          paused = true
                                                                          (capture continues,
                                                                           encode is skipped)
                                       (no UDP traffic for streamId
                                        until resumed)
RESUME_STREAM {streamId} ─────────────▶
                                       send Resume + RequestKeyframe ─▶
                                                                          paused = false
                                                                          force IDR on next
                                                                          captured frame
                                  ════ UDP NAL units resume ═══════════════▶
```

Viewer's panel for `streamId` holds its last decoded frame the entire time. v2.0 viewer overlays a "paused" hint (semi-transparent badge); concrete UX deferred to plan.

### Focus relay + input

```
Viewer                                 Coordinator
─────                                  ───────────
FOCUS_WINDOW {streamId} ──────────────▶
                                       lookup HWND for streamId
                                       AttachThreadInput → SetForegroundWindow
                                       → AttachThreadInput(detach)
                                       (logs success/failure)

KEY_EVENT {streamId, keyCode, ...} ──▶
                                       validate foreground HWND matches
                                          streamId's HWND
                                       if mismatch: re-focus, log warning
                                       Win32InputInjector.InjectKey(...)
```

Viewer is responsible for sending `FOCUS_WINDOW` whenever its UI focus changes. Portable flavor: tap-to-focus per panel (or tab-switcher selection). GXR (later spec): gaze-based.

### Stream close

Three close paths:

1. **Viewer-initiated**: `CLOSE_STREAM {streamId}`. Coordinator sends `Shutdown` over pipe, awaits exit, emits `STREAM_STOPPED {streamId, reason=ClosedByViewer}`.
2. **Window gone**: `WindowEnumerator` sees HWND removed. Coordinator finds active stream for that windowId (if any), kills the worker, emits `STREAM_STOPPED {streamId, reason=WindowGone}` to viewer plus `WINDOW_REMOVED {windowId}`.
3. **Worker died**: `Process.Exited` fires. Coordinator emits `STREAM_STOPPED {streamId, reason=EncoderFailed}` (or `CaptureFailed` based on exit code).

In all three cases, coordinator frees the streamId and removes the entry from the active-streams table. Viewer tears down its decoder for that streamId.

### Viewer disconnect

If TCP drops (heartbeat timeout or socket error), coordinator:
- Kills all active workers (`Shutdown` over pipe; force-kill after 2s).
- Frees all streamIds.
- Continues advertising on mDNS, accepts a new viewer connection.

A reconnecting viewer sees the full window snapshot fresh; previously-open streams do not auto-reopen.

## Protocol (Authoritative for v2)

### mDNS

- Service type: `_windowstream._tcp.local.` (unchanged)
- Instance name: `<MachineName>` (one per host; the `-<TcpPort>` discriminator retires)
- TXT records: `version=2`, `hostname=<MachineName>`, `protocolRev=0`

### TCP control messages

Framing unchanged (length-prefixed JSON). v2 message set:

| Type | Direction | Payload | Notes |
|---|---|---|---|
| `HELLO` | viewer → server | `{viewerVersion, displayCapabilities}` | First message; `viewerVersion=2` required |
| `SERVER_HELLO` | server → viewer | `{serverVersion, windows: WindowDescriptor[]}` | Response to HELLO; carries initial window snapshot |
| `LIST_WINDOWS` | viewer → server | `{}` | Optional manual refresh; server responds with `WINDOW_SNAPSHOT` |
| `WINDOW_SNAPSHOT` | server → viewer | `{windows: WindowDescriptor[]}` | Response to LIST_WINDOWS |
| `WINDOW_ADDED` | server → viewer | `{window: WindowDescriptor}` | Pushed when enumerator detects new window |
| `WINDOW_REMOVED` | server → viewer | `{windowId}` | Pushed when enumerator detects window gone |
| `WINDOW_UPDATED` | server → viewer | `{windowId, title?, width?, height?}` | Pushed on title or dimension change for existing window |
| `OPEN_STREAM` | viewer → server | `{windowId}` | Request capture + encode |
| `STREAM_STARTED` | server → viewer | `{streamId, windowId, codec, width, height, framesPerSecond, dpiScale?}` | Reply to OPEN_STREAM (or pushed for any other reason a stream becomes active) |
| `STREAM_STOPPED` | server → viewer | `{streamId, reason}` | Stream ended; reason ∈ {`ClosedByViewer`, `WindowGone`, `EncoderFailed`, `CaptureFailed`, `StreamHung`, `ServerShutdown`} |
| `CLOSE_STREAM` | viewer → server | `{streamId}` | Viewer-initiated close |
| `PAUSE_STREAM` | viewer → server | `{streamId}` | Stop encode; capture stays warm |
| `RESUME_STREAM` | viewer → server | `{streamId}` | Resume encode + force IDR |
| `FOCUS_WINDOW` | viewer → server | `{streamId}` | Bring HWND to foreground |
| `KEY_EVENT` | viewer → server | `{streamId, keyCode, isUnicode, isDown}` | `streamId` added in v2 |
| `VIEWER_READY` | viewer → server | `{viewerUdpPort}` | Sent once, immediately after `HELLO`. Registers the UDP endpoint for all subsequently-opened streams; `streamId` field from v1 retires |
| `REQUEST_KEYFRAME` | viewer → server | `{streamId}` | Forced IDR for that stream |
| `HEARTBEAT` | both | `{}` | Unchanged |
| `ERROR` | both | `{code, message}` | Unchanged framing; new codes below |

`WindowDescriptor`:

```jsonc
{
  "windowId": 42,                  // server-assigned, stable for HWND lifetime
  "hwnd": 1574208,                 // descriptive; may be reused after WINDOW_REMOVED
  "processId": 9876,
  "processName": "devenv",         // process name without extension
  "title": "Solution1 - Microsoft Visual Studio",
  "physicalWidth": 1920,           // last-observed physical client size
  "physicalHeight": 1080
}
```

### Error codes

v1 codes inherit; new codes:

- `ENCODER_CAPACITY` — server is at NVENC session cap; viewer should close another stream and retry.
- `WINDOW_NOT_FOUND` — `windowId` referenced by viewer is no longer alive.
- `STREAM_NOT_FOUND` — `streamId` referenced by viewer is not active.

### UDP video channel

Packet format unchanged from v1 (the `streamId` field already exists; v2 just stops hardcoding it to 1).

## Error Handling

Inherits v1's "fail loudly, recover manually" philosophy. Worker isolation creates new failure modes worth calling out.

### Worker failures

| Failure | Detection | v2 response |
|---|---|---|
| Worker process exits non-zero | `Process.Exited` + exit code | `STREAM_STOPPED{streamId, EncoderFailed/CaptureFailed}` to viewer; coordinator keeps running; sibling streams unaffected |
| Worker hangs (pipe writes stop) | per-stream watchdog, 5s no-frames threshold | force-kill worker; emit `STREAM_STOPPED{StreamHung}`; the new error reason joins the list above |
| Worker pipe disconnects unexpectedly | `IOException` on read | treat as worker-died; force-kill if process still running |
| Coordinator fails to spawn worker | `Process.Start` exception | `ERROR{ENCODE_FAILED}` to viewer; no streamId allocated |

Auto-restart on worker death is **not** in v2.0. Practical reason: most worker deaths are deterministic (odd-height window crashes the same way every time). Viewer-driven retry — close + reopen — is honest and lets the user see what happened.

### Window enumeration failures

| Failure | Detection | v2 response |
|---|---|---|
| `EnumWindows` returns empty / fails | enumerator try/catch | log; keep retrying on the next tick |
| HWND races (window destroyed between LIST and OPEN) | `WgcCaptureSource.Start` throws | `STREAM_STOPPED{WindowGone}` immediately; viewer should refresh its list |

### Capacity gating

| Failure | Detection | v2 response |
|---|---|---|
| Viewer requests OPEN_STREAM at NVENC cap | `WorkerSupervisor.MaximumConcurrentStreams` check | `ERROR{ENCODER_CAPACITY}` reply (no streamId allocated) |
| Worker fails NVENC init despite capacity check passing | worker exits with code 1 | `STREAM_STOPPED{EncoderFailed}`; the cap was wrong (driver state changed); coordinator does not auto-decrement |

### Network failures

Inherit v1 handling. New: when TCP drops with N active streams, coordinator must tear down all N workers, not just one.

## Testing

Coverage gate stays 100% line + branch on `WindowStream.Core` (and on the new `WindowStream.Core.Hosting` module). Coverlet config in `Directory.Build.props` already enforces this — new code must keep the gate green.

### Unit tests (`WindowStream.Core.Tests`)

- **Protocol round-trip** — every new message type, every new error code, every new `STREAM_STOPPED` reason.
- **`WindowEnumerator` diff logic** — feed sequences of `EnumWindows` snapshots, assert correct add/remove/update event sequence + correct `windowId` assignment + windowId-stability across title changes.
- **`WorkerSupervisor`** — using a fake `IWorkerProcessLauncher`, exercise: spawn, capacity gate, stop, unexpected exit, hang detection, viewer-disconnect mass-stop.
- **`StreamRouter`** — pipe protocol round-trip; multi-stream demux; partial-frame handling.
- **`LoadShedder`** — threshold trips drop oldest non-keyframe; keyframes never dropped; idle streams unaffected.
- **`FocusRelay`** — using a mockable Win32 layer, assert `AttachThreadInput` dance ordering; assert `KEY_EVENT` mismatch path triggers re-focus.
- **`WorkerChunkPipe`** — frame encode/decode round-trip, both directions; partial-read handling on coordinator side.

### Integration tests (`WindowStream.Integration.Tests`, Windows TFM)

- **End-to-end multi-stream loopback** — coordinator + 2 fake workers (substituting `IWorkerProcessLauncher` to spawn an in-process worker for test speed) + fake viewer harness over TCP/UDP localhost. Open 2 streams; verify both render decoded frames; close one; verify the other unaffected.
- **Real-process worker round-trip** — coordinator spawns the actual `windowstream worker` binary against a Notepad HWND; verify chunks flow over named pipe; clean shutdown via Shutdown command. Inherits the v1 Notepad-PID-snapshot cleanup pattern.
- **Worker crash recovery** — kill an active worker mid-stream; assert `STREAM_STOPPED{EncoderFailed}` reaches viewer; assert sibling stream(s) unaffected; assert streamId is freed for reuse.
- **NVENC capacity rejection** — set `MaximumConcurrentStreams=1`; open one stream; attempt second; assert `ERROR{ENCODER_CAPACITY}`.
- **Pause / resume keyframe** — open stream, pause, verify no UDP traffic for that streamId for 2s, resume, verify next packet is IDR (flag bit 0 set).
- **Focus relay** — open two notepad streams (different titles); send `FOCUS_WINDOW` then `KEY_EVENT`; assert input lands in the focused notepad (read its title or text via `User32` API).

### Viewer tests (Kotlin)

- **Protocol parsing** — mirror server's round-trip on the new message set.
- **Multiplex demux** — UDP receiver hands `EncodedFrame {streamId}` to the right decoder; test with synthetic packets carrying multiple streamIds interleaved.
- **Picker delta logic** — feed `WINDOW_ADDED`/`WINDOW_REMOVED`/`WINDOW_UPDATED` sequences; assert UI state matches.

### Manual acceptance

Inherits v1's checklist plus:

- [ ] Open 3 streams simultaneously; close one; other two unaffected.
- [ ] Pause one stream; verify it freezes on last frame, others stay live.
- [ ] Focus stream A then B; type into a known editor in B; characters land in B's window.
- [ ] Kill `windowstream worker` for one stream via Task Manager; viewer sees `STREAM_STOPPED`; sibling streams unaffected.
- [ ] Open a window known to crash WGC (Task Manager — odd height); single stream dies; sibling streams unaffected; can close + reopen unrelated streams.

## Migration Notes

The retirement of v1 single-stream code is part of v2 — v1 and v2 do not coexist in the same binary.

Files retired or substantially rewritten:

- `src/WindowStream.Core/Session/SessionHost.cs` — capture/encode pump moves into worker; TCP/UDP/heartbeat moves into coordinator's `ControlServer`.
- `src/WindowStream.Cli/Hosting/SessionHostLauncherAdapter.cs` — replaced by coordinator's worker-spawn path. Probe logic moves into the coordinator's `OPEN_STREAM` handler.
- `src/WindowStream.Cli/Commands/ServeCommandHandler.cs` — `--hwnd` and `--title-matches` flags retire.
- `src/WindowStream.Core/Protocol/ServerHelloMessage.cs` — `ActiveStream` field removed; `Windows` list added.
- `src/WindowStream.Core/Protocol/StreamStartedMessage.cs` — `WindowId` field added; `StreamStartedMessage` becomes a runtime-pushed message rather than a one-shot at session start.
- `src/WindowStream.Core/Protocol/StreamStoppedMessage.cs` — `Reason` field added.
- Viewer: `ServerSelectionActivity` and `ServerPickerScreen` rewritten as a one-server-many-windows picker. `DemoActivity` grid layout retires.

## Open Questions

None from brainstorming. Implementation may surface additional questions; record them in the implementation plan as follow-ups.
