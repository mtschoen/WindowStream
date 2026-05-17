# Handoff — Server + Viewer Observability Plan

**Date paused:** 2026-05-17 (third pause this day)
**Branch:** `main`
**Plan file:** `docs/superpowers/plans/2026-05-17-server-viewer-observability.md` (26 tasks across 7 phases)
**HEAD:** `5414341` (ahead of `gitea/main` by 2)

## TL;DR for the next session

Resume by dispatching **Task 10 (`MainPage.xaml` state-board section)** with a Sonnet subagent. Phase 1 (Core foundation) and Phase 2 (server sinks + reducer + MauiProgram wiring) are now complete. Phase 3 starts at T10 with the XAML state-board markup + glyph converter, then T11 ships the viewer-side Timber dependency.

## Commits since the previous handoff

Previous handoff was at `0ca930c chore: wrap session hygiene`.

Plan execution + review-fix commits on top (2 commits, in order):

```
31c66d0 feat(server): wire Serilog sinks + sink-driven dashboard VM (T8+T9)
5414341 fix(server): address T8+T9 code-quality review
```

## Status snapshot

| Task | Status | Notes |
|------|--------|-------|
| **T1** PipelineEvent + Severity | ✅ Done | Prior session. 19 record subtypes. |
| **T2** Diagnostics façade | ✅ Done | Prior session. ILogger-routing + Subscribe. |
| **T3** MEL.Abstractions on Core | ✅ Done | Prior session (absorbed into T2). |
| **T4** Refactor CoordinatorLauncher | ✅ Done | Required new `IWorkerHandle.ProcessId`, `StreamStartedEventArguments.WorkerProcessId`, `CoordinatorControlServer.ViewerConnected`/`ViewerDisconnected` events. |
| **T5** Serilog packages on server | ✅ Done | Serilog 4.2.0, `Serilog.Extensions.Logging 9.0.0`, `Serilog.Sinks.File 6.0.0`, `Serilog.Formatting.Compact 3.0.0`. |
| **T6** InAppDashboardSink | ✅ Done | `LogEntry` record + ring-buffer sink + 3 tests. |
| **T7** State board reducer | ✅ Done | `StageStatus` + `StreamStateRow` + `ServerStateReducer` + 7 tests. |
| **T8** Wire Serilog in `MauiProgram.cs` | ✅ Done | Composition of Diagnostics + 3 sinks (Debug, rolling JSONL file, `InAppDashboardSink`) in MAUI startup. Bundled with T9 in a single commit (`31c66d0`) per the plan body's "defer commit until T9" note. |
| **T9** `ServerDashboardViewModel` subscribes to sink | ✅ Done | Full VM rewrite — derived properties off `reducer.State`, snapshot-replay in ctor, `OnEvent`+`ApplyEvent` paths via `MainThread.BeginInvokeOnMainThread` with synchronous fallback for headless tests. `LogEntryViewModel` added. Tests: 44/44 passing. |
| **T9 follow-up** Code-quality fix | ✅ Done | Commit `5414341`. Deleted two 148-line no-op `Assert.True(true)` tests left over from coverage debugging, narrowed `catch (Exception)` to `catch (COMException)` after verifying the actual headless throw type, dead `using` removed, test rename, factory extraction, comment fixes. Coverage still 100%. |
| **T10** `MainPage.xaml` state-board section | **⏭ Next up** | Adds `StageStatusGlyphConverter.cs`, rewrites `MainPage.xaml` with state-board grid + per-stream rows + event-log pane, click handler in code-behind for the "Stop streaming" button. See plan file (lines 1208–1366) for full text. |
| **T11–T26** | ⏸ Pending | T11–T15 are Phase 4 (viewer Android side: Timber, FileLoggingTree, InAppBufferTree, viewer PipelineEvent, viewer Diagnostics). T16+ wires the viewer UI. See plan file. |

Test totals at HEAD:
- **Core:** 338/338 passing, 100% line / 100% branch / 100% method.
- **Server:** 44/44 passing, 100% line / 100% branch / 100% method.
- **Server + CLI** both build cleanly (0 warnings, 0 errors).

## What changed vs. the original plan (deviations cumulative across all sessions)

Prior session deviations covered T1–T7 (see prior commits + plan-body notes). New deviations from THIS session (T8–T9):

