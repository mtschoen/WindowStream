# Handoff — Server + Viewer Observability Plan

**Date paused:** 2026-05-18
**Branch:** `main`
**Plan file:** `docs/superpowers/plans/2026-05-17-server-viewer-observability.md`
**HEAD:** `e0bc6bf` (this handoff + wrap-hygiene push will land on top)

## TL;DR for the next session

Resume at **Task 16 — plant trees in `WindowStreamViewerApplication`**. Phase 4 (viewer foundation) is essentially done; T16 is a small `onCreate()` edit that adds `Timber.plant(...)` for the two custom trees. After T16, Phase 4 closes and **Phase 5 (T17–T20) starts** — viewer call-site instrumentation across `UnifiedStreamingActivity`, `XrDemoActivity`, GXR `MainActivity`, `MediaCodecDecoder`, `MultiStreamControlClient`, plus a UDP-stall watchdog. That's the natural fan-out moment for Sonnet subagent dispatch.

## Commits since the previous handoff (`dbaa84a`)

```
e0bc6bf feat(viewer): FileLoggingTree with daily rotation + retention (T15)
2937b45 feat(viewer): InAppBufferTree exposing SharedFlow of LogEvent (T14)
cd081a0 feat(viewer): Diagnostics façade + LogEvent with ThreadLocal payload bridge (T13)
```

(Plus a wrap-hygiene commit on top of these from `/wrap` 2026-05-18.)

## Status snapshot

