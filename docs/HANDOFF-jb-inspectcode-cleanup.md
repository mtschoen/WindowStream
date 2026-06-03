# Handoff - jb inspectcode deep-gate burn-down

Updated 2026-06-03 (non-naming burn-down pass). Branch **`chore/jb-inspectcode-cleanup`** (off `main`
@ `283c4de`), **pushed to `gitea/chore/jb-inspectcode-cleanup`** (HEAD `07c9ae7`). Resume here.

## STATUS: COMPLETE - jb inspectcode 896 -> 0 (zero findings; no exceptions)

The Rider naming rename + delete-unused pass is DONE, and the 5 previously-"documented exceptions"
were re-verified and removed (Phase 6, 2026-06-03). Removing the four RedundantExtendsListEntry base
types (App/AppShell/MainPage/Windows-App `.xaml.cs`) and the one Xaml.RedundantNamespaceAlias
(`Platforms/Windows/App.xaml` `xmlns:local`) keeps build -warnaserror 0/0 and WindowStreamServer at
100% line/branch/method. The belief that they protected coverage was stale: the real cause (a CsWinRT
CCW-vtable class lacking `[GeneratedCode]`, pulled into the denominator by a loose `*ViewModels*`
coverage glob) had already been fixed by anchoring the coverage scope, so the base types were
genuinely redundant. Build -warnaserror 0/0; Core 338 + Server 44 at 100%. The section below is
retained as the record of HOW the burn-down was performed (and the #if WINDOWS gotcha to watch for
next time).

### How the naming step was done (Rider)

jb CLI `cleanupcode` REFUSES naming renames (batch-conflict risk - confirmed via JetBrains docs),
so this step was run in Rider:

1. Open `WindowStream.sln` in Rider.
2. `alt+Enter` on any `InconsistentNaming` warning -> **"Fix inconsistent naming in solution"**
   (or Code -> Inspect Code -> apply the naming quick-fix in solution scope). It renames
   semantically: private fields -> `_camelCase`, members/static-readonly/const -> `PascalCase`,
   per the committed FDG `.editorconfig`. It updates all call sites and the 6 `ParameterHidesMember`
   auto-clear once the `options` field becomes `_options`.
3. The Win32 interop names (`Win32InputInjector`), the `d3d11*` locals (`FFmpegNvencEncoder`), and
   the 5 MAUI/CCW documented exceptions are already `// ReSharper disable`-marked -> the batch fix
   skips them. Do NOT remove those markers.
4. Wire-safe: System.Text.Json uses `JsonNamingPolicy.CamelCase`, so PascalCase members still
   serialize lowercase - the rename does not change the wire protocol.
5. Verify (must all stay green), then commit + push:

   ```bash
   export WINDOWSTREAM_SKIP_NVENC=1
   dotnet build WindowStream.sln -warnaserror --no-incremental    # 0/0
   dotnet test tests/WindowStream.Core.Tests/...   --no-build      # 338, 100%
   dotnet test tests/WindowStream.Server.Tests/... --no-build      # 44, 100%
   ~/.dotnet/tools/jb inspectcode WindowStream.sln --output=.claude/inspect.sarif --format=Sarif --severity=WARNING
   # expect 0 findings
   ```

6. Open the PR (target `main`) as `claude-code` once naming is in and the re-scan is clean.

This is the "jb inspectcode deep gate" follow-up that PR #12 (`db396b2`) explicitly deferred. The
Roslyn/Roslynator analyzer gate (`dotnet build -warnaserror` = 0/0) already landed on `main` via #12;
`dotnet build` does NOT enforce the jb inspectcode rules, so this is a separate gate.

## Status: 896 -> 351 findings (545 cleared, 61%); 0 error-severity remain

