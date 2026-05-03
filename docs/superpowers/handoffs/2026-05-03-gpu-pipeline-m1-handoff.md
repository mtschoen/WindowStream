# Handoff — Execute M1 of the GPU-resident frame pipeline refactor

**Date prepared:** 2026-05-03
**Prepared by:** prior session (running out of context budget after spec + plan)
**Your job:** Execute M1 of a five-milestone server-side refactor.

## TL;DR

1. Read `docs/superpowers/specs/2026-05-03-gpu-resident-frame-pipeline-design.md` for the *why* and the full five-milestone arc.
2. Read `docs/superpowers/plans/2026-05-03-gpu-pipeline-m1-shared-d3d11-device.md` for what you'll actually do today.
3. Execute that plan using **`superpowers:subagent-driven-development`** (the prior session and the user agreed on this mode).
4. After M1 lands and the user reviews, the next session writes the M2 plan and executes it. One milestone, one plan, one execution per session.

## What's already done

- Spec written, self-reviewed, user-approved, committed (`fccaf62`, refined in `760d34b`).
- M1 plan written, self-reviewed, committed (`85c463a`).
- A project memory entry documents this refactor as in-progress (`project_gpu_pipeline_refactor.md`).
- A feedback memory entry confirms you have pre-authorisation to clone FFmpeg / D3D11 / etc source into `~/src/` for reference reading (`feedback_pull_native_dep_source.md`).

Branch is `main`, local is ahead of `gitea/main` by 4 commits (the three docs commits plus the prior viewer fix at `331d44e`). Working tree is clean. Push to remotes is the user's call — ask before pushing if unsure.

## Why this is being done one milestone per session

User explicitly asked to wrap this session at the planning boundary because we crossed 200k tokens. The M2–M5 plans are deliberately *not* written yet — each milestone's plan is best written with fresh context after the prior milestone has landed (you may discover refactor opportunities in M1 that change M2's shape, etc.).

## Things you need to know that aren't in the spec or plan

These came up in the brainstorm conversation and shaped the design:

- **Hard cutover, not env-flag side-by-side.** User does not want a CPU-fallback env var on `main` after this lands. AMD support is a separate future effort (also GPU-resident, just AMF instead of NVENC). The plan honours this — there is no `WINDOWSTREAM_GPU_PIPELINE` flag.
- **Coverage gate is "painful but worth it for keeping LLMs in check."** User authorised relaxing thresholds (M2–M4) but never disabling. M5 mandatorily restores them. M1 itself doesn't relax anything because all of M1's new code is `#if WINDOWS`-guarded and the unit test project targets `net8.0` (so `#if WINDOWS` code is auto-excluded from coverage measurement). Verified in `tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj`.
- **"Functional at every checkpoint" was relaxed mid-brainstorm.** New criterion is "compile + proof of life" per milestone, bounded so at most one milestone (M3) may have a broken end-to-end demo and the next milestone (M4) must restore it. M1 *does* leave the demo functional (Task 7 in the plan verifies this), so this relaxation is informational for M3 prep.
- **The user values bisect-friendly history.** The plan's Task 4 deliberately leaves the build broken until Task 5 commits, then both commit together. Otherwise: one commit per task. Don't squash these.
- **Pushback safe word: `full send`.** If the user says it, accept their call without further pushback. (This is in their global CLAUDE.md too.)
- **Subagent worktree CWD drift is a known hazard.** If you dispatch parallel agents with `isolation: "worktree"`, verify each returned a `worktreePath` before trusting isolation. M1 is sequential per the plan so this shouldn't bite, but worth knowing. (See memory `feedback_subagent_worktree_cwd_drift.md`.)

## Recommended workflow

1. Start by reading both the spec and the plan in full. Don't skim. The plan was written assuming a fresh executor with zero conversational context.
2. Invoke `superpowers:subagent-driven-development` and feed it the plan path. Each task gets its own subagent; you review between tasks.
3. After all 9 tasks complete, summarise what landed, then ask the user whether to:
   - Push to `origin` and `gitea` remotes, OR
   - Hold for review, OR
   - Move straight on to writing the M2 plan in this same session if context allows.
4. If you do continue to M2 in the same session: brainstorming was already done at the parent-spec level, so M2's plan can be drafted directly via `superpowers:writing-plans` against the relevant spec section.

## Validation expectations

- M1's success signal is **the existing `WgcCaptureSourceSmokeTests.Attaches_To_Notepad_And_Receives_Frame` integration test passing unchanged** plus the four new `Direct3D11DeviceManagerTests` integration tests passing. That's literally proof of behaviour preservation.
- No manual hardware smoke is *required* at M1 (Task 8 step 3 marks the GXR check optional). The user has GXR + Quest 3 + Fold 3 available (see memory `project_xr_test_fleet.md`) but the spec defers required manual smoke to M4 and M5.
- If anything in the plan fails on first run, debug rather than work around. The plan's "expected output" lines were written with care; an unexpected error usually means a real issue, not a plan bug.

## After M1

The handoff for M2 will be a much shorter doc — by then we'll have the actual M1 commits, you can `git log` them for context, and the design has stabilised. M2 is "Add `CapturedFrame` discriminated representation" — a small, additive plumbing milestone per the spec. Should be a quick session.

## Closing

Good luck. The plan is detailed because the spec was thorough; trust both, and ask the user only when the plan is genuinely ambiguous (it shouldn't be — there's a self-review note at the bottom of the plan listing every deviation from the spec and why).
