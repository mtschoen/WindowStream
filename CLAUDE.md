# WindowStream

## Repository layout

- `src/WindowStream.Core/` — multi-targeted library (`net8.0`, `net8.0-windows10.0.19041.0`). Protocol, session, discovery, capture/encode interfaces.
- `src/WindowStreamServer/` — .NET MAUI picker GUI (Windows target in v1).
- `src/WindowStream.Cli/` — headless console application.
- `tests/WindowStream.Core.Tests/` — unit tests (xUnit, Coverlet).
- `tests/WindowStream.Integration.Tests/` — capture/encode smoke tests, Windows-only.

## Build

```bash
dotnet restore
dotnet build
```

## Test (100% line + branch coverage gate)

```bash
dotnet test
```

Coverage thresholds are enforced via `Directory.Build.props` and will fail the
build below 100% line or branch coverage on `WindowStream.Core`.

## Conventions

- One type per file.
- Nullable reference types enabled everywhere.
- Full words in identifiers — `maximum`, `configuration`, `sequence`, `arguments` (no `max`, `cfg`, `seq`, `args`).
- `async`/`await` for I/O; `CancellationToken` threaded through public async methods.
- Commit messages in imperative mood. Small, frequent commits.

## Running the demo end-to-end

### Server side (Windows)

1. Install OBS Studio (provides FFmpeg native DLLs) OR manually drop `avcodec-61.dll`, `avutil-59.dll`, `swscale-8.dll`, `swresample-5.dll`, `zlib.dll`, `libx264-164.dll` next to the CLI output.
2. **Network profile must be `Private`** on the LAN adapter — Windows Firewall blocks outbound mDNS multicast on `Public`, so the viewer never discovers the server:
   ```powershell
   Set-NetConnectionProfile -Name <ssid> -NetworkCategory Private
   ```
3. First run adds firewall rules as admin (UAC). If auto-prompt doesn't cover it, run in an elevated PowerShell:
   ```powershell
   New-NetFirewallRule -DisplayName WindowStream-Session-TCP-<port> -Direction Inbound -LocalPort <tcpPort> -Protocol TCP -Action Allow -Profile Any
   New-NetFirewallRule -DisplayName WindowStream-Session-UDP-<port> -Direction Inbound -LocalPort <udpPort> -Protocol UDP -Action Allow -Profile Any
   ```
   (OS assigns ports per session; a broader binary-based rule covering `windowstream.exe` is cleaner. `/wrap` removes `WindowStream-Session-*` rules at session end.)
4. Start the server:
   ```bash
   dotnet run --project src/WindowStream.Cli -f net8.0-windows10.0.19041.0 -- list
   # pick an HWND with actively-updating content AND even width/height
   #   (odd-height windows crash the sws_scale pump — see Gotchas)
   dotnet run --project src/WindowStream.Cli -f net8.0-windows10.0.19041.0 -- serve --hwnd <handle>
   ```
   Server advertises itself via mDNS as `<MachineName>-<TcpPort>._windowstream._tcp` so the viewer's picker finds it automatically. Run the command N times with N different HWNDs for multi-window.
5. Note the IP (your LAN address) and the TCP port in the server banner.

### Viewer side — two Gradle flavors

Build the flavor you want. APK paths changed with the portable-flavor split (commit `211bc15`); the pre-flavor `app-debug.apk` no longer exists.

**Portable flavor** (Quest 3, phones, tablets, Fold, Galaxy XR as 2D window):
```bash
./gradlew :app:assemblePortableDebug
adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/portable/debug/app-portable-debug.apk
# Launcher: tap the WindowStream Viewer icon → multi-select picker → Connect.
# Or bypass the picker (adb-only) with explicit IP:
adb shell am start -n com.mtschoen.windowstream.viewer/.demo.DemoActivity \
    --es streamHost <pc-lan-ip> --ei streamPort <tcpPort>
# Multi-server via adb:
adb shell am start -n com.mtschoen.windowstream.viewer/.demo.DemoActivity \
    --esa streamHosts "<ip1>,<ip2>" --eia streamPorts "<port1>,<port2>"
```

**GXR flavor** (Samsung Galaxy XR immersive spatial panel):
```bash
./gradlew :app:assembleGxrDebug
adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/gxr/debug/app-gxr-debug.apk
```
⚠ Icon-tap currently crashes on Galaxy XR's current OS — Jetpack XR alpha04 × system `XrExtensions` ABI mismatch (`NoSuchMethodError: createSplitEngineBridge`). Use the portable-flavor `DemoActivity` adb-launch pattern above as a workaround until the Jetpack XR dependency is bumped. See memory `project_gxr_jetpack_xr_alpha04_broken` and `docs/superpowers/specs/2026-04-20-multi-window-followups.md`.

Force-stop any flavor with:
```bash
adb shell am force-stop com.mtschoen.windowstream.viewer
```

### Gotchas — capture target selection

- **Static windows emit ≤1 frame.** WGC only delivers frames on content change. Notepad with no typing + no cursor = one frame then silence. Pick a window with active content (terminal with spinner, video player, editor with cursor) or v1.x will need to enable cursor capture / timed RedrawWindow.
- **Windows 11 Store-packaged apps** (Notepad, Terminal) use a launcher process that exits immediately; `Process.Start` returns a stub. Not a demo issue but affects test cleanup — snapshot existing PIDs, kill new ones in `finally`.

### Gotchas — Galaxy XR

