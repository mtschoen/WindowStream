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