**Phase 3 (hand-fix pass, done):** cleared 61 (412 -> 351) — the mechanical-but-cleanup-unsafe (55 of
60) and small-correctness (4 of 6) categories, hand-fixed per site. Build -warnaserror 0/0; Core 338 +
Server 44 at 100% line/branch/method. Snapshot: `.claude/inspect-after-handfix.sarif`. Full per-rule
breakdown + the 7 documented exceptions now live in `TEST-REPORT.md` ("jb inspectcode deep gate" section).
Key learnings this pass:

- The `(nint)0` Assert.Equal cast the prior handoff feared was a FALSE alarm — generic inference still
  binds `T=nint`, build 0/0 confirms. ReSharper's RedundantCast IS overload-aware here.
- RedundantExtendsListEntry on the 4 MAUI `.xaml.cs` partials (+ the App.xaml xmlns:local alias) is NOT
  safe to apply: removing the base type regenerates the CsWinRT CCW vtable for WindowPickerViewModel and
  drops WindowStreamServer coverage to 90.29%. Reverted; kept as documented exceptions.
- The `(_, args)` lambda + `_ = FooAsync()` fire-and-forget: the sender `_` shadows the discard. Fix =
  rename sender to `sender` (keeps CS4014 suppression; ReSharper does not flag event-lambda sender as
  unused; bonus: cleared 2 AllUnderscoreLocalParameterName).

## Status (historical): 896 -> 412 findings (484 cleared, 54%); all 13 error-severity items resolved

Every step verified green: `dotnet build -warnaserror` 0/0 across all TFMs, Core.Tests 338 +
Server.Tests 44 at 100% line/branch/method coverage.

| Commit | Cleared | What |
| --- | --- | --- |
| `093582c` | 474 | Scoped ReSharper cleanup: RedundantUsingDirective (389), RedundantNameQualifier (74), ArrangeThisQualifier (8), assorted |
| `79a68c0` | 13 | CsWinRT1028 (3 ViewModels -> `partial`); CS9191 (10 `ref` -> `in` at D3D11 COM sites) |
| `698024b` | - | TEST-REPORT.md burn-down record |

## How to reproduce / continue the cleanup

The validated scoped cleanup profile lives at `.claude/cleanup-redundancies.DotSettings` (gitignored).
It does optimize-usings + shorten-references + arrange-qualifiers ONLY (no reformat, no naming, no
member reorder). Command:

```bash
# from repo root, WINDOWS TFM env set so capture/encode code analyses correctly
export WINDOWSTREAM_SKIP_NVENC=1
~/.dotnet/tools/jb cleanupcode WindowStream.sln \
    --profile=RedundanciesOnly --settings=.claude/cleanup-redundancies.DotSettings
# then ALWAYS rebuild -warnaserror and run the two coverage gates (see Verify below)
```

Re-scan to measure remaining findings (SARIF; current snapshot at `.claude/inspect-final.sarif`):

```bash
~/.dotnet/tools/jb inspectcode WindowStream.sln \
    --output=.claude/inspect.sarif --format=Sarif --severity=WARNING
jq -r '[.runs[0].results[].ruleId]|group_by(.)|map({r:.[0],n:length})|sort_by(-.n)|.[]|"\(.n)\t\(.r)"' \
    .claude/inspect.sarif
```

### Gotchas learned this session (do not re-discover)

- **`--no-build` cleanupcode over-removes usings in multi-target `#if` files.** It analyses only one
  preprocessor branch (the non-WINDOWS TFM), so usings used only under `#if WINDOWS` look redundant and
  get stripped. Hit this in `CliServices.cs` (Microsoft.Extensions.Logging, WindowStream.Core.Observability).
  Fix pattern: move the conditionally-needed usings INSIDE the `#if WINDOWS` block - satisfies both the
  compiler (present under WINDOWS) and the inspection (absent under non-WINDOWS). Always rebuild
  `-warnaserror` after any cleanup pass; the build is the only reliable catch.
