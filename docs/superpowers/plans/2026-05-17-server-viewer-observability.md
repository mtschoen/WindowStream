# Server + Viewer Observability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the user end-to-end pipeline visibility on both the WindowStream server (MAUI dashboard) and the viewer (Android), so "tap connect, nothing happens" is diagnosable without reading `adb logcat` or invisible MAUI debug output.

**Architecture:** A typed `PipelineEvent` sealed class hierarchy on each side feeds a `Diagnostics` façade. On the server, events flow through `Microsoft.Extensions.Logging.ILogger` to three sinks: existing Debug, a Serilog rolling JSONL file sink, and a custom in-app sink that fans out to the `ServerDashboardViewModel`. On the viewer, events flow through Timber to three trees: existing Logcat, a rotating JSONL `FileLoggingTree`, and an `InAppBufferTree` exposing a `SharedFlow<LogEvent>` to the UI. A per-side reducer derives the state board from the event stream so the board and the event log can't disagree by construction.

**Tech Stack:**
- Server (.NET 10): `Microsoft.Extensions.Logging`, Serilog (`Serilog`, `Serilog.Extensions.Logging`, `Serilog.Sinks.File`, `Serilog.Formatting.Compact`), MAUI bindings.
- Viewer (Android Kotlin): `com.jakewharton.timber:timber`, `kotlinx-serialization-json` (already present), `kotlinx-coroutines` (already present).

---

## Phase 7: Cleanup + documentation

### Task 24: Update `AGENTS.md` with diagnostics paths

**Files:**
- Modify: `AGENTS.md`

- [x] **Step 1: Add a "Diagnostics" section to AGENTS.md**

Inserted after the "Debugging tips" subsection (line 224) and before "## Dependency report" — preserves the existing header hierarchy.

- [x] **Step 2: Commit**

### Task 25: Final coverage check + Core tests for `Diagnostics.Subscribe` fan-out

**Files:**
- Modify: `tests/WindowStream.Core.Tests/Observability/DiagnosticsTests.cs`

- [x] **Step 1: Add subscribe test** *(drift: the test was already in `DiagnosticsTests.cs:80-94`, added in Phase 1 commit `5e1231c` when the façade was introduced. No new code needed for T25.)*

- [x] **Step 2: Run full test suite both sides**

`dotnet test`: Core + Server projects 100% line/branch/method coverage, all 44 Server tests pass, all Core tests pass. `./gradlew :app:testPortableDebugUnitTest` BUILD SUCCESSFUL. One pre-existing integration-test failure (`WgcCaptureSourceSmokeTests.Attaches_To_Notepad_And_Receives_Frame`, `HRESULT 0x80070057` from `EnsureNv12RingAndConverter`) reproduces on a clean tree — unrelated to observability.

- [x] **Step 3: Commit** *(no commit needed — test was already committed in Phase 1.)*

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