| Task | Status | Notes |
|------|--------|-------|
| **T1–T9** | ✅ Done | Core observability types + server sinks + reducer + MauiProgram wiring. |
| **T10** Server state board | ✅ Done | XAML rewritten; visual smoke deferred to user. |
| **T11** Timber dep | ✅ Done | Libs.versions.toml + app/build.gradle.kts. |
| **T12** `PipelineEvent` sealed class | ✅ Done | 22-case exhaustive test. |
| **T13** `Diagnostics` façade + `LogEvent` | ✅ Done (`cd081a0`) | Object with `report(event)`, ThreadLocal payload + event bridge, sealed-class dispatch. **Deviations:** added `pipelineEvent: PipelineEvent? = null` to `LogEvent` (collapses T22's anticipated refactor); `throwableOf` widened `private`→`internal` to test its dead `else` arm; added 8-case `DiagnosticsTest` + 2-case `LogEventTest` for Kover 100%. |
| **T14** `InAppBufferTree` | ✅ Done (`2937b45`) | `Timber.Tree` exposing `SharedFlow<LogEvent>`. **Deviation:** `InAppBufferTree.log` populates `pipelineEvent` from `Diagnostics.currentEvent.get()` — same anticipatory pre-fix as T13's `LogEvent` field. Test expanded from plan's 1 case to 6 (severity arms + streamId + raw-Timber `?: "Log"` fallback + default-replay ctor). Followed plan's TDD discipline (verify-FAIL → impl → verify-PASS) before expanding for coverage. |
| **T15** `FileLoggingTree` | ✅ Done (`e0bc6bf`) | JSONL file writer with daily rotation + retention. **Six deviations** (all in commit body): inlined `ZoneOffsetUtc` helper, replaced `android.util.Log.e` with `System.err`, sentinel non-null `writer` field, reordered `rotateIfNeeded` to open-new-before-close-old, refactored `value?.toString() ?: ""` to `(value ?: "").toString()` (dead-branch fix), `purgeOldFiles` widened to `internal`. Test expanded from plan's 2 cases to 12. |
| **T16** Plant trees in `WindowStreamViewerApplication` | **⏭ Next up** | Small `onCreate()` edit. Plan body has full source. Closes Phase 4. |
| **T17–T20** Viewer instrumentation (Phase 5) | ⏸ Pending | Refactor call sites in 6 source files to emit `PipelineEvent`s + UDP-stall watchdog. Subagent dispatch (Sonnet) recommended — parallel-friendly. |
| **T21–T23** Viewer UI (Phase 6) | ⏸ Pending | `ViewerStateReducer` + `ObservabilityOverlay` panel + GXR `SpatialPanel`. Note: T22's "ThreadLocal won't cross coroutine boundaries" refactor is **already done** (T13's `LogEvent.pipelineEvent` field + T14's populate-from-ThreadLocal). The collector can read `event.pipelineEvent` directly. |
| **T24–T26** Cleanup + smoke (Phase 7) | ⏸ Pending | `AGENTS.md` diagnostics section, `Diagnostics.Subscribe` Core test, e2e smoke. |

Test totals at `e0bc6bf`:
- **.NET (Coverlet):** Core 338/338, Server 44/44, Integration 38/41 (3 skipped). 100% line/branch/method across Core, Server, CLI.
- **Viewer (Kover, JaCoCo backend):** 271 unit tests passing (+28 from baseline at `dbaa84a`: +8 Diagnostics, +2 LogEvent, +6 InAppBufferTree, +12 FileLoggingTree). 100% line/branch on both portable and gxr flavors.

## What changed vs. the original plan (this session's deviations)

This session ran T13–T15 inline on Opus. Twelve documented deviations, all pre-flagged in the prior handoff or surfaced as Kover-gate-driven necessities. All are in the commit messages — search `git log --all -- 'viewer/**/observability/**'` for full rationale per commit.

The pattern across deviations: the plan body's Kotlin source assumed it was running in an Android-runtime test environment but the viewer's tests run on JVM with `android.jar`'s stubs. Two flavors of mismatch surfaced:

1. **`android.util.Log` throws Stub! on JVM** (T15). Substituted `System.err` in catch blocks. Saved as cross-project reference at `~/.claude/notes/reference_android_log_jvm_stub.md`.

2. **JaCoCo dead-branch counting on Kotlin idioms** (T13, T14, T15). Three patterns:
   - Chained `?.` + `?:` generates 4 branches when only 2 are reachable. Collapse via `(x ?: default).foo()`.
   - Nullable field that's "only briefly null" → use a sentinel non-null instance so the safe-call branch isn't dead.
   - `private` defensive `else -> null` arm that can't be reached → widen to `internal` and test it directly.

   Saved as cross-project idiom at `~/.claude/notes/idioms_kotlin_jacoco_coverage.md`. Likely to recur in Phase 5 when adding instrumentation across `UnifiedStreamingActivity` etc.

## Operational notes for the next session

### Pre-dispatch checklist for T16

- T16 modifies `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/WindowStreamViewerApplication.kt`.
- Plant `InAppBufferTree` (kept on the Application as a `lateinit val`) and `FileLoggingTree` (constructed with `filesDir` resolved via `getExternalFilesDir(null)` or similar).
- `WindowStreamViewerApplication` is currently in the Kover exclusion list (`build.gradle.kts:114`), so adding the `lateinit val inAppBufferTree` keeps the class Android-runtime-bound and the exclusion stays correct. Don't remove the exclusion.
- **Build verification:** `cd viewer/WindowStreamViewer && ./gradlew.bat :app:koverVerifyPortableDebug :app:koverVerifyGxrDebug` — both must PASS.

### Phase 5 dispatch recommendation (T17–T20)

The four tasks edit 6 source files independently:
- `UnifiedStreamingActivity.kt` (T17, ~250 lines)
- `XrDemoActivity.kt` + GXR `MainActivity.kt` (T18, ~200 lines combined)
- `MediaCodecDecoder.kt` + `MultiStreamControlClient.kt` (T19, ~300 lines combined)
- `UdpStalled` watchdog (T20, new code in `UdpTransportReceiver` or sibling)

This is the parallelization sweet spot the prior handoffs flagged. Dispatch each as a Sonnet subagent with:
- A pre-flight read of `~/.claude/notes/idioms_kotlin_jacoco_coverage.md` + `~/.claude/notes/reference_android_log_jvm_stub.md`
- Instructions to keep the Kover gate green (will require expanding tests for each instrumented method)
- Per-file commit at task completion
- Final orchestrator review for cross-cutting consistency before Phase 6 starts

### Recurring deviation-flags to carry forward

- **Plan body's Kotlin source is not Kover-gate-aware.** Each plan-body method needs to be reviewed against the four idioms in `idioms_kotlin_jacoco_coverage.md` before lifting verbatim. Same with `android.util.Log` calls.
- **Plan checkbox tick + phase-prune cadence.** Tick `- [ ]` → `- [x]` AS PART OF the task's commit. After T16's commit, prune Phase 4 from the plan body at the start of Phase 5's first commit.
- **Kover 100% gate (`build.gradle.kts:281–287`)** applies to all new code under `src/main/`. Either cover it or add a documented exclusion with rationale. The existing exclusion list (lines ~104–278) is the model.
- **Always regenerate the XML report before inspecting.** `koverVerify` and `koverXmlReport` write different artifacts; the latter caches and lies if not explicitly run.

## What didn't ship this session

- T16 itself (the trivial Application.onCreate edit) — deferred so this handoff can frame Phase 5 dispatch cleanly.
- Visual UI confirmation of the T10 server state board (still deferred to user).
- Coverage-command callout in `CLAUDE.md` / `AGENTS.md` referencing `TEST-REPORT.md` — light docs cleanup.

## Cost-discipline reminder

Three Opus turns landed T13, T14, T15 inline. For Phase 5's four tasks across 6 source files, **Sonnet subagent dispatch earns its keep** — the per-task work is mechanical instrumentation that benefits from parallelism + code-review feedback loop. Use Opus orchestrator + Sonnet executor/reviewer per `~/.claude/notes/feedback_prefer_sonnet_subagents.md`.
