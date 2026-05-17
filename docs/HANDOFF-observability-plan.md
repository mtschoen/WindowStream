# Handoff — Server + Viewer Observability Plan

**Date paused:** 2026-05-17 (second pause this day)
**Branch:** `main`
**Plan file:** `docs/superpowers/plans/2026-05-17-server-viewer-observability.md` (26 tasks across 7 phases)

## TL;DR for the next session

Resume by dispatching **Task 8 (Wire Serilog + sinks in `MauiProgram.cs`)** with a Sonnet subagent. Phase 1 (Core foundation) and most of Phase 2 (server sinks + reducer) are now complete and tested. Phase 2 finishes at T8, then Phase 3 (dashboard VM/XAML) starts at T9.

## Commits since the previous handoff

Previous handoff was at `60f19df docs: handoff for paused observability plan (T1-T3 done)`.

Plan execution + drift-correction commits on top (13 commits, in order):

```
10658a2 refactor(core): route CoordinatorLauncher through Diagnostics façade
b17590e docs: mark T4 complete in observability plan
8e3fe1f build(server): add Serilog file + compact-json sinks
fb4992f docs: mark T5 complete in observability plan
66a4a90 fix(server-tests): unblock SessionViewModelTests build (T4 fallout)
80425f6 feat(server): InAppDashboardSink ring buffer with OnEvent fan-out (T6)
846862e docs: mark T6 complete in observability plan
b866f85 test(core): cover ViewerConnected/Disconnected event invocation paths
1137acd docs: correct T4's coverage-restoration note in plan
2bd4817 feat(server): state-board reducer with per-stream rows
a66b865 docs: mark T7 complete + update plan to use renamed Fps/Kbps properties
cb9ac6e fix(server): reducer must consume WindowAppeared/Disappeared, not unused ServerHelloSent
f30d57e docs: revert T7 plan-body drift around WindowCount source
```

## Status snapshot

| Task | Status | Notes |
|------|--------|-------|
| **T1** PipelineEvent + Severity | ✅ Done | Prior session. 19 record subtypes. |
| **T2** Diagnostics façade | ✅ Done | Prior session. ILogger-routing + Subscribe. |
| **T3** MEL.Abstractions on Core | ✅ Done | Prior session (absorbed into T2). 8.0.0 to match net8.0 target. |
| **T4** Refactor CoordinatorLauncher | ✅ Done | Required new `IWorkerHandle.ProcessId`, `StreamStartedEventArguments.WorkerProcessId`, and `CoordinatorControlServer.ViewerConnected`/`ViewerDisconnected` events (+ EventArgs files). 14 files changed. |
| **T4 follow-up** Coverage of viewer events | ✅ Done | Plan claimed T5 addresses this; it didn't. Added 2 tests in `CoordinatorControlServerTests` covering the new event-fire branches. |
| **T5** Serilog packages on server | ✅ Done | Serilog bumped 4.1.0 → **4.2.0** (4.1.0 was a plan slip; floor required by `Serilog.Extensions.Logging 9.0.0`). |
| **T6** InAppDashboardSink | ✅ Done | `LogEntry` record + ring-buffer sink + 3 tests. Also rewrote the four `SessionViewModelTests` that referenced removed `SessionStatus.Idle/Streaming`, plus added `ServerDashboardViewModelTests` to restore the server-side 100% gate. |
| **T7** State board reducer | ✅ Done | `StageStatus` + `StreamStateRow` + `ServerStateReducer` + 7 tests (5 plan + 2 lock-in). Required a drift correction: an earlier draft swapped `WindowAppeared/Disappeared` for `ServerHelloSent`, but `ServerHelloSent` has no production emitter — fix landed in `cb9ac6e`. |
| **T8** Wire Serilog in `MauiProgram.cs` | **⏭ Next up** | Composition of Diagnostics + 3 sinks (Debug, rolling JSONL file, `InAppDashboardSink`) in MAUI startup. See plan body for full text. |
| **T9–T26** | ⏸ Pending | See plan file. |

