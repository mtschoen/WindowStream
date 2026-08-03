# WindowStream

## Repository layout

- `src/WindowStream.Core/` — multi-targeted library (`net8.0`, `net8.0-windows10.0.19041.0`). Protocol, session, discovery, capture/encode interfaces.
- `src/WindowStreamServer/` — .NET MAUI picker GUI (Windows target in v1).
- `src/WindowStream.Cli/` — headless console application.
- `tests/WindowStream.Core.Tests/` — unit tests (xUnit, Coverlet).
- `tests/WindowStream.Integration.Tests/` — capture/encode smoke tests, Windows-only.

## Architecture (server-side pipeline)

After the GPU-resident refactor, captured frames stay on the GPU all the way through the encoder. Only the encoded H.264 bitstream is read back to
managed memory for the socket:

```text
[ WGC ] -> D3D11 BGRA texture
              |
              v   D3D11VideoProcessorColorConverter (VideoProcessorBlt)
[ NV12 D3D11 texture ] (from FFmpeg hw_frames_ctx pool)
              |
              v   FFmpeg D3D11VA hwaccel (h264_nvenc)
[ NVENC ] -> H.264 AVPacket bytes
              |
              v
[ NalFragmenter ] -> UDP socket
```

Composition root: `WorkerCommandHandler` constructs a single `Direct3D11DeviceManager` per worker process and shares it with both `WgcCaptureSource`
and `FFmpegNvencEncoder`. The shared device must be created with `D3D11_CREATE_DEVICE_VIDEO_SUPPORT` (the manager handles this). There is no `sws_scale`
and no per-frame CPU staging readback on the encode path — both are deliberately gone post-M4.

`[FRAMECOUNT]` instrumentation: the server emits `stage=convert` (in `WgcFrameConverter`) and `stage=enc` (in `FFmpegNvencEncoder`); the viewer emits
`stage=reasm`, `stage=dec`, and `stage=present` (the last via a `Choreographer.postFrameCallback` in `MediaCodecDecoder`, timestamping actual scanout
rather than buffer-release). All five share the same `ptsUs` axis (microseconds since capture start, threaded through `CapturedFrame`, encoder pts, and
the H.264 frame PTS) and `wallMs` axis (Unix-epoch ms), so end-to-end cap → present joins are exact per-frame.

### FFmpeg / OBS dependency

The encoder uses FFmpeg's D3D11VA hwaccel for `h264_nvenc`. This requires **FFmpeg 5.x or newer** (`AV_HWDEVICE_TYPE_D3D11VA` and `AV_PIX_FMT_D3D11`
must be exported). OBS Studio ships FFmpeg 6.x in `bin/64bit/`, which is fine. If you replace the OBS-bundled DLLs with a custom build, verify those two
symbols are exported.

### Design notes (folklore worth preserving)

- **WGC frame surface lifetime.** `Direct3D11CaptureFrame` reuses textures within the framepool, so the converter must complete `VideoProcessorBlt`
  before returning from `OnFrameArrived`. `WgcFrameConverter.Convert` runs synchronously in the callback by construction, which preserves the
  invariant — keep it that way.
