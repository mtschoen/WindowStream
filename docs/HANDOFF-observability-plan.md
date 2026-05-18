# Handoff — Server + Viewer Observability Plan

**Date paused:** 2026-05-17 (fourth pause this day)
**Branch:** `main`
**Plan file:** `docs/superpowers/plans/2026-05-17-server-viewer-observability.md`
**HEAD:** `dbaa84a` (ahead of `gitea/main` by 4 — wrap commit will push to 5)

## TL;DR for the next session

Resume at **Task 13 (Viewer `Diagnostics` object + `LogEvent` + ThreadLocal payload bridge)**. Phase 3 (server UI, T10) is fully landed; Phase 4 is partly done — T11 (Timber dep) + T12 (`PipelineEvent` sealed class with 22-case exhaustive test) — and T13–T16 remain.

T13 introduces a Kotlin `object Diagnostics` that routes `PipelineEvent` through Timber and stashes the typed event in a `ThreadLocal<PipelineEvent?>` so custom Trees can pick it back up. The plan body for T13 is in the plan file (look for `### Task 13`).

## Commits since the previous handoff (`47d9405`)

```
dbaa84a docs: add TEST-REPORT.md baseline at ead4fc8
ead4fc8 feat(viewer): add PipelineEvent sealed hierarchy and Severity enum (T12)
818f4cc build(viewer): add Timber 5.0.1 dependency (T11)
581bb93 feat(server): state-board UI with per-stream rows and event log (T10)
```

(Plus a wrap-hygiene commit added by `/wrap` after this handoff is written.)

## Status snapshot

| Task | Status | Notes |
|------|--------|-------|
| **T1–T9** | ✅ Done | See prior handoff (`HEAD=5414341`) for full notes. Core observability types + server sinks + reducer + MauiProgram wiring all landed before this session. |
| **T10** `MainPage.xaml` state board | ✅ Done (`581bb93`) | New `StageStatusGlyphConverter`; XAML rewritten with top-level state board, per-stream `CollectionView` (binds `State.Streams.Values`), event-log `CollectionView` (binds `RecentEvents`), "Open log folder" button. Visual smoke NOT done — build clean, MAUI process launched + the JSONL log captured `Listening`+`WindowAppeared` (side-channel proof bindings resolved). |
| **T11** Timber dep | ✅ Done (`818f4cc`) | Timber 5.0.1 added via `libs.versions.toml` + `app/build.gradle.kts`. `:app:assemblePortableDebug` BUILD SUCCESSFUL in 56s. |
| **T12** Viewer `PipelineEvent` | ✅ Done (`ead4fc8`) | `viewer/.../observability/PipelineEvent.kt` + `PipelineEventTest.kt`. **Deviation: 22-case exhaustive test rather than the plan's 3 cases** — required to keep Kover's 100% line+branch gate green. All 22 PASS; `:app:koverVerifyPortableDebug` PASS. |
| **T13** Viewer `Diagnostics` object + `LogEvent` + ThreadLocal payload bridge | **⏭ Next up** | Plan body has full source. Note Phase 6's late-binding `ThreadLocal` caveat (line ~1370 of plan, search for "ThreadLocal trick won't actually work across coroutine boundaries") — the fix is to push the `PipelineEvent` onto `LogEvent` itself; that refactor is anticipated and listed inline in T22 Step 2 of the plan body. Easier to land it cleanly in T13 from the start (add `val pipelineEvent: PipelineEvent? = null` to `LogEvent.kt` and populate it in `InAppBufferTree.log`). |
| **T14** `InAppBufferTree` | ⏸ Pending | Custom `Timber.Tree()` exposing a `SharedFlow<LogEvent>`. |
| **T15** `FileLoggingTree` | ⏸ Pending | Daily rotation + retention; plan suggests Robolectric-free `@TempDir` tests. |
| **T16** Plant trees in `WindowStreamViewerApplication` | ⏸ Pending | `Timber.plant(...)` in `onCreate`. **Note:** the existing application class is currently excluded from Kover (`build.gradle.kts:114`); adding the `inAppBufferTree` lateinit property keeps it Android-runtime-bound, so leaving the exclusion in place is correct. |
| **T17–T20** Viewer instrumentation (Phase 5) | ⏸ Pending | Refactor call sites in `UnifiedStreamingActivity`, `XrDemoActivity`, GXR `MainActivity`, `MediaCodecDecoder`, `MultiStreamControlClient` to emit `PipelineEvent`s. Plus a UDP-stall watchdog. |
| **T21–T23** Viewer UI (Phase 6) | ⏸ Pending | `ViewerStateReducer` + `ObservabilityOverlay` panel + GXR `SpatialPanel`. |
| **T24–T26** Cleanup + smoke (Phase 7) | ⏸ Pending | `AGENTS.md` diagnostics section, `Diagnostics.Subscribe` core test, e2e smoke. |

Test totals at `dbaa84a`:
- **.NET (Coverlet):** Core 338/338, Server 44/44, Integration 38/41 (3 skipped). 100% line/branch/method across Core, Server, CLI.
- **Viewer (Kover, JaCoCo backend):** 243 unit tests passing. 100% line (661/661), 100% branch (225/225) on portable debug. 73/4508 instructions missed (synthetic bytecode — not gated). Baseline now checked in at repo-root `TEST-REPORT.md`.

## What changed vs. the original plan (this session's deviations)

(Earlier deviations are catalogued in prior wrap commits + plan-body inline notes. New deltas from this session:)

21. **T10 — plan body had two typos: `pages:StreamStateRow` and `CurrentFps`.** Real types are in `WindowStream.Server.Observability` namespace and the property is `MeasuredFramesPerSecond`. Fixed inline as trivial typo per the executing-plans drift policy. Flagged in the T10 commit message.