Test totals at HEAD:
- **Core:** 338/338 passing, 100% line / 100% branch / 100% method.
- **Server:** 27/27 passing, 100% line / 100% branch / 100% method.
- **Server + CLI** both build cleanly (0 warnings, 0 errors).

## What changed vs. the original plan (deviations cumulative across all sessions)

The handoff from session 1 already covered T1–T3 deviations. New deviations from this session (T4–T7):

5. **`IWorkerHandle.ProcessId`** added in T4 to propagate PID from `WorkerProcessLauncher.WorkerHandle.process.Id` up to `StreamStartedEventArguments.WorkerProcessId`. The original plan assumed PID was already on the event. Test fakes (`FakeWorkerHandle`, `FakeWorkerProcessLauncher`) return `0`.

6. **`CoordinatorControlServer.ViewerConnected` / `ViewerDisconnected` events** added in T4 with sibling `ViewerConnectedEventArguments` / `ViewerDisconnectedEventArguments` files. The plan referenced viewer-accept hooks that didn't exist. `CoordinatorLauncher.LaunchAsync` subscribes to both and emits the corresponding `PipelineEvent`s.

7. **`Microsoft.Extensions.Logging.Console` 8.0.0** added to `src/WindowStream.Cli/WindowStream.Cli.csproj` in T4 so the CLI can construct a real `ILogger` to satisfy `Diagnostics`'s constructor.

8. **`MauiProgram.cs`** modified in T4 (not mentioned in plan body) — it called `CoordinatorLauncher` with the old `(int, TextWriter)` signature and wired the dashboard via the four deleted callback properties. Rewired to use `Diagnostics.Subscribe` from an `ILoggerFactory` pulled out of MAUI's service collection. The VM's previous `ReportActiveStreams` / `ReportAvailableWindows` no longer have a direct call site — they get repopulated in T9 when the reducer feeds the VM.

9. **Plan's T4 note "coverage may dip; addressed in T5" was wrong** — T5 is just Serilog package adds for the server project, doesn't touch Core coverage. Fixed in commit `b866f85` (Core viewer-event tests) + `1137acd` (plan correction note).

10. **`SessionViewModelTests` and `ServerDashboardViewModelTests`** — pre-existing build break (the `SessionStatus` enum was renamed to `Starting/Serving/Stopped` before this plan started; tests still referenced `Idle/Streaming`) blocked the server test project from compiling. T6 rewrote the four affected tests and added 9 new tests for `ServerDashboardViewModel` to restore the 100% gate. The first commit (`66a4a90 fix(server-tests): unblock SessionViewModelTests build`) is a pre-T6 unblock; the second (`80425f6`) is the actual T6 sink work + the rewrite.

11. **Serilog 4.2.0, not 4.1.0** — `Serilog.Extensions.Logging 9.0.0`'s floor is `Serilog >= 4.2.0`. Picked the smallest delta. Documented in the plan file directly under T5's heading.

12. **T7 `StreamStateRow` field renames** — plan body used `EncodeFps`, `EncodeKbps`, `CurrentFps`, `CurrentKbps`. Real reducer uses `EncodeFramesPerSecond`, `EncodeBitrateKilobitsPerSecond`, `MeasuredFramesPerSecond`, `MeasuredBitrateKilobitsPerSecond` — full-words rule applied + `Measured*` qualifier inherited from `FramesFlowing.MeasuredFramesPerSecond`.

13. **T7 reducer uses a private `UpdateStream(streamId, update)` helper** instead of the plan's inline `when State.Streams.ContainsKey(...)` guards. Same behavior, less duplication.

14. **T7 reducer drift was corrected** — the executor initially swapped the plan's `WindowAppeared`/`WindowDisappeared` arms for a single `ServerHelloSent` arm under "more authoritative" framing. Grep-verified that `ServerHelloSent` has zero production emitters; reverted to the plan's increment/decrement design + added two lock-in tests. Fix in `cb9ac6e`, plan-body revert in `f30d57e`.

## Operational notes for the next session

### Pre-dispatch checklist for T8

Per the plan: T8 modifies `src/WindowStreamServer/MauiProgram.cs` substantially. Key context for the executor:

- The current file already constructs `Diagnostics` from a `Microsoft.Extensions.Logging.ILogger` (added in T4's deviation #8). T8 needs to swap that ILogger for a Serilog-backed one with three sinks: existing Debug sink (already present), new rolling JSONL file sink (`Serilog.Sinks.File` + `Serilog.Formatting.Compact`), and the new `InAppDashboardSink` (`src/WindowStreamServer/Observability/InAppDashboardSink.cs`).
- The reducer (`ServerStateReducer` from T7) is NOT yet wired anywhere — T9 wires it to the dashboard VM via `InAppDashboardSink.OnEvent`. T8 should leave the reducer alone.
- File-sink path needs a writable directory on Windows. Recommended: `Path.Combine(FileSystem.AppDataDirectory, "logs", "windowstream-.jsonl")` (MAUI's `FileSystem.AppDataDirectory` gives the per-app writable spot). Plan body has the exact `WriteTo.File(...)` shape.
- The `InAppDashboardSink` must be registered as a singleton in DI so both the Serilog pipeline AND the dashboard VM (T9) can resolve the SAME instance — otherwise the VM subscribes to a sink no events flow through.

### How to dispatch

Same pattern as T4–T7: `Agent` with `subagent_type: general-purpose` + `model: sonnet`. Paste the plan body for T8 (`docs/superpowers/plans/2026-05-17-server-viewer-observability.md` from `### Task 8: Wire Serilog + sinks in MauiProgram.cs` through the next `###` heading). Pre-flag the `MauiProgram.cs` current state from T4's deviation (don't expect a clean slate — the file already wires Diagnostics, just with a Console ILogger instead of Serilog).

### Recurring quality-review patterns

These have caught issues in T1, T4, T6, and T7. Apply unconditionally to every server-side task:

- **Abbreviations.** Scan new identifiers against AGENTS.md's "full words" rule. `args` → `eventArguments`, `cfg` → `configuration`, `ev`/`evt` → `pipelineEvent`, etc. The plan body has stale abbreviations in several spots — always check `PipelineEvent.cs` for the real property names before transcribing the plan code.
- **`required` on non-nullable refs** — needs `[SetsRequiredMembers]` on any constructor that satisfies it.
- **Subagent silent design swaps.** If a subagent reports "the plan's approach was fragile, I used X instead", VERIFY by greping for production emitters/callers of both old and new APIs. The T7 `ServerHelloSent` swap looked plausible but was buggy (see deviation #14). Don't accept "more robust" framing on faith.
- **LSP stale diagnostics.** New test files will report missing `Xunit` / `FactAttribute` / `Serilog` types in LSP for several minutes after creation. `dotnet build` and `dotnet test` are ground truth — ignore the LSP noise.
- **Coverage gate scope** — server tests are scoped to `<Include>[WindowStreamServer]*ViewModels*</Include>`, so adding new types in `Observability` doesn't move the server gate. Core tests have no `<Include>` restriction — all production assemblies count toward the 100% gate.

### Things to skip

- **Don't use `--filter` when verifying coverage gates.** Coverlet measures gate against the assembly under test; filtering only runs the filtered tests so non-filtered code shows 0% and the gate fires. Run the full suite for gate checks: `dotnet test tests/WindowStream.Core.Tests/` and `dotnet test tests/WindowStream.Server.Tests/`.

## What didn't ship this session

Nothing was started past Task 7. The viewer side is untouched (Phase 4+ starts at T11). The user's in-progress GXR picker MVP work (per WIP baseline `d56bd59`) is still in the tree — it's the foundation the observability plan extends, not part of it.

## Cost-discipline reminder

User asked for Sonnet subagents for plan execution. Opus stays on the orchestrator role + design + plan-writing. Memory `~/.claude/notes/feedback_prefer_sonnet_subagents.md` captures this. Session pattern: orchestrator (Opus) reads ground-truth code to write a precise prompt with deviation-flags; Sonnet executor does the bulk edits; orchestrator verifies via grep/build before accepting. Cost-effective and catches drift early — see new memory `feedback_subagent_silent_design_swaps.md`.
