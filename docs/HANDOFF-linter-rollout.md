# Handoff — linter rollout (branch `linter-rollout`)

Session wrapped 2026-05-30. Worktree: `.worktrees/linter-rollout` (off `main` @ 17687d2).
Branch is **local-only (never pushed)**, 6 commits. The worktree is intentionally
**kept** — resume here.

## ✅ Done & committed — the SDK/Roslynator analyzer gate is GREEN

`dotnet build WindowStream.sln` (warn-as-error) exits **0 findings** across
`AnalysisLevel=latest-All` + `Roslynator.Analyzers 4.15.0` + `EnforceCodeStyleInBuild`,
and **100% line/branch/method coverage holds** (Core.Tests 338 + Server.Tests 44 pass).

Commits:
1. `ec4c65f` style: dotnet format whitespace sweep
2. `8c400ff` build: enable analyzers (latest-All) + documented opt-out policy
3. `1e2d2de` build: adopt canonical fleet C# conventions (.editorconfig + .DotSettings) + Roslynator
4. `d21f83b` docs: TEST-REPORT lint section
5. `6a0d65b` fix(lint): resolve all CA/RCS findings to zero (~230 fixes, ~15 opt-outs, ~14 pragmas)

How the bulk got done: 4 edit-only Sonnet agents (disjoint project scopes) fixed
~230 findings; orchestrator reconciled compile breaks (NalFragmenter/WorkerCommandHandler
→ static class + call-site updates; ~14 sync tests → `async Task` + `await using` for
IAsyncDisposable fakes), added editorconfig opt-outs + per-site pragmas, deleted dead code,
and **reverted agent-added RCS1194 exception ctors** that had broken coverage (unused members).

Repro the gate:
```
cd .worktrees/linter-rollout
$env:WINDOWSTREAM_SKIP_NVENC=1
dotnet build WindowStream.sln --no-restore --no-incremental          # 0/0
dotnet test tests/WindowStream.Core.Tests/... ; dotnet test tests/WindowStream.Server.Tests/...   # 100%
```

## ⛔ Remaining work (user priority: **aislop gate first**)

### 1. aislop gate (TOP priority — newest CLAUDE.md instruction)
- A **C# fork is installed**: `C:\Users\mtsch\AppData\Local\pnpm\aislop.CMD`
  (pinned `github:mtschoen/aislop#feat/csharp-support`). **Call the binary directly — NOT `npx aislop`** (upstream npm has no C# support; this bit us — it would false-green).
- Current score: **8 / 100** — 29 `ai-slop/swallowed-exception` (empty catch) errors,
  19 narrative-comment warns, 5 python `print()`, 5 function-too-long, 4 unused-import,
  3 file-too-large. `aislop fix` auto-clears ~26 (formatting/imports/dead code); rest by hand.
- **TENSION to resolve:** several of the 29 "swallowed exception" hits are the *intentional*
  best-effort `catch { }` boundaries we just justified per-site for CA1031/RCS1075 (worker
  teardown, native cleanup). Don't blindly un-swallow them — decide per-site (log vs annotate
  vs aislop-ignore). Some hits are in `tools/*.py` (python `print`, unused `annotations`).
- Then: create `.aislop/config.yml` (`ci.failBelow`, `exclude` obj/bin/*.g.cs, engines),
  wire `aislop ci .` into CI. Don't gate from a noisy baseline — clean first.

### 2. jb inspectcode deep gate — 898 findings
`~/.dotnet/tools/jb.exe inspectcode WindowStream.sln -o=jb.xml --severity=WARNING --no-build --no-updates` (SARIF JSON).
- 390 `RedundantUsingDirective` — **auto-fixable** (jb cleanupcode `CSOptimizeUsings`, or a
  naming-safe scoped cleanup profile). NOT enforced by `dotnet build` (compiler doesn't emit
  CS8019 here), so they don't fail the current gate — only jb would.
- **252 `InconsistentNaming`** — the `_camelCase` private-field rename. **BLOCKER:** per
  JetBrains docs, `jb cleanupcode` does NOT do naming renames ("not included… naming conflicts").
  Needs **Rider/VS interactively** (the machine's global ReSharper config is already aligned to
  the convention) or a Roslyn-based rename. Do NOT attempt blind text-rename (hits string literals).
- ~256 misc (RedundantNameQualifier 71, RedundantCast 26, IntVariableOverflow 20,
  AccessToDisposedClosure 20, EmptyGeneralCatchClause 9, …).

### 3. CI lint job — `.gitea/workflows/ci.yml` already EXISTS (don't overwrite — ADD steps)
Add to the existing Windows job (or a new `lint` job): `dotnet format WindowStream.sln
--verify-no-changes` + `dotnet build -warnaserror` (both green now) + later jb inspectcode + aislop ci.

### 4. PostToolUse on-save hook — `.claude/settings.json` already EXISTS (MERGE, don't overwrite)
It has `permissions` + `enabledPlugins`, no `hooks`. Add a `PostToolUse` Write|Edit hook running
`dotnet format ... --include <file>` on `*.cs`. (See LINTER-SETUP.md for the snippet.)

### 5. Open the PR (PAUSED per user instruction — get explicit go-ahead before pushing/opening)
Branch is local-only. PR target = `main`. C#-only; won't conflict with the unmerged xr Kotlin work.

## Gotchas learned this session
- `dotnet build` does NOT enforce IDE1006 naming or CS8019 redundant-usings — only Rider/jb do.
  So the build gate ≠ the jb gate. Plan the rename + redundant-using cleanup as the jb-gate phase.
- `dotnet format analyzers` can't FixAll most CA rules here (no solution-wide fixers) and the
  CA2007/CA1515/CA1028 fixers actively MISFIRE (ConfigureAwait on `await using` → CS0029;
  internal-conversion breaks test access; stripping `: byte` breaks the wire protocol).
- The 8 documented `.editorconfig` opt-outs + the protocol/coverage-conflict ones each carry an
  inline rationale — review them in the PR; they're the policy surface.