- **D3D11 device sharing.** The shared `ID3D11Device` satisfies three consumers (WGC via the WinRT `IDirect3DDevice` wrapper, the D3D11 video processor,
  and FFmpeg's `AVHWDeviceContext` of type D3D11VA). Feature levels must align across all three. `Direct3D11DeviceManager`'s device-creation flags exist
  for this reason — don't strip `D3D11_CREATE_DEVICE_VIDEO_SUPPORT`.
- **FFmpeg hwaccel error messages are silent.** `hw_frames_ctx` misconfiguration surfaces as `AVERROR(EINVAL)` with no contextual message, and the
  failure typically appears at the first `avcodec_send_frame` rather than at `avcodec_open2` — i.e. "encoder opened fine, then died on frame 1". When
  debugging a fresh hwaccel setup, reach for the canonical FFmpeg sample (`doc/examples/hw_decode.c` plus the NVENC patterns in `libavcodec/nvenc.c`)
  before assuming the WindowStream wiring is wrong.
- **NV12 pool BindFlags.** For NVENC encoding, the pool textures must carry `D3D11_BIND_RENDER_TARGET` (and `D3D11_BIND_SHADER_RESOURCE` so the same
  texture also serves as the video-processor input view). `FFmpegNvencEncoder.OpenCodecAndAssignOptions` sets this explicitly — the default D3D11VA
  `BindFlags` (decoder + shader resource) cause `E_INVALIDARG` on `av_hwframe_ctx_init` because NVENC rejects decode-only bind flags.

### Open future work

- **CUDA filter chain.** When on-GPU scaling becomes a need (e.g. a resolution-adaptive ladder), the next addition is a `scale_cuda` filter inserted
  between the converter and NVENC. This requires adding a CUDA hwaccel device alongside the D3D11VA one and using `hwmap` / `hwupload` to bridge them.
- **AMD AMF encoder.** `Direct3D11DeviceManager` is intentionally encoder-agnostic. An `AmfVideoEncoder` would consume the same manager-owned device
  and accept the same texture-bearing `CapturedFrame`, with AMF's analogue of `hw_frames_ctx` for its surface pool.
- **Cross-process texture sharing.** The v2 coordinator/worker split currently exchanges encoded bitstream chunks between processes. If we ever want to
  share captured textures across processes, that requires `KeyedMutex` + shared handles — significantly more work, not done.

## Build

```bash
dotnet restore
dotnet build
```

## Test (100% line + branch coverage gate)

```bash
dotnet test
```

Coverage thresholds are enforced via Coverlet in `tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj` and will fail the build below 100% line
or branch coverage. Native I/O wrappers (D3D11 COM, FFmpeg, raw sockets) are excluded via `[ExcludeFromCodeCoverage]` with inline rationale; integration
tests cover those paths.

## Conventions

- One type per file.
- Nullable reference types enabled everywhere.
- Full words in identifiers — `maximum`, `configuration`, `sequence`, `arguments` (no `max`, `cfg`, `seq`, `args`).
- `async`/`await` for I/O; `CancellationToken` threaded through public async methods.
- Commit messages in imperative mood. Small, frequent commits.

## Running the demo end-to-end

### Fast path: HMD-camera latency-clock test

For the standard latency measurement (cold start, with HMD on but nothing else running):

```cmd
tools\latency-test
```

(`tools\latency-test.bat` is a one-line wrapper that invokes `tools\record-latency-clock.ps1` with `-ExecutionPolicy Bypass`. Args are forwarded —
e.g. `tools\latency-test -Duration 5 -Hwnd 12345`.)

The script handles adb wifi connect, source-window detection, server launch, and a 4-second frame-flow probe before asking you to go on-head.
Diagnostics on every common failure mode (no HMD, no source window, WGC capture failed, network blocked).

### Latency baseline & ground truth metrics

Below is the durable ground truth and software-level end-to-end latency baseline for the GPU-resident pipeline (established on Quest 3 / Galaxy XR):

- **Ground Truth Photon-to-Photon Latency** (Camera-based ground truth via `SpatialExternalSurface` / XR compositor):
  - **p0 (Min)**: **12 ms** (~2 frames at 165 Hz; OCR shows 1-frame delta but shutter catches mid-digit-transition so true floor is ~2 frames)
  - **p50 (Median)**: **17 ms** (~1 frame at 60 fps / ~2.8 frames at 165 Hz)
  - **p95**: **34 ms**
  - **Steady-State Range**: 12–17 ms
- **Software-Level End-to-End Latency** (`convert` to `present` timestamps):
  - **p0 (Min)**: **12 ms** (≈2 frames at 165 Hz)
  - **p50 (Median)**: **24 ms** (4 frames at 165 Hz; improved from 28 ms after UDP transport fix, 2026-05-28)
  - **p95**: **73 ms** (occasional compositor hitches; steady-state p95 clusters near 40 ms)

The manual recipe below is the fallback when the script itself is broken or you want to test something the script doesn't cover.

### Server side (Windows)

1. Install OBS Studio (provides FFmpeg native DLLs) OR manually drop `avcodec-61.dll`, `avutil-59.dll`, `swscale-8.dll`, `swresample-5.dll`,
   `zlib.dll`, `libx264-164.dll` next to the CLI output.
2. **Network profile must be `Private`** on the LAN adapter — Windows Firewall blocks outbound mDNS multicast on `Public`, so the viewer never
   discovers the server:

   ```powershell
   Set-NetConnectionProfile -Name <ssid> -NetworkCategory Private
   ```

3. First run adds firewall rules as admin (UAC). If auto-prompt doesn't cover it, run in an elevated PowerShell:

   ```powershell
   New-NetFirewallRule -DisplayName WindowStream-Session-TCP-<port> -Direction Inbound -LocalPort <tcpPort> -Protocol TCP -Action Allow -Profile Any
   New-NetFirewallRule -DisplayName WindowStream-Session-UDP-<port> -Direction Inbound -LocalPort <udpPort> -Protocol UDP -Action Allow -Profile Any
   ```

   (OS assigns ports per session; a broader binary-based rule covering `windowstream.exe` is cleaner. `/wrap` removes `WindowStream-Session-*` rules
   at session end.)
4. Start the server (v2 coordinator — `serve` takes no `--hwnd` arg; the viewer picks the window remotely via OPEN_STREAM):

   ```bash
   dotnet run --project src/WindowStream.Cli -f net8.0-windows10.0.19041.0 -- serve
   ```

   The coordinator advertises via mDNS as `<MachineName>._windowstream._tcp` and lists capturable windows in `ServerHello`; the viewer drives
   selection (multi-server, multi-window). Pre-fetch HWNDs from the host with `list` if you want to bypass the picker via the
   `selectedWindowHwnds` adb intent extra below:

   ```bash
   dotnet run --project src/WindowStream.Cli -f net8.0-windows10.0.19041.0 -- list
   # pick HWNDs with active content and even width/height (NV12 needs even
   # dims; sws_scale is gone post-M4, but odd-dim hasn't been re-verified
   # against the GPU video processor + NVENC path — pick even for safety).
   ```

5. Note the IP (your LAN address) and the TCP port in the server banner.

### Viewer side — two Gradle flavors

Build the flavor you want. APK paths changed with the portable-flavor split (commit `211bc15`); the pre-flavor `app-debug.apk` no longer exists.

**Portable flavor** (Quest 3, phones, tablets, Fold, Galaxy XR as 2D window):

```bash
./gradlew :app:assemblePortableDebug
adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/portable/debug/app-portable-debug.apk
# Launcher: tap the WindowStream Viewer icon → auto-connects to discovered server. Open windows dynamically using the drawer toggle (≡) in the tab bar.
# Or bypass the picker (adb-only) with explicit IP:
adb shell am start -n com.mtschoen.windowstream.viewer/.demo.DemoActivity \
    --es streamHost <pc-lan-ip> --ei streamPort <tcpPort>
# Bypass picker and target one or more HWNDs (one shared control session):
adb shell am start -n com.mtschoen.windowstream.viewer/.demo.DemoActivity \
    --es streamHost <pc-lan-ip> --ei streamPort <tcpPort> \
    --ela selectedWindowHwnds <hwnd1>,<hwnd2>
# DemoActivity resolves each HWND to a v2 windowId via ServerHello.windows.
# Multi-server via adb:
adb shell am start -n com.mtschoen.windowstream.viewer/.demo.DemoActivity \
    --esa streamHosts "<ip1>,<ip2>" --eia streamPorts "<port1>,<port2>"
```

**GXR flavor** (Samsung Galaxy XR / Android XR — immersive **spatial window manager**):

```bash
./gradlew :app:assembleGxrDebug
adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/gxr/debug/app-gxr-debug.apk
# Launch the immersive window manager (icon-tap, or via adb):
adb shell am start -n com.mtschoen.windowstream.viewer/.app.MainActivity
# Optional: skip discovery / auto-spawn windows by HWND:
adb shell am start -n com.mtschoen.windowstream.viewer/.app.MainActivity \
    --es streamHost <pc-lan-ip> --ei streamPort <tcpPort> \
    --ela selectedWindowHwnds <hwnd1>,<hwnd2>
```

`MainActivity` (gxr launcher) is a spatial window manager: it auto-discovers the server, shows a persistent **drawer** panel listing capturable
windows, and spawns each picked window as its own movable `SpatialExternalSurface` panel with a **chrome bar** (close ×, minimize/restore,
resize –/+). Multiple windows stream concurrently. Minimize pauses the stream and shrinks the panel (the surface stays mounted — no decoder churn);
restore resumes it; resize is viewer-side panel scaling (not source-window resize). Architecture: `SpatialWindowManager` holds pure panel state
(unit-tested); the Activity owns per-window runtime resources; `SpatialWindowManagerScene` renders the Compose-for-XR scene (coverage-excluded). Dep
is now Jetpack XR `1.0.0-alpha13` — the old alpha04 `createSplitEngineBridge` icon-crash no longer applies. **HMD-verified 2026-05-29**
(built, unit-tested, and confirmed on-head: 2 windows streaming live video concurrently). The on-head checklist + plan docs have since been
retired.

Force-stop any flavor with:

```bash
adb shell am force-stop com.mtschoen.windowstream.viewer
```

### Gotchas — capture target selection

- **Static windows emit ≤1 frame.** WGC only delivers frames on content change. Notepad with no typing + no cursor = one frame then silence. Pick a
  window with active content (terminal with spinner, video player, editor with cursor) or v1.x will need to enable cursor capture / timed RedrawWindow.
- **Background browser/game windows self-throttle, and it is TIME-PROGRESSIVE.** A Chromium/Electron/game window that animates via requestAnimationFrame
  keeps feeding WGC only while it keeps painting. Once backgrounded/occluded for a while it stops: measured (spike `wgc-frame-delivery-map`) ~full rate at
  6s background but **1 frame then silence at 30s** background. This starves capture identically to a static window. For Edge `--app` capture targets
  (e.g. integration tests) pass the anti-throttle flags `--disable-background-timer-throttling --disable-backgrounding-occluded-windows
  --disable-renderer-backgrounding --disable-features=CalculateNativeWinOcclusion`. For arbitrary production targets we cannot relaunch with flags;
  the shipped `SourceFrameMonitor` (worker-side cadence detector) and `ChunkCadenceWatchdog` (coordinator safety net) detect the stall and surface
  `STREAM_STALLED` / `STREAM_RESUMED` to the viewer, which banners it per-panel. (Minimized = 0 frames always; offscreen-but-not-minimized
  composes fine.)
- **Windows 11 Store-packaged apps** (Notepad, Terminal) use a launcher process that exits immediately; `Process.Start` returns a stub. Not a demo
  issue but affects test cleanup — snapshot existing PIDs, kill new ones in `finally`.

### Gotchas — Galaxy XR

- **Radio parks off-head.** HMD Wi-Fi stops routing packets when the proximity sensor reports "off face." Wear it or block the sensor with a card to
  keep it alive during multi-minute tests. Don't leave the card there long — thermal risk.
- **Wi-Fi after OS update.** Toggle Wi-Fi off/on once after a big update — the driver can be wedged with a valid IP but no actual traffic.
- **adb over Wi-Fi across sleep** — connection may drop when HMD sleeps. Reconnect with `adb connect <ip>:5555` after waking.

### Debugging tips

- Server has `Console.Error.WriteLine` diagnostics for capture/encode pump lifecycle, VIEWER_READY registration, per-chunk send counts.
  Redirect `2>&1` to capture.
- Viewer logs — filter with:

  ```bash
  adb logcat -d --pid=$(adb shell pidof com.mtschoen.windowstream.viewer) \
      -s WindowStreamDemo:V MediaCodec:V MediaCodecDecoder:V FRAMECOUNT:V *:E
  ```

  The `FRAMECOUNT` tag emits one line per frame at `stage=reasm` (reassembler complete) and `stage=dec` (output buffer rendered); pair with server
  stderr `[FRAMECOUNT]` lines (`stage=enc`/`stage=frag`) to measure pipeline-depth latency. PTS in microseconds is the join key across server/viewer.
- Frame flow check: `adb shell cat /proc/net/dev | grep wlan0` and watch RX bytes climb; steady 0 → server isn't actually sending → likely
  VIEWER_READY / endpoint issue.

### Diagnostics — pipeline state + JSONL logs

Both apps emit typed `PipelineEvent`s through a `Diagnostics` façade. State boards and event logs live in-app; a rotating JSONL file log persists for
7 days.

**Server file log:** `%LOCALAPPDATA%\WindowStream\logs\server-YYYY-MM-DD.jsonl`. Open via the dashboard's "Open log folder" button, or grep with `jq`:

```bash
jq 'select(.EventType=="WorkerSpawnFailed")' server-2026-05-17.jsonl
```

**Viewer file log:** `<app-external-files>/logs/viewer-YYYY-MM-DD.jsonl`.
Pull via `adb pull /storage/emulated/0/Android/data/com.mtschoen.windowstream.viewer/files/logs/`.

**What's NOT in the pipeline event stream:** `[FRAMECOUNT]` per-frame markers stay on stderr / logcat — they would flood the in-app buffer + balloon
the file. The diagnostic boundary is *stage transitions and errors*, not per-frame.

### Source stall detection

Two complementary detectors watch for source windows that stop producing frames:

- **`SourceFrameMonitor`** (worker-side, primary): watches the real WGC frame cadence. Detects "never got frame 1" (startup grace expired) and "cadence
  cliff" (frames were flowing, then a gap many multiples of the established interval). Emits `WorkerStatusFrame` over the worker pipe.
- **`ChunkCadenceWatchdog`** (coordinator-side, safety net): watches chunk arrival on the pipe and fires only when the worker goes silent without
  self-reporting (worker wedged/crashed). Suppressed while the worker has self-reported a stall.

Both surface `PipelineEvent.SourceStalled` / `PipelineEvent.SourceResumed` through the `Diagnostics` facade, and send `STREAM_STALLED` /
`STREAM_RESUMED` control messages to the viewer. The viewer banners the stall per-stream (portable) or per-panel (GXR). `StallCause` values:
`NEVER_STARTED`, `SOURCE_STALLED`, `WORKER_SILENT`. A stall is NOT a `STREAM_STOPPED`: the stream stays alive and may resume.

## Dependency report

Generate with:

```bash
python tools/report-dependencies.py
```

Reads every csproj and the viewer's `libs.versions.toml`, emits a markdown snapshot of production + test packages with resolved versions. The csprojs
and version catalog are the source of truth; don't hand-maintain a separate doc.

## WGC frame-delivery probe

`tools/frame-delivery-probe/` measures WGC frame delivery by window state (foreground, occluded, minimized, offscreen). Validates the stall-detector
thresholds. See `tools/frame-delivery-probe/README.md`.

## Testing

Integration tests are the authoritative signal. If an integration test is slow, speed up the setup — do NOT replace it with a unit-level mock that
pretends to verify behavior. Shared fixtures and warm external state are the levers. The general `fast-tests` skill covers cross-project patterns.

Project-specific test notes:

- **Integration tests live at `tests/WindowStream.Integration.Tests/`**, `#if WINDOWS`-gated or skipped when hardware is absent (no NVIDIA driver →
  NVENC init skips; no mDNS loopback → that test skips).
- **Notepad cleanup** — Windows 11's Store-packaged Notepad makes `Process.Start("notepad.exe")` return a launcher that exits immediately. Use the
  PID-snapshot pattern in `WgcCaptureSourceSmokeTests` — snapshot existing notepad PIDs, kill any new ones with `entireProcessTree: true` in `finally`.
  Don't regress this.
- **Shared fixtures for CLI+loopback** — when adding more integration tests around `SessionHost`, put them in one xUnit `[Collection]` with an
  `IAsyncLifetime` class fixture that boots the stack once. Per-test cost drops from seconds to milliseconds.
- **Android emulator** — prefer a persistent AVD over Gradle Managed Devices for rapid iteration. GMD is fine for CI; it is expensive locally because
  of per-run cold boot.
- **DPI test matrix** — the server handles DPI internally (`GetDpiForWindow` + physical-pixel encoding per the protocol's DPI handling section).
  Integration tests must cover at least 100% / 125% / 150% / 175% scaling.

## Toolchain and runtime dependencies

- **FFmpeg native DLLs** — v1 stopgap copies them from `$(ProgramFiles)\obs-studio\bin\64bit\` if OBS is installed. Replace with a BtbN-builds
  MSBuild downloader target (planned follow-up). Until then, install OBS Studio OR set `WINDOWSTREAM_SKIP_NVENC=1` to skip encoder-dependent tests.
- **MAUI and .NET 10** — the MAUI workload on this machine is `.NET 10` era. `WindowStreamServer` targets `net10.0-windows10.0.19041.0` while the rest
  of the solution is `net8.0[;-windows10.0.19041.0]`. Don't try to force the server to `net8.0-windows` — MAUI will refuse.
- **Android SDK** — only `android-36` is installed; `compileSdk`/`targetSdk` are pinned to 36 accordingly. If you add emulator integration work,
  pre-download system images with `sdkmanager` before fanning out agents.

## DPI handling

Server-side responsibility. Read source window DPI via `GetDpiForWindow`, configure the encoder to match WGC's physical output, and advertise
`width`/`height` as physical pixels in `STREAM_STARTED`. `dpiScale` is optional informational metadata. `GetWindowRect`/`GetClientRect` differ
from WGC's actual captured frame size by a few pixels (window chrome, shadows, DPI rounding), so probe WGC for one frame and configure the
encoder from the real `CapturedFrame` dimensions rather than the window rect. Expect per-platform tuning (Windows WinForms/WPF/MAUI/Qt all
handle scaling differently; macOS has its own backing-scale-factor weirdness; cross-platform consistency is a v2 concern).

## Coverage gate configuration

- **.NET (Coverlet)** — set `<CollectCoverage>true</CollectCoverage>` and thresholds directly in each test csproj. On .NET 10 SDK, a
  `Directory.Build.props` `<PropertyGroup Condition="'$(IsTestProject)' == 'true'">` block silently disables collection because `IsTestProject` isn't
  set early enough for VSTest. Don't revert to the conditional form.
- **Kotlin (Kover)** — `viewer/WindowStreamViewer/app/build.gradle.kts` uses `useJacoco()` because the default IntelliJ engine counts synthetic
  kotlinx-serialization `$$serializer` / `$Companion` branches as uncovered. Class exclusions are documented inline with rationale; each new exclusion
  should get a rationale.
- **Coroutine idiom gotcha** — `while (isActive) { delay() }` creates an unreachable while-false branch under cooperative cancellation. Prefer
  restructuring to `while (true) { delay(); … }` over adding a Kover exclusion.

## Quality gate: aislop

This project uses **aislop** as a deterministic quality gate for AI-written code (narrative comments, swallowed exceptions, `as any`, dead stubs,
oversized functions, etc.) across TS/JS, Python, Go, Rust, Ruby, PHP, Java, and C#.

`aislop` is installed globally on this machine (pinned to the fork `mtschoen/aislop`, which adds C#/roslynator support). Call the installed binary
directly — do NOT use `npx aislop`, which pulls upstream from npm with no C# support:

- **Before declaring work complete**, run `aislop scan .` and address findings.
- **Before committing**, run `aislop scan --staged` (staged files only).
- `aislop fix` auto-clears mechanical issues (formatting, unused imports, dead code); `aislop fix --claude` hands the rest back with full context.
- `aislop ci .` is the gate — exits non-zero if the score drops below the threshold in `.aislop/config.yml`. Treat a failing gate like a failing test.

To refresh the pinned binary after new commits land on the fork branch:
`pnpm add -g --allow-build=aislop "github:mtschoen/aislop#feat/csharp-support"`