- **Radio parks off-head.** HMD Wi-Fi stops routing packets when the proximity sensor reports "off face." Wear it or block the sensor with a card to keep it alive during multi-minute tests. Don't leave the card there long — thermal risk.
- **Wi-Fi after OS update.** Toggle Wi-Fi off/on once after a big update — the driver can be wedged with a valid IP but no actual traffic.
- **adb over Wi-Fi across sleep** — connection may drop when HMD sleeps. Reconnect with `adb connect <ip>:5555` after waking.

### Debugging tips

- Server has `Console.Error.WriteLine` diagnostics for capture/encode pump lifecycle, VIEWER_READY registration, per-chunk send counts. Redirect `2>&1` to capture.
- Viewer logs: `adb logcat -d --pid=$(adb shell pidof com.mtschoen.windowstream.viewer) -s WindowStreamDemo:V MediaCodec:V MediaCodecDecoder:V FRAMECOUNT:V *:E` filters to relevant output. The `FRAMECOUNT` tag emits one line per frame at `stage=reasm` (reassembler complete) and `stage=dec` (output buffer rendered); pair with server stderr `[FRAMECOUNT]` lines (`stage=enc`/`stage=frag`) to measure pipeline-depth latency. PTS in microseconds is the join key across server/viewer.
- Frame flow check: `adb shell cat /proc/net/dev | grep wlan0` and watch RX bytes climb; steady 0 → server isn't actually sending → likely VIEWER_READY / endpoint issue.

## Dependency report

Generate with:

```bash
python tools/report-dependencies.py
```

Reads every csproj and the viewer's `libs.versions.toml`, emits a markdown
snapshot of production + test packages with resolved versions. The csprojs and
version catalog are the source of truth; don't hand-maintain a separate doc.

## Testing

Integration tests are the authoritative signal. If an integration test is slow, speed up the setup — do NOT replace it with a unit-level mock that pretends to verify behavior. Shared fixtures and warm external state are the levers. The general `fast-tests` skill covers cross-project patterns.

Project-specific test notes:

- **Integration tests live at `tests/WindowStream.Integration.Tests/`**, `#if WINDOWS`-gated or skipped when hardware is absent (no NVIDIA driver → NVENC init skips; no mDNS loopback → that test skips).
- **Notepad cleanup** — Windows 11's Store-packaged Notepad makes `Process.Start("notepad.exe")` return a launcher that exits immediately. Use the PID-snapshot pattern in `WgcCaptureSourceSmokeTests` — snapshot existing notepad PIDs, kill any new ones with `entireProcessTree: true` in `finally`. Don't regress this.
- **Shared fixtures for CLI+loopback** — when adding more integration tests around `SessionHost`, put them in one xUnit `[Collection]` with an `IAsyncLifetime` class fixture that boots the stack once. Per-test cost drops from seconds to milliseconds.
- **Android emulator** — prefer a persistent AVD over Gradle Managed Devices for rapid iteration. GMD is fine for CI; it is expensive locally because of per-run cold boot.
- **DPI test matrix** — the server handles DPI internally (`GetDpiForWindow` + physical-pixel encoding per the protocol's DPI handling section). Integration tests must cover at least 100% / 125% / 150% / 175% scaling.

## Toolchain and runtime dependencies

- **FFmpeg native DLLs** — v1 stopgap copies them from `$(ProgramFiles)\obs-studio\bin\64bit\` if OBS is installed. Replace with a BtbN-builds MSBuild downloader target (planned follow-up). Until then, install OBS Studio OR set `WINDOWSTREAM_SKIP_NVENC=1` to skip encoder-dependent tests.
- **MAUI and .NET 10** — the MAUI workload on this machine is `.NET 10` era. `WindowStreamServer` targets `net10.0-windows10.0.19041.0` while the rest of the solution is `net8.0[;-windows10.0.19041.0]`. Don't try to force the server to `net8.0-windows` — MAUI will refuse.
- **Android SDK** — only `android-36` is installed; `compileSdk`/`targetSdk` are pinned to 36 accordingly. If you add emulator integration work, pre-download system images with `sdkmanager` before fanning out agents.

## DPI handling

Server-side responsibility. Read source window DPI via `GetDpiForWindow`, configure the encoder to match WGC's physical output, and advertise `width`/`height` as physical pixels in `STREAM_STARTED`. `dpiScale` is optional informational metadata. Expect per-platform tuning (Windows WinForms/WPF/MAUI/Qt all handle scaling differently; macOS has its own backing-scale-factor weirdness; cross-platform consistency is a v2 concern). See the protocol's DPI handling section in `docs/superpowers/specs/2026-04-19-windowstream-design.md`.

## Coverage gate configuration

- **.NET (Coverlet)** — set `<CollectCoverage>true</CollectCoverage>` and thresholds directly in each test csproj. On .NET 10 SDK, a `Directory.Build.props` `<PropertyGroup Condition="'$(IsTestProject)' == 'true'">` block silently disables collection because `IsTestProject` isn't set early enough for VSTest. Don't revert to the conditional form.
- **Kotlin (Kover)** — `viewer/WindowStreamViewer/app/build.gradle.kts` uses `useJacoco()` because the default IntelliJ engine counts synthetic kotlinx-serialization `$$serializer` / `$Companion` branches as uncovered. Class exclusions are documented inline with rationale; each new exclusion should get a rationale.
- **Coroutine idiom gotcha** — `while (isActive) { delay() }` creates an unreachable while-false branch under cooperative cancellation. Prefer restructuring to `while (true) { delay(); … }` over adding a Kover exclusion.