22. **T10 — stream-row title binds `WindowId` directly, not `Title`.** The reducer never populates `StreamStateRow.Title` (it has no window-title context after `OpenStreamReceived`). The plan's `{Binding Title, StringFormat='windowId / {0}'}` would render `"windowId / "` — useless. Replaced with `{Binding WindowId, StringFormat='windowId {0}'}`. A future improvement is to thread `WindowAppeared.Title` through to the corresponding `StreamStateRow` in the reducer, which is out of this plan's stated scope but worth noting for any future Phase-2 follow-up.

23. **T10 — visual UI smoke deferred.** MAUI desktop window; agent can't see it. Verified via build + process liveness + JSONL log capturing `Listening`+`WindowAppeared`. **Action for the user:** next time you run `dotnet run --project src/WindowStreamServer -f net10.0-windows10.0.19041.0`, eyeball the state board for layout sanity.

24. **T12 — exhaustive test (22 cases) instead of plan's 3.** Required by Kover's 100% line+branch gate (`viewer/.../app/build.gradle.kts:281-287`). Test instantiates every event subclass and reads every property. Verified `:app:koverVerifyPortableDebug` PASS.

25. **T12 — verify-FAIL step skipped to save one gradle cycle.** Test + impl written together; verify-PASS at Step 4 served as the meaningful gate. Discipline tradeoff; flagged in T12 commit message.

26. **TEST-REPORT.md baseline added** (`dbaa84a`) — first checked-in coverage baseline for the repo, covering both .NET (Coverlet) and viewer (Kover) sides. Per superpowers:maintaining-full-coverage. The CLAUDE.md hasn't been updated to mention the coverage command yet — light cleanup item for a future pass.

27. **Plan housekeeping done in `/wrap`:** Phase 3 pruned from plan body (was overdue since T11's commit per executing-plans phase-boundary cadence). T11+T12 step-checkboxes ticked retroactively.

## Operational notes for the next session

### Pre-dispatch checklist for T13

- T13 creates two files under `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/`:
  - `LogEvent.kt` (data class with `timestamp`, `severity`, `eventType`, `streamId`, `message`, `payload`, `throwable`)
  - `Diagnostics.kt` (`object Diagnostics` with `report(event)` + internal `ThreadLocal<PipelineEvent?>` and `ThreadLocal<Map<String, Any?>>`)
- **Recommended pre-fix:** add `val pipelineEvent: PipelineEvent? = null` to `LogEvent.kt` from the start. The plan acknowledges (in T22 Step 2's "the ThreadLocal trick won't actually work across coroutine boundaries" note) that the bridge needs to be widened anyway; landing it now avoids a follow-up edit to a recently-written file. No regressions because no one reads the field yet at T13.
- T13 has no test step in the plan body. To honor the 100% Kover gate, write a unit test that exercises `Diagnostics.report(...)` for an INFO, a WARNING, and an ERROR (the three `severity` branches inside `report()`), plus reads from `currentPayload` / `currentEvent` to cover the `ThreadLocal` initializers. Reasonable subset — the full ThreadLocal pickup is properly tested at T14 (`InAppBufferTreeTest`).
- **Build verification:** `cd viewer/WindowStreamViewer && ./gradlew :app:koverVerifyPortableDebug` is the one command that exercises tests + the 100% gate together. ~10s incremental.

### How to execute

This session executed T10–T12 **inline in the orchestrator** rather than via sonnet subagents (as the prior session's handoff recommended). Inline was fine for these tasks — each one was 1–3 files of straightforward writing with no parallel work to dispatch. For T13, inline is again fine. **The subagent dispatch pattern from the earlier handoff is still worth using for T17–T20** (viewer-instrumentation tasks that touch ~6 source files and benefit from review by sonnet `code-reviewer`).

### Recurring deviation-flags to carry forward

- **Plan body's checkbox-tick + phase-prune cadence is easy to miss.** Tick `- [ ]` → `- [x]` AS PART OF the task's commit; prune the completed phase at the START of the next phase's first commit. This session let both drift and folded them into `/wrap`. Future sessions: do them inline.
- **Kover 100% gate (`build.gradle.kts:281-287`)** applies to all new code under `viewer/WindowStreamViewer/app/src/main/`. Either cover it or add a documented exclusion with rationale. The existing exclusion list (lines ~104–278) is the model.
- **LSP false-positives on MAUI types** are noise; trust `dotnet build`. Memorialized at `~/.claude/notes/reference_maui_lsp_false_positives.md`.
- **MAUI desktop smoke needs user eyes.** No agent visual proof; use background-launch + process liveness + log-file capture as a proxy and flag the deferred eyeball in the commit message.

## What didn't ship this session

- Visual UI confirmation of the T10 state board (deferred to user).
- Threading `WindowAppeared.Title` through to `StreamStateRow.Title` (out of plan scope; nice-to-have).
- Viewer Phase 4 finishing tasks (T13–T16) — the next pickup point.
- Coverage-command callout in `CLAUDE.md` / `AGENTS.md` referencing the new `TEST-REPORT.md` — light docs cleanup.

## Cost-discipline reminder

Prior handoff recommended Opus orchestrator + Sonnet executor/reviewer subagents. This session was on Opus throughout (no subagents) because the tasks were small and serial. For larger Phase-5/6 work that touches multiple call sites, the prior pattern (orchestrator-on-Opus, executor-on-Sonnet, reviewer-on-Sonnet) earns its keep again — see `~/.claude/notes/feedback_prefer_sonnet_subagents.md`.
