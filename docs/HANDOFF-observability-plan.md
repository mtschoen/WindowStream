# Handoff — Server + Viewer Observability Plan

**Date paused:** 2026-05-17
**Branch:** `main`
**Plan file:** `docs/superpowers/plans/2026-05-17-server-viewer-observability.md` (26 tasks across 7 phases)

## TL;DR for the next session

Resume by dispatching **Task 4 (Refactor `CoordinatorLauncher` to use `Diagnostics`)** with a Sonnet subagent. The Core foundation (PipelineEvent + Diagnostics + MEL.Abstractions ref) is complete and tested. Skip Task 3 — it was absorbed into Task 2's commit.

## Commits since WIP baseline

WIP baseline: `d56bd59 wip: gxr picker MVP scaffolding baseline`

Plan execution commits on top:

```
5e1231c feat(core): add Diagnostics façade routing PipelineEvent to ILogger
dae0e45 refactor(core): rename Pipeline abbreviations + add required modifiers
5d52fc5 chore(core): remove redundant using System from PipelineEvent
3889f03 feat(core): add PipelineEvent hierarchy and Severity enum
```

All four merge changes to `WindowStream.Core` only — no server-side or viewer-side files touched yet. Plan file's Task 1 + Task 2 checkboxes are flipped.

## Status snapshot

| Task | Status | Notes |
|------|--------|-------|
| **T1** PipelineEvent + Severity | ✅ Done | 19 record subtypes, 20 tests. Quality review rounds: rename `Pid`/`Fps`/`Kbps` → full words, add `required` + `[SetsRequiredMembers]`. |
| **T2** Diagnostics façade | ✅ Done | `Diagnostics(ILogger).Report(PipelineEvent)` + `Subscribe(Action)`. 10 tests (absorbed T25's Subscribe test). |
| **T3** Add MEL.Abstractions to Core | ✅ Done (in T2) | Agent had to add it for T2 to compile. Used **8.0.0** (matches `net8.0` target), not the plan's 10.0.0. |
| **T4** Refactor `CoordinatorLauncher` to `Diagnostics` | **⏭ Next up** | Plan body has step-by-step. Reads existing code in `src/WindowStream.Core/Hosting/CoordinatorLauncher.cs`. |
| **T5–T26** | ⏸ Pending | See plan file. |

Full test suite at HEAD: **336/336 passing, 100% line / 100% branch coverage** in `tests/WindowStream.Core.Tests/`.

## What changed vs. the original plan

These are the only deviations the executor needs to know about so downstream tasks don't get confused:

1. **PipelineEvent uses explicit constructors with init-only properties**, not positional record inheritance. Reason: `(int? StreamId)` on the base + `(int StreamId, …)` on derived hits CS8866. Public API (`new PipelineEvent.WorkerSpawnFailed(StreamId: 7, Exception: ex)` + pattern matching + `.Severity` + `.StreamId`) is unchanged.

2. **Renamed properties (from plan original):**
   - `WorkerSpawned.Pid` → `WorkerSpawned.ProcessId`
   - `EncodeStarted.Fps` → `EncodeStarted.TargetFramesPerSecond`
   - `EncodeStarted.Kbps` → `EncodeStarted.BitrateKilobitsPerSecond`
   - `FramesFlowing.Fps` → `FramesFlowing.MeasuredFramesPerSecond`
   - `FramesFlowing.Kbps` → `FramesFlowing.BitrateKilobitsPerSecond`
   - `Hwnd` kept as-is (matches codebase precedent in `WorkerArguments.Hwnd` etc.).

   **Downstream tasks must use the new names** — especially T4 (CoordinatorLauncher emits these events) and the viewer-side equivalents (T12 should mirror the same naming).

3. **`[SetsRequiredMembers]`** is on all PipelineEvent constructors for subtypes with `required` properties (Exception-carrying + string-carrying). Required because Roslyn doesn't infer required-satisfaction from constructor body assignments alone — the attribute is mandatory.

4. **Switch shape in `Diagnostics.Report`:** uses two explicit arms (`Warning`, `Error`) + `_ => LogLevel.Information` rather than three explicit arms. Reason: three-explicit form generates CS8524 on net8.0 because Roslyn can't prove enum exhaustiveness without a default. The discard arm IS covered (every `Severity.Info` event hits it).

5. **MEL.Abstractions version is 8.0.0**, not 10.0.0 as the plan stated. The Core project targets `net8.0` + `net8.0-windows10.0.19041.0`. If a downstream task that crosses into MAUI server code (T8) needs 10.0.0 for the MAUI host, fine — but the Core project stays on 8.0.0.

## Operational notes for the next session

### Pre-dispatch checklist for T4

Per the plan: T4 modifies `src/WindowStream.Core/Hosting/CoordinatorLauncher.cs` substantially. The current file (read at session start) shows:
- Constructor takes `(int tcpPort, TextWriter output)` — T4 replaces with `(int tcpPort, Diagnostics diagnostics)`.
- Several `Action<…>?` callback properties — T4 removes them (state will live in the reducer).
- `output.WriteLine` banner — T4 replaces with `diagnostics.Report(new PipelineEvent.Listening(…))`.
- Dead `ResolveEncoderOptions` + `ProbeCaptureSizeAsync` — T4 deletes (the live path uses `ResolveEncoderOptionsFromDescriptor`).
- Per-stream lifecycle events (T4 Step 5/6) — needs reading `CoordinatorControlServer.StreamStartedEventArgs` to confirm what fields are available.

### How to dispatch

Same pattern as T1/T2: `Agent` with `subagent_type: general-purpose` + `model: sonnet`. Paste the plan header + the full Task 4 text from `docs/superpowers/plans/2026-05-17-server-viewer-observability.md` (lines covering "### Task 4"). Tell the agent to read `CoordinatorLauncher.cs` end-to-end before editing, and to confirm `WorkerSpawnedEventArgs` has `WorkerProcessId` before emitting `WorkerSpawned`.

### Quality-review patterns to repeat

T1's quality review caught two recurring categories worth checking on every server-side task:
- **Abbreviations** — scan new identifiers against AGENTS.md's "full words" rule. Local precedent: `WorkerArguments.Hwnd` (kept), `SessionViewModel.BitrateKilobitsPerSecond` (full words). When in doubt, full word.
- **`required` on non-nullable refs** — if a new record/class has non-nullable reference-typed init properties, they need `required` + `[SetsRequiredMembers]` on the constructor.

### Things to skip

- **Don't use `--filter` when verifying the coverage gate.** Coverlet measures gate against the assembly under test; filtering only runs the filtered tests so non-filtered code shows 0% and the gate fires. Run the full `dotnet test tests/WindowStream.Core.Tests/` for the gate check.
- **Ignore LSP `Xunit`/`Microsoft.Extensions.Logging` stale diagnostics** on the new test/source files. `dotnet build` and `dotnet test` are ground truth.

## What didn't ship this session

Nothing was started past Task 3. The viewer side is untouched. The user's in-progress GXR picker MVP work (per WIP baseline `d56bd59`) is in the tree but is *not* part of this plan — those files (UnifiedStreamingActivity, WindowDrawerOverlay, ServerDashboardViewModel skeleton, StatusColorConverter, MainPage.xaml/.cs, MauiProgram.cs edits, AndroidManifest tweaks) are the foundation the observability plan extends.

## Cost-discipline reminder

User asked for Sonnet subagents for plan execution. Opus stays on the orchestrator role + design + plan-writing. Memory `~/.claude/notes/feedback_prefer_sonnet_subagents.md` captures this.
