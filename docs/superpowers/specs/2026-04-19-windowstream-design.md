# WindowStream — v1 Design

**Date**: 2026-04-19
**Status**: Approved (brainstorming phase complete)
**Author**: mtschoen

## Vision

WindowStream is a Windows → HMD window-streaming tool. Each individual operating-system window becomes a first-class XR object, rendered as a panel in the headset's three-dimensional space. v1 streams from Windows to Samsung Galaxy XR (Android XR).

The broader project this belongs to is a distributed peripheral mesh — Synergy's model generalized to screens, HMDs, audio, controllers, and input devices. v1 is deliberately scoped to one use-case slice (Windows window → Galaxy XR panel) to prove the video pipeline end-to-end before the mesh vision expands.

The design explicitly avoids premature mesh abstractions. Concrete types for concrete things; extensibility arrives through additive protocol messages, not a speculative `Host`/`Endpoint`/`StreamChannel` framework.

## v1 Scope

### In scope

- One Windows window streamed at a time to one Galaxy XR viewer
- Windows-side picker GUI (`WindowStreamServer`, .NET MAUI)
- Windows-side CLI (`WindowStream.Cli`) for headless operation
- Android XR Kotlin application (`WindowStreamViewer`) using Jetpack XR panel subspace
- LAN-only; mDNS / Network Service Discovery; no authentication or encryption
- Output-only: no input relay back to Windows. v1 relies on operating-system passthrough (physical keyboard + mouse, Bluetooth peripherals paired to the PC). The HMD participates in the currently-logged-in Windows session — this is not Remote Desktop.
- Panel placed at a default pose in the HMD's world space; user repositions with hand drag (Jetpack XR built-in gesture)
- Reconnect supported; panel pose is not persisted across sessions

### Out of scope for v1 (future slices)

- Multiple simultaneous streamed windows
- Multiple viewers connected at once (second connection is rejected)
- Input relay (mouse/keyboard from HMD back to Windows, virtual keyboard, speech-to-text)
- Virtual-monitor spawning as a workaround for minimized/occluded windows that stop rendering
- Non-Windows capture (macOS, Linux)
- Non-Galaxy-XR clients (Vision Pro, Quest, SteamVR, Big Screen Beyond, legacy HMDs)
- Audio streaming
- DRM-aware rendering or protected-content detection
- Authentication, encryption, non-LAN operation
- Window pose persistence across reconnects
- Adaptive bitrate or codec negotiation beyond a fixed H.264 profile
- Automatic reconnection beyond one backoff attempt

## Known Limitations (deliberate, documented)

These are acceptable for v1 proof-of-concept; later slices address them.