- **Do NOT use "Built-in: Full Cleanup".** It STRIPS named-argument labels (`widthPixels: 3` -> `3`,
  hurting test readability and not even flagged), and removes `(nint)0` casts in ways that can change
  `Assert.Equal` overload resolution. Tested and reverted. Use the scoped `RedundanciesOnly` profile
  instead. (NOTE: an earlier version of this note also warned that Full Cleanup converts explicit types
  to `var` "against codebase style" - that was wrong. `var` IS the codebase style: `.editorconfig`
  sets `csharp_style_var_* = true` and `dotnet_style_require_accessibility_modifiers = omit_if_default`,
  so converting explicit types to `var` and dropping redundant modifiers moves code TOWARD the config.)
- **The scoped profile's `CSRemoveCodeRedundancies` element silently no-op'd** (wrong key; the earlier
  `LoggerException` is the tell), which is why RedundantCast (26) and friends survived Phase 1. Getting
  those needs either the correct cleanup key or careful hand-fixes (see below).
- **LSP diagnostics lie on this solution.** Mid-edit the in-IDE LSP reports `CS0234 'Silk could not be
  found'` / `CS0103 'MainThread'` on Silk.NET and MAUI types because it hasn't loaded the package refs /
  MAUI global usings. The `dotnet build` is authoritative; ignore LSP CS0234/CS0103 on those.

## Outcome: all remaining findings resolved (896 -> 0)

Every category from the original burn-down was resolved, none deferred:

- Naming (256: InconsistentNaming, ParameterHidesMember, etc.) - cleared by Rider's semantic
  "Fix inconsistent naming in solution" (Phase 5). Protocol-safe: System.Text.Json's CamelCase
  policy keeps PascalCase members serializing lowercase on the wire.
- False positives on correct code (56: IntVariableOverflow, AccessToDisposed/ModifiedClosure,
  EmptyGeneralCatchClause) - fixed in code where a real change applied (dropped redundant `(uint)`
  HRESULT casts, `StrongBox<T>` closure rewrite, intent comments in best-effort catch bodies);
  per-site `// ReSharper disable` with rationale only for the genuinely-shared `Task.Run`
  disposables (Phase 4).
- Mechanical but cleanup-unsafe (60: RedundantCast and friends) - hand-fixed per site, avoiding
  the `(nint)0` `Assert.Equal<T>` overload-resolution trap and named-argument stripping (Phase 3).
- Unused members (34) - dead code deleted; DTO/record members consumed only via channels ReSharper
  can't trace excluded via `resharper_*_global_highlighting = none` (.Global only; .Local stays on).
- Small correctness signals (6) and the final 5 "documented exceptions" - investigated and resolved
  (Phase 6 removed the 4 RedundantExtendsListEntry base types + 1 Xaml.RedundantNamespaceAlias; see
  STATUS above). InvalidXmlDocComment was a real fix (`<paramref>` to `<c>` in a class summary).

The per-phase, per-rule record lives in `TEST-REPORT.md` ("jb inspectcode deep gate" section).

## Verify (run after ANY change; all must stay green)

```bash
export WINDOWSTREAM_SKIP_NVENC=1
dotnet build WindowStream.sln -warnaserror --no-incremental          # must be 0/0
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --no-build      # 338, 100%
dotnet test tests/WindowStream.Server.Tests/WindowStream.Server.Tests.csproj --no-build  # 44, 100%
```

## Integration

Branch is local-only; PR target = `main`, C#-only. Open as `claude-code` (token `~/.gitea-token-claude`)
so the user can approve. Per #12 pattern: branch -> PR -> user review. Other deferred lint tracks (not
this branch): aislop config+gate (8/100 baseline), CI lint job, PostToolUse on-save hook.

## Scratch artifacts (gitignored, kept for resume)

- `.claude/cleanup-redundancies.DotSettings` - the validated scoped cleanup profile.
- `.claude/inspect-final.sarif` - current 412-finding snapshot (the source for the jq recipes above).
- `.claude/inspect-results.sarif` (original 896) and `.claude/inspect-after-phase1.sarif` (422) - delta refs.