15. **`MauiProgram.cs` wires `diagnostics.Subscribe(dashboard.ApplyEvent)` directly** (rather than a separate `Diagnostics.Extend` API or similar). The plan's prose note after T9 Step 1 mentioned this would land "in Step 3," but Step 3 was the test-writing step. The wiring was added inline in `MauiProgram.cs` (the same file T8 rewrites), keeping it adjacent to dashboard construction. Plan-prose-compliant per reviewer's read.

16. **Dispatcher-marshal helpers `MarshalEntryToMainThread` / `MarshalRaiseAllToMainThread` were extracted as private methods on `ServerDashboardViewModel`** and attributed `[ExcludeFromCodeCoverage]`. The plan body inlines `MainThread.BeginInvokeOnMainThread(...)` directly in `OnSinkEvent` / `ApplyEvent`; the extraction is a minimal structural change required so the `[ExcludeFromCodeCoverage]` attribute can apply to the dispatcher call site without smearing across the rest of the method. Synchronous catch-fallback (`COMException` → call action directly) is exercised by `On_Sink_Event_Appends_Entry_Synchronously_In_Headless_Host` and `Apply_Event_Fires_Property_Changed_Synchronously_In_Headless_Host`. The fallback is a test-host accommodation; the production path is unchanged.

17. **`catch (COMException)`, not `catch (InvalidOperationException)` or `catch (Exception)`.** The code-quality reviewer initially suggested narrowing the marshal helpers' catch to `InvalidOperationException`. The implementer tried it, tests failed with `System.Runtime.InteropServices.COMException` from `WinRT.ActivationFactory.Get` ("ClassFactory cannot supply requested class") — that is the actual headless throw. `COMException` is correct; both narrower and broader alternatives are wrong. Memorialized in `~/.claude/notes/reference_maui_headless_dispatcher_comexception.md`.

18. **Two no-op `Assert.True(true, "...")` tests were committed and immediately deleted.** Implementer's first commit (`31c66d0`) shipped two 70+ line methods that were stream-of-consciousness debugging narrative ending with `Assert.True(true, "RaiseAll coverage is exercised...")`. The code-quality reviewer caught them; the follow-up commit (`5414341`) deletes both. Coverage stayed 100% on deletion, proving the blocks contributed nothing. The actual working test for that concern is `Apply_Event_Fires_Property_Changed_Synchronously_In_Headless_Host`. Pattern memorialized in `~/.claude/notes/feedback_subagent_noop_assert_true_tests.md`.

19. **Reviewer's `MessageTemplateParser` → constructor substitution was REJECTED.** Reviewer suggested replacing `MessageTemplateParser.Parse(message)` in test helpers with `new MessageTemplate(message, Array.Empty<MessageTemplateToken>())`. The latter renders blank, which would break tests asserting on rendered message text — the implementer had flagged this in their DONE message. Orchestrator rejected the suggestion. Pattern memorialized in `~/.claude/notes/feedback_verify_reviewer_suggestions.md`.

20. **`MakeViewModel()` factory extracted in tests.** Six (then more — 17 sites total) inline constructions of `new ServerDashboardViewModel(new FakeSessionHostLauncher(), new InAppDashboardSink(capacity: 16))` collapsed into a tiny default-arg helper. Tests with custom launchers (`CancellingSessionHostLauncher`, `ThrowingSessionHostLauncher`) kept inline construction for clarity. Test file shrank 501 → 344 lines, comfortably under the 500-line soft cap.

## Operational notes for the next session

### Pre-dispatch checklist for T10

Per the plan: T10 modifies `src/WindowStreamServer/MainPage.xaml`, adds `src/WindowStreamServer/Converters/StageStatusGlyphConverter.cs`, and edits `src/WindowStreamServer/MainPage.xaml.cs` for a Stop-streaming click handler. Key context for the executor:

- `MainPage.xaml.cs` currently binds against `ServerDashboardViewModel` via the DI registration in `MauiProgram.cs`. T10 rewrites the XAML to bind against the new `State` derived properties + `RecentEvents` ObservableCollection.
- The plan body has the full XAML markup — a state-board section showing `Listening`/`ViewerConnected` stage statuses with glyphs, per-stream rows from `State.Streams`, and an event-log pane bound to `RecentEvents`.
- `StageStatusGlyphConverter` is a one-way `IValueConverter` that maps `StageStatus.{Pending,InProgress,Ok,Error}` to a unicode glyph (e.g. `○`/`◐`/`●`/`✕`). T10 plan body has the full converter source.
- Step 4 says "build + run smoke." On Windows MAUI this requires `dotnet run --project src/WindowStreamServer --framework net10.0-windows10.0.19041.0` which launches a real window. Coordinate with the user before requesting this — it's not a headless test.

### How to dispatch

Same pattern as T4–T9: `Agent` with `subagent_type: general-purpose` + `model: sonnet`. Paste the plan body for T10 inline (don't reference the file path — see [[feedback_subagent_silent_design_swaps]] and the subagent-driven-development skill's "Make subagent read plan file" red flag). Include ground-truth excerpts of the current `MainPage.xaml` and `ServerDashboardViewModel.cs` (post-rewrite) so the executor doesn't have to grep around.

Carry forward the same set of deviation flags that landed T8+T9 cleanly:
- Abbreviations rule (full words, e.g. `args` → `eventArguments`).
- `required` + `[SetsRequiredMembers]` discipline.
- No silent design swaps (verify production emitters/consumers before substituting any reducer or event handling).
- LSP stale diagnostics on freshly-created XAML files are noise — `dotnet build` is ground truth.
- Coverage filter scope: `<Include>[WindowStreamServer]*ViewModels*</Include>` excludes `Converters/`, so `StageStatusGlyphConverter` does NOT need test coverage to keep the Server gate at 100%. (If T10 lands tests for it anyway, great — but the gate doesn't require them.)
- Don't run coverage with `--filter`.

Add one new deviation flag specifically for T10:
- **MAUI XAML "run smoke" requires user coordination.** Don't `dotnet run` the MAUI project headlessly — it launches a window. Either ask the user to run it themselves and confirm visually, or skip Step 4 with an explicit note in the commit message.

### Recurring quality-review patterns (carry forward)

These have caught issues in T1, T4, T6, T7, T8, T9. Apply unconditionally to every server-side task:

- **Abbreviations.** Scan new identifiers against AGENTS.md's "full words" rule.
- **`required` on non-nullable refs** — needs `[SetsRequiredMembers]` on any constructor that satisfies it.
- **Subagent silent design swaps.** If a subagent reports "I used X instead because the plan's approach was fragile", VERIFY by grepping for production emitters/callers.
- **Reviewer suggestions can be wrong too.** Code-quality reviewers over-suggest narrowing and "simpler alternative" rewrites; verify before forwarding. New cross-cutting memory: [[feedback_verify_reviewer_suggestions]].
- **Spot-check for `Assert.True(true, "...")`.** If a subagent reports DONE and a test file grew by >100 lines, grep for `Assert\.True\(true` — any hit is a no-op test wart. New cross-cutting memory: [[feedback_subagent_noop_assert_true_tests]].
- **LSP stale diagnostics** on new files for several minutes. Build tool is ground truth.

### Things to skip

- **Don't use `--filter` when verifying coverage gates** (coverlet measures gate against the full assembly).
- **Don't try to `dotnet run` the MAUI server project from a subagent** — it launches a window and stalls the agent. See T10 deviation flag above.

## What didn't ship this session

Nothing was started past Task 9. The viewer side is still untouched (Phase 4+ starts at T11). The user's prior GXR picker MVP work (per WIP baseline `d56bd59` mentioned in earlier memory) remains in tree — separate concern from the observability plan.

## Cost-discipline reminder

User asked for Sonnet subagents for plan execution. Opus stays on orchestrator + design + plan-writing + review curation. Memory `~/.claude/notes/feedback_prefer_sonnet_subagents.md` captures this. Session pattern is solid: orchestrator (Opus) reads ground-truth code, writes a precise prompt with deviation-flags, dispatches Sonnet executor, then dispatches Sonnet reviewers (plan-compliance + code-quality, in that order), curates the fix list (rejecting overzealous reviewer suggestions where appropriate — see [[feedback_verify_reviewer_suggestions]]), bounces back to the implementer for fixes, verifies, commits. Catches drift early and keeps Opus tokens on judgment work.