1. **Minimized or occluded windows do not stream.** Windows stops rendering them. The virtual-monitor workaround is planned for v2.
2. **DRM-protected content streams as a black panel.** No automatic detection in v1.
3. **One viewer per server.** A second connection receives `ERROR{VIEWER_BUSY}`.
4. **Panel pose is not persisted.** Reconnect places the panel back at the default pose.
5. **No authentication or encryption.** Any viewer on the LAN may connect.
6. **NVENC is required.** No libx264 software fallback; encoder initialization failure is fatal.
7. **Static windows emit only one frame.** Windows Graphics Capture produces frames on content-change events; a static window (e.g. a Notepad with no cursor / typing) renders once and then silently delivers nothing more. Demo-verified on Windows 11. v1.x fix either enables cursor capture (`CaptureOptions.includeCursor = true`) or calls `RedrawWindow` on a timer. Active-content windows (terminals, video players, anything with cursors or animation) are unaffected.
8. **PC-side window resize is not yet adaptive.** Encoder dimensions are locked at session start via the probe pattern. Resizing the captured window on the PC causes `sws_scale` to mismatch and the capture pump to die silently. v1.x fix: a supervisor that detects dimension change on `CapturedFrame` and restarts the encode pipeline.
9. **FFmpeg native DLLs are not bundled.** v1 copies them from OBS Studio (`$ProgramFiles\obs-studio\bin\64bit\`) when present. Machines without OBS set `WINDOWSTREAM_SKIP_NVENC=1` to skip or must place the DLLs manually. v1.x fix: an MSBuild target that downloads from BtbN's FFmpeg-Builds.
10. **Windows Firewall rules must be added as admin on first run.** TCP + UDP inbound for the CLI's listener ports. `windowstream.exe Allow` rules created by the first-run UAC prompt may not cover OS-assigned ports; explicit port-range rules work. Tracked for an installer / setup script.
11. **Galaxy XR radio parks while off-head.** The HMD's Wi-Fi stack appears to idle when the proximity sensor reports "off face," even though `dumpsys wifi` reports CONNECTED. Symptom: no packets flow to or from the device. Workaround: wear it or block the proximity sensor. Not our bug; documented for future troubleshooting.

## Architecture

```
┌─────────────────────────── Windows PC ───────────────────────────┐
│                                                                   │
│  ┌───────────────────────────┐    ┌──────────────────────┐        │
│  │ WindowStreamServer (MAUI) │    │ WindowStream.Cli     │        │
│  │ - Window picker UI        │    │ - Headless smoke     │        │
│  │ - Session control         │    │   testing            │        │
│  └─────────────┬─────────────┘    └─────────┬────────────┘        │
│                │                            │                     │
│                └─────────────┬──────────────┘                     │
│                              ▼                                    │
│              ┌──────────────────────────────┐                     │
│              │ WindowStream.Core (lib)      │                     │
│              │ multi-targeted:              │                     │
│              │   net8.0                     │                     │
│              │   net8.0-windows10.0.19041.0 │                     │
│              │   net8.0-maccatalyst         │                     │
│              │ - IWindowCaptureSource (WGC) │                     │
│              │ - IVideoEncoder (FFmpeg)     │                     │
│              │ - Protocol types             │                     │
│              │ - Session state              │                     │
│              │ - Discovery (mDNS advertise) │                     │
│              └──────────────┬───────────────┘                     │
│                             │                                     │
└─────────────────────────────┼─────────────────────────────────────┘
                              │
                   ┌──────────┴──────────┐
                   │  TCP control (JSON) │
                   │  UDP video (H.264)  │
                   │  mDNS discovery     │
                   └──────────┬──────────┘
                              │
┌─────────────────────────────┼─────────────────────────────────────┐
│                             ▼                   Galaxy XR         │
│              ┌──────────────────────────────┐                     │
│              │ WindowStreamViewer (Kotlin)  │                     │
│              │ - NSD discovery client       │                     │
│              │ - TCP control client         │                     │
│              │ - UDP video receiver         │                     │
│              │ - MediaCodec decoder         │                     │
│              │ - Jetpack Compose for XR     │                     │
│              │   SpatialExternalSurface /   │                     │
│              │   PanelEntity per stream     │                     │
│              └──────────────────────────────┘                     │
└───────────────────────────────────────────────────────────────────┘
```

### Key design decisions

- **Core library is multi-targeted**. A single `WindowStream.Core` project with target frameworks `net8.0`, `net8.0-windows10.0.19041.0`, and `net8.0-maccatalyst`. Platform-specific capture implementations live behind `#if WINDOWS` / `#if MACCATALYST` guards. The portable core (protocol, session, FFmpeg encoder, mDNS) compiles into every target framework with no guards.
- **UI is optional**. Both `WindowStreamServer` (MAUI GUI) and `WindowStream.Cli` consume the core library. Running the server headlessly is a first-class mode, not a degraded one.
- **Viewer is a separate codebase** in Kotlin. The protocol is reimplemented on the Kotlin side. This intentional duplication keeps the Android application from growing a .NET-for-Android dependency tail; the protocol is small enough that two implementations cost little.
- **Transport is plain UDP + TCP**. WebRTC and QUIC remain available as future upgrades when LAN operation outgrows their lack of built-in congestion control. For v1, the simplest thing that could work.
- **Protocol supports multiple streams; v1 implementation exercises one**. Messages carry a `streamId` field. v1 hard-codes one active stream and rejects additional viewers. The shape is correct for future expansion without the implementation cost now.

## Components

### Server side (.NET)

**`WindowStream.Core`** — single class library, multi-targeted
- **What**: protocol types, session state, mDNS advertise, platform-abstracted capture and encode interfaces, Windows capture implementation, FFmpeg encoder implementation.
- **Target frameworks**: `net8.0`, `net8.0-windows10.0.19041.0`, `net8.0-maccatalyst`
- **Interfaces**:
  - `IWindowCaptureSource` — enumerate windows (`IEnumerable<WindowInfo> ListWindows()`), create captures (`IWindowCapture Start(WindowHandle, CaptureOptions)`), emit frames (`IAsyncEnumerable<CapturedFrame> Frames`)
  - `IVideoEncoder` — configure (`Configure(EncoderOptions)`), push frames (`EncodeAsync(CapturedFrame)`), pull encoded output (`IAsyncEnumerable<EncodedChunk>`)
  - `Session` — owns the capture→encode→transport pipeline for one streamed window; lifecycle `Start`/`Stop`
  - `ServerAdvertiser` — mDNS service publication on `_windowstream._tcp.local.` with TXT records
  - Protocol message types + serializers (`System.Text.Json` in v1)
- **Platform-specific implementations** (behind `#if WINDOWS`):
  - `WgcCaptureSource : IWindowCaptureSource` — `Windows.Graphics.Capture` via CsWinRT; maps HWND → `GraphicsCaptureItem` → `Direct3D11CaptureFramePool`
  - `WindowEnumerator` — `User32.EnumWindows` with filters (visible, titled, non-system)
- **Portable implementations** (all target frameworks):
  - `FFmpegNvencEncoder : IVideoEncoder` — wraps FFmpeg.AutoGen, configured for `h264_nvenc` with low-latency preset + zerolatency tune; runs wherever FFmpeg detects a capable NVIDIA driver at runtime
- **Depends on**: FFmpeg.AutoGen, CsWinRT (Windows TFM only), Silk.NET or SharpDX (D3D11 interop, Windows TFM only)

**`WindowStreamServer`** — .NET MAUI application, Windows target in v1
- **What**: picker GUI, session controls, live-status pane (frames per second, bitrate, connected viewer)
- **Depends on**: `WindowStream.Core`

**`WindowStream.Cli`** — .NET console application
- **What**: headless operation for smoke testing and automation. Commands:
  - `windowstream list` — enumerate windows
  - `windowstream serve --hwnd <handle>` — start session on given HWND
  - `windowstream serve --title-matches <pattern>` — start session on first match
- **Depends on**: `WindowStream.Core`, System.CommandLine

### Client side (Kotlin / Android XR)

**`WindowStreamViewer`** — Android Studio project, single module, Kotlin + Jetpack Compose for XR
- **What**: discovers servers, connects, receives video, renders panels.
- **Internal packages**:
  - `discovery` — Android NSD (Network Service Discovery) client for `_windowstream._tcp`; exposes `Flow<ServerInformation>`
  - `control` — TCP client with kotlinx.serialization JSON; suspending request/response plus event flow
  - `transport` — UDP receiver, packet reassembly; exposes `Flow<EncodedFrame>`
  - `decoder` — MediaCodec in async callback mode; emits decoded frames into a `FrameSink` abstraction (production binding: the panel's input surface via `XrPanelSink`; test binding: a plain `SurfaceTexture` via `TextureSink`). Handles SPS/PPS parsing and routes keyframe requests to `control`. The `FrameSink` boundary keeps the decoder pipeline testable on a standard Android emulator without the XR runtime.
  - `xr` — Jetpack Compose for XR. Provides `XrPanelSink : FrameSink` wrapping `SpatialExternalSurface`. One `PanelEntity` per active stream; default world pose is 1.5 meters forward, eye-height, slightly angled toward the viewer. OS-provided grab and drag gesture is enabled.
  - `app` — Compose entry point, service lifecycle, wires everything together
- **Depends on**: Jetpack XR SDK, Jetpack Compose for XR, kotlinx-coroutines, kotlinx-serialization

### Shared (documentation only, no code)

**`docs/protocol.md`** — authoritative wire format specification. Both implementations reference this document. Kept in the repository root so changes are visible as commits.

## Data Flow

### Happy-path sequence

```
Windows PC (Server)                         Galaxy XR (Viewer)
─────────────────────                       ──────────────────
boot
  ├─ mDNS advertise                           boot
  │  _windowstream._tcp                        ├─ NSD listener populates
  │  port=T, version=1                         │  list of discovered servers
  │                                            │
user picks window in GUI
  ├─ WGC attach to HWND
  ├─ NVENC configure
  ├─ UDP socket bound (port U)
  ├─ encoder hot, frames hit
  │  the void                              user taps server in Viewer
  │                                ──TCP──▶   ├─ open TCP control
  │                                           │
  │                                      ◀── HELLO {viewerVersion, capabilities}
  │                                ──TCP──▶
SERVER_HELLO {serverVersion,
  activeStream: {streamId, udpPort=U,
    codec=h264, width, height, framesPerSecond}}
  │                                           ├─ open UDP receiver
  │                                           ├─ configure MediaCodec
  │                                      ◀── REQUEST_KEYFRAME
  ├─ encoder flush + force IDR
  ├─ emit SPS/PPS + IDR NAL units
  │                           ════UDP batch════▶
  │                                           ├─ feed MediaCodec
  │                                           ├─ output Surface →
  │                                           │  SpatialExternalSurface
  │                                           ├─ PanelEntity placed at
  │                                           │  default pose
  │ (steady state)           ═══UDP frames═══▶ (steady state)
  │                                           ├─ user hand-grabs panel,
  │                                           │  drags in 3D (local only,
  │                                           │  zero server traffic)
  │
user closes server / picks new window
  ├─ encoder stop, WGC detach
  │                                ──TCP──▶
STREAM_STOPPED {streamId}
  │                                           ├─ close MediaCodec
  │                                           ├─ hide panel
```

### Channel summary

- **mDNS**: `_windowstream._tcp.local.`, TXT records `version=1`, `hostname=<human name>`, `protocolRev=1`. Server advertises; viewer listens.
- **TCP control**: JSON framed (length-prefixed, `uint32` big-endian + payload). Small message set; see Protocol section.
- **UDP video**: custom 24-byte packet header + NAL unit fragment payload. See Protocol section.

### Keyframe policy

- Default GOP length: 60 frames (~1 second at 60 frames per second).
- Force IDR on: viewer connect, `REQUEST_KEYFRAME` received, every N seconds as safety (configurable, default 2 seconds).
- SPS/PPS re-sent inline with each IDR so a mid-stream join decodes within one safety interval even without an explicit request.

### Reconnection / viewer lifecycle

- Viewer TCP disconnect: server continues capturing. No state torn down. Pixels resume hitting the void.
- Viewer returns: new TCP handshake, new `REQUEST_KEYFRAME`, stream resumes. Panel restored at default pose (pose persistence is explicitly out of scope for v1).
- v1 hard constraint: **one active viewer at a time**. Second connection receives `ERROR{VIEWER_BUSY}`.

### Latency budget (descriptive, not a pass/fail gate)

| Stage | Typical | Notes |
|---|---|---|
| WGC frame ready | ~frame interval | ~8 ms at 120 fps, ~16 ms at 60 fps |
| NVENC encode | 2–8 ms | low-latency preset, zerolatency tune |
| UDP send + receive | < 1 ms | LAN |
| MediaCodec decode | 16–50 ms | dominant term, 1–3 frame buffer |
| Panel composite | ~1 frame VSync | ~8–16 ms |

Rough ballpark: 40–90 ms motion-to-photon on a healthy LAN. v1 acceptance is "feels good enough to code in," not a specific millisecond number. The target workload is productivity applications (Fork, Visual Studio, Unity Editor for reading) — not motion-to-photon-sensitive content like Beat Saber.

## Protocol (Authoritative)

### mDNS service type

`_windowstream._tcp.local.`

TXT records:
- `version=1` — protocol major version
- `hostname=<human name>` — e.g., "mtsch-desktop"
- `protocolRev=1` — minor revision within major version

### TCP control channel

Port advertised by mDNS. Framing: `uint32` big-endian length prefix, then UTF-8 JSON payload.

v1 message types:

| Type | Direction | Payload | Notes |
|---|---|---|---|
| `HELLO` | viewer → server | `{viewerVersion, displayCapabilities}` | first message after connect |
| `SERVER_HELLO` | server → viewer | `{serverVersion, activeStream?}` | response to HELLO |
| `STREAM_STARTED` | server → viewer | `{streamId, udpPort, codec, width, height, framesPerSecond, dpiScale?}` | emitted when server picks a window; see DPI handling below |
| `STREAM_STOPPED` | server → viewer | `{streamId}` | emitted when user stops streaming or switches window |
| `REQUEST_KEYFRAME` | viewer → server | `{streamId}` | emitted on connect and on decoder reset |
| `VIEWER_READY` | viewer → server | `{streamId, viewerUdpPort}` | sent by viewer after binding its UDP receiver; server combines with TCP peer IP to register the outgoing video endpoint |
| `HEARTBEAT` | both | `{}` | 2-second interval; 6-second silence terminates connection |
| `ERROR` | both | `{code, message}` | fatal to session in v1 |

v1 error codes:
- `VERSION_MISMATCH` — protocol versions differ
- `VIEWER_BUSY` — server already has a connected viewer
- `WINDOW_GONE` — captured window no longer exists
- `CAPTURE_FAILED` — WGC could not attach
- `ENCODE_FAILED` — encoder initialization or operation failed
- `MALFORMED_MESSAGE` — received message could not be parsed

### Viewer endpoint registration

TCP gives the server the viewer's IP; `VIEWER_READY` gives it the viewer's UDP port. On receipt, the server registers `new IPEndPoint(tcpPeerAddress, viewerUdpPort)` as the destination for video packets. Without this message, the server has no way to address UDP output and silently drops encoded chunks. Viewers MUST emit `VIEWER_READY` after binding their UDP receiver; the first `REQUEST_KEYFRAME` may be sent immediately after so the server emits an IDR that arrives at a known endpoint.

### DPI handling

`width` and `height` in `STREAM_STARTED` are **physical pixel dimensions of the encoded H.264 stream** — exactly what the decoder produces. The server is responsible for reading the source window's DPI (`GetDpiForWindow` on Windows) and configuring the encoder to match WGC's physical output; viewers never compute DPI from logical dimensions.

**Implementation note (hard-won):** `GetWindowRect` and `GetClientRect` both give results that differ from WGC's actual captured frame size by a few pixels (window chrome, shadows, DPI rounding). The server MUST probe WGC for one frame, read the actual `CapturedFrame.widthPixels`/`heightPixels`, and use those dimensions to configure the encoder. Then align DOWN to even for NVENC's NV12 requirement — aligning up causes `sws_scale` to read past the source stride and either crash or hang.

`dpiScale` is an optional informational float (e.g. `1.0`, `1.25`, `1.5`) reporting the source monitor's DPI divided by 96. Viewers may use it to pick a reasonable panel size in meters (a small logical window should be a small XR panel) but are not required to. If absent, viewers pick any sensible default.

DPI is a cross-platform squishy problem — Windows' per-monitor-v2 manifesting, macOS' backing-scale-factor on Retina, GTK/Qt/MAUI/WinForms each handling scaling differently. v1 does not try to solve consistency across hosts. The protocol advertises the best information the server has; expect per-platform tuning knobs in later slices. Tests should cover at least 100%, 125%, 150%, and 175% DPI configurations on Windows.

### UDP video channel

Port advertised in `STREAM_STARTED`. Packet structure:

```
offset  size   field
──────  ────   ─────
0       4      magic = 0x57535452 ('WSTR')
4       4      streamId (big-endian uint32)
8       4      sequence (big-endian uint32, monotonic per stream)
12      8      presentationTimestampMicroseconds (big-endian uint64)
20      1      flags
                 bit 0: IDR frame
                 bit 1: last fragment of NAL unit
                 bits 2-7: reserved
21      1      fragmentIndex (0-based)
22      1      fragmentTotal (count of fragments for this NAL unit)
23      1      reserved
24      ≤1200  NAL unit fragment (Annex-B framing, including start code)
```

Payload size chosen to fit 1500-byte path MTU with IPv4 + UDP + protocol overhead.

Fragment reassembly: the viewer buffers fragments keyed by `(streamId, sequence)` until `fragmentTotal` packets received, then hands the reassembled NAL unit to MediaCodec. Incomplete sets are dropped after 500 milliseconds.

## Error Handling

Philosophy for v1: **fail loudly, recover manually.** Restart-the-application is an acceptable recovery path for proof-of-concept. Sophisticated retry machinery is deferred to v1.1.

### Server — capture/encode failures

| Failure | Detection | v1 response |
|---|---|---|
| WGC cannot attach (window closed between list and start) | exception on `CreateFromWindowId` | `ERROR{WINDOW_GONE}` to viewer if connected; GUI shows "window no longer available"; picker refreshes list |
| WGC delivers black frames (DRM / protected content) | no automatic detection in v1 | documented limitation; user notices visually |
| Minimized or occluded window stops producing frames | `Direct3D11CaptureFramePool` frame arrival stalls > 5 seconds | server logs; no virtual-monitor workaround in v1 |
| NVENC initialization fails | FFmpeg returns error on `avcodec_open2` | hard fail at session start; explicit error in GUI/CLI; no libx264 fallback |
| Encoder stalls / back-pressure | input queue exceeds threshold | drop oldest un-encoded frames, log |

### Server — network failures

| Failure | Detection | v1 response |
|---|---|---|
| UDP port bind failure | socket exception | hard fail, explicit error |
| TCP listener bind failure | socket exception | hard fail, explicit error |
| mDNS advertise fails | zeroconf library returns error | log warning, continue; viewer can use manual IP entry |
| Viewer TCP drops unexpectedly | SocketException on read or write | log, keep capturing, await new connection |
| Heartbeat timeout (6 seconds of silence) | timer elapsed | close TCP, keep capturing, wait for new connection |

### Viewer — decoder/render failures

| Failure | Detection | v1 response |
|---|---|---|
| MediaCodec configure fails (unsupported codec or resolution) | `IllegalArgumentException` on `configure()` | UI surfaces "cannot decode this stream: {reason}," disconnect |
| Decoder output stalls (no usable frame > 2 seconds) | output-surface timestamp monitor | send `REQUEST_KEYFRAME`; if still stalled 4 seconds later, reset decoder |
| Decoder reset fails | exception on recreate | disconnect, show error |
| Panel / XR session invalid (headset removed, permissions revoked) | Jetpack XR callback | pause UDP/TCP, save minimal state, resume on session return |
| Missing UDP fragments | `fragmentIndex` / `fragmentTotal` mismatch on frame assembly | drop partial frame; if gap exceeds 500 ms, send `REQUEST_KEYFRAME` |

### Viewer — network failures

| Failure | Detection | v1 response |
|---|---|---|
| No servers discovered within 10 seconds | NSD empty list | UI: "no servers found"; offer manual IP entry as first-class v1 feature |
| Server disappears mid-session | heartbeat timeout or TCP read error | UI: "disconnected"; return to server-picker screen; one auto-reconnect attempt with 2-second backoff; give up after that |
| Control channel send failure | write exception | same as server disappearance |

### Protocol errors

| Failure | Response |
|---|---|
| Version mismatch in HELLO | both sides send `ERROR{VERSION_MISMATCH}`, disconnect; UI shows "server and viewer on different versions" |
| Malformed JSON or unknown message type | log with hex dump, disconnect |
| `ERROR` received | display to user; disconnect (all v1 errors are session-fatal) |

## Testing

Proportional to proof-of-concept. Invest where it prevents rework; do not overbuild.

### Coverage target

**100% line and branch coverage on all v1 production code.** Enforced by a coverage gate (details in implementation plan). Not a best-effort aspiration; a design-level commitment.

### Server (.NET) — unit tests

- **Protocol serialization** — round-trip every message type. Pins the wire format; breaking it later is costly across two stacks.
- **Packet fragmentation and reassembly** — feed NAL units of various sizes, verify fragment headers and reconstitution. Table-driven.
- **Session state machine** — `Idle → Capturing → Streaming → Stopped` transitions, including disconnect/reconnect edges.
- **mDNS TXT record construction** — smoke test.

xUnit test framework; Coverlet for coverage measurement.

### Server — integration tests (Windows target framework only, skipped on other runners)

- **WGC can-attach smoke test** — spawn a Notepad process, enumerate windows, find by title, attach WGC, verify at least one frame delivered within 2 seconds. Tear down Notepad at end. Requires desktop session; local-only.
- **NVENC initialization smoke test** — configure FFmpeg `h264_nvenc` with expected options; verify `avcodec_open2` succeeds. Requires NVIDIA driver.

### Viewer (Kotlin) — unit tests

- **Protocol parsing** — mirror the server's round-trip tests on the Kotlin side. Intentional duplication: two implementations keep each other honest.
- **Packet reassembly** — same table-driven inputs as server tests.
- **State machine** — `Disconnected → Discovering → Connected → Streaming → Error`.

JUnit 5 + kotlinx-coroutines-test.

### Viewer — integration tests (Android emulator)

Android XR has no emulator as of 2026-04, so XR-specific behavior (panel placement, hand drag, gesture response) remains manual-acceptance territory. Everything below the XR layer *can* be tested on a standard Android emulator and is worth the investment — it lets agents iterate on viewer code without deploying to the headset on every change.

- **End-to-end viewer loopback test** — Gradle Managed Devices provisions a standard Android emulator. A JVM test harness acts as a fake server, sending a pre-recorded H.264 stream to `127.0.0.1` over TCP (control) + UDP (video). The viewer application runs on the emulator with a `TextureSink` (headless `SurfaceTexture`) bound in place of `XrPanelSink`. Assertions: N decoded frames arrive within timeout, dimensions match the stream, frame content is non-black (sampled pixel check).

This is the viewer-side analog of the server's CLI + loopback smoke test. Together they form the "everything below XR actually works" gate before manual acceptance — agents can make progress on either side of the wire without the headset in hand.

### End-to-end smoke test

One integration test worth the effort:

- **CLI serve + loopback harness** — `WindowStream.Cli serve --hwnd <notepad>` on localhost; a test harness acts as viewer over TCP + UDP on `127.0.0.1`; receives N IDR frames; feeds them to a software H.264 decoder (FFmpeg again); asserts the decoded frame is non-black and has expected dimensions. Single "everything actually works" gate before manual acceptance. Agents rely on this to iterate without the HMD.

### Manual acceptance — the real v1 gate

Automation cannot tell you "feels good enough to code in." Manual acceptance checklist:

- [ ] Notepad streams; text is sharp enough to read
- [ ] Visual Studio or Rider streams; can follow code at normal editing speed
- [ ] Fork (git GUI) streams; can read diffs and commit graph
- [ ] Panel repositions with hand drag without obvious jank
- [ ] Disconnect and reconnect works without restarting server or viewer
- [ ] Server CPU + GPU usage is not alarming when idle with one stream

### Continuous integration

Not configured in v1. All testing is local. Revisit when the project has more than one contributor.

## Implementation Stack Summary

| Layer | v1 Choice | Rationale |
|---|---|---|
| Server runtime | C# / .NET 8 | user's preferred stack; cross-platform via .NET for future |
| Server GUI | .NET MAUI | user's preferred stack |
| Server CLI | System.CommandLine | standard .NET CLI framework |
| Windows capture | Windows.Graphics.Capture via CsWinRT | per-HWND capture; handles most applications |
| Video encode | FFmpeg via FFmpeg.AutoGen, `h264_nvenc` codec | portable C#; hardware encode on NVIDIA |
| Core library targets | `net8.0`, `net8.0-windows10.0.19041.0`, `net8.0-maccatalyst` | WinRT and AppKit require platform-flavored TFMs |
| Client runtime | Kotlin on Android XR | user wants to learn Jetpack XR; not Unity |
| Client XR framework | Jetpack XR SDK + Jetpack Compose for XR | SpatialExternalSurface + PanelEntity trivialize "video as floating panel" |
| Client decoder | MediaCodec (async mode) | direct Surface output into panel; keyframe-request control needed |
| Transport: video | UDP with custom framing (H.264 Annex-B + 24-byte header) | lean, low-latency, debuggable with Wireshark |
| Transport: control | TCP with JSON (length-prefixed) | simple, human-readable, trivial to debug |
| Discovery | mDNS / Network Service Discovery | LAN-native; cross-platform |

## Planned Project Structure

Created in the implementation phase.

```
~/WindowStream/
├── README.md
├── .gitignore
├── WindowStream.sln
├── src/
│   ├── WindowStream.Core/              # multi-targeted class library
│   │   ├── WindowStream.Core.csproj
│   │   ├── Protocol/                   # message types, serializers, packet framing
│   │   ├── Session/                    # session state machine
│   │   ├── Discovery/                  # mDNS advertiser
│   │   ├── Capture/
│   │   │   ├── IWindowCaptureSource.cs
│   │   │   └── Windows/                # #if WINDOWS
│   │   │       ├── WgcCaptureSource.cs
│   │   │       └── WindowEnumerator.cs
│   │   │   # (MacOS/ and Linux/ directories added in later slices)
│   │   └── Encode/
│   │       ├── IVideoEncoder.cs
│   │       └── FFmpegNvencEncoder.cs   # portable (FFmpeg handles hardware acceleration)
│   ├── WindowStreamServer/             # MAUI application, Windows target in v1
│   │   └── WindowStreamServer.csproj
│   └── WindowStream.Cli/               # console application
│       └── WindowStream.Cli.csproj
├── tests/
│   ├── WindowStream.Core.Tests/
│   └── WindowStream.Integration.Tests/  # smoke test + capture/encoder integration
├── viewer/                             # separate Android Studio project
│   └── WindowStreamViewer/
│       ├── app/
│       │   └── src/main/kotlin/.../
│       │       ├── discovery/
│       │       ├── control/
│       │       ├── transport/
│       │       ├── decoder/
│       │       ├── xr/
│       │       └── app/
│       └── build.gradle.kts
└── docs/
    ├── superpowers/
    │   └── specs/
    │       └── 2026-04-19-windowstream-design.md   # this document
    └── protocol.md                     # authoritative wire format specification
```

## Open Questions

None from brainstorming. Implementation may surface additional questions; record them here as follow-ups.
