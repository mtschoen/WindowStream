# WindowStream test report — 2026-06-23T08:38:00Z

Status:   PASS
Mode:     maintain
Tests:    790 total (385 Core, 38 Server, 43 Integration, 324 Viewer)
Git:      f769021 (Dead code cleanup + Touch-to-mouse forwarding + Input connection soft keyboard UX + visual polish)
Coverage: 100% statements (100% line/branch/method for .NET Core/Server/CLI, 100% line/branch for Kotlin Viewer app)
          0 lines uncovered
          20 C# / 50 Kotlin exclusion annotations
Lint:     aislop: 6 findings (0 errors, 6 warnings) - score 92/100
          0 per-case suppressions
          0 documented exceptions

## Summary of Changes
- **Part 1 — Dead Code Removal**: Cleaned up legacy/unused ViewModels, Activities, Screens, and tests across both .NET and Android codebases.
- **Part 2A — Touch-to-Mouse Pointer Forwarding**: Completed end-to-end touch event transmission from Android (`MotionEvent`) to the Windows server to inject Win32 mouse input, including unit/serialization coverage.
- **Part 2B — Soft Keyboard UX Fix**: Created custom `InputProxyView` with an `InputConnection` interface to address composition drift and empty-buffer backspace issues.
- **Part 2C — Tab Bar & Drawer Visual Polish**: Converted all layout pixel dimensions to DP (40dp tab bar, 240dp drawer), added ripple drawable foregrounds for tactile feedback, introduced tab selection background color animation, and implemented a connection status chip in the tab bar.

## Test Results Detail

### .NET (Coverlet — line + branch + method, 100% gate)
- **WindowStream.Core** — 100% line, 100% branch, 100% method
- **WindowStreamServer** — 100% line, 100% branch, 100% method
- **windowstream (CLI)** — 100% line, 100% branch, 100% method

### Viewer (Kover with JaCoCo backend — line + branch, 100% gate)
- **app (portable + gxr flavors)** — 100% line, 100% branch coverage (excluding platform-binding/Compose/XR-runtime classes).

### Per-suite test counts
- `WindowStream.Core.Tests`: 385 passed, 0 skipped
- `WindowStream.Server.Tests`: 38 passed, 0 skipped
- `WindowStream.Integration.Tests`: 43 passed, 3 skipped
- `viewer :app:testPortableDebugUnitTest`: 324 passed, 0 skipped

---

## Historical: WindowStream test report — 2026-06-03

Status:   PASS (coverage + Roslyn/Roslynator gate) + jb inspectcode deep gate
          896 -> 0 (all findings cleared; the 5 previously-documented MAUI/CCW
          "exceptions" were re-verified this pass and removed - they did not
          actually affect coverage)
Mode:     close-the-gap (jb inspectcode deep gate - reached zero)
Tests:    Core.Tests 338 + Server.Tests 44 pass at 100% coverage
Git:      main @ a776975 (jb-inspectcode-cleanup PR #13 merged) + this commit:
          fix WorkerEmitsChunksThroughPipe Edge-capture frame starvation. Roslyn
          analyzer gate landed on main via PR #12 (db396b2); jb deep gate via #13.

Integration fix (this commit): WorkerEmitsChunksThroughPipe was failing
  deterministically (worker pipe read timed out at 30s; encoder opened but emitted
  zero chunks). Root cause: commit 06595c7 swapped the capture target from
  notepad+frame-nudger to an Edge --app latency clock relying on requestAnimationFrame.
  When the Edge window is occluded by the test runner's console, Chromium painter
  throttling suspends rAF until the window is shown, so WGC (delivers only on content
  change) is starved and the worker's capture loop blocks. Fix: launch Edge with the
  chrome-launcher/Puppeteer anti-throttle flags (--disable-background-timer-throttling,
  --disable-backgrounding-occluded-windows, --disable-renderer-backgrounding,
  --disable-features=CalculateNativeWinOcclusion). Now passes 3/3 in ~7s (was 36s
  timeout); full integration suite 41 passed / 3 skipped.

.NET (Coverlet — line + branch + method, 100% gate)
  WindowStream.Core      — 100% line, 100% branch, 100% method
  WindowStreamServer     — 100% line, 100% branch, 100% method
  windowstream (CLI)     — 100% line, 100% branch, 100% method
  20 `[ExcludeFromCodeCoverage]` annotations across 11 production files
  (native I/O wrappers — D3D11/COM, FFmpeg, raw sockets — per AGENTS.md
  rationale.)
  Server.Tests coverage `<Include>` anchored to `WindowStream.Server.ViewModels.*`
  (was a loose `*ViewModels*` glob): the CsWinRT AOT CCW-vtable class
  `WinRT.WindowStreamServerVtableClasses.…SessionViewModelWinRTTypeDetails`
  matched the glob by name and pulled 10 lines of uncovered generated marshalling
  glue into the denominator (90.29%). It lacks `[GeneratedCode]`, so the
  attribute-based exclusion missed it. Pre-existing at HEAD 1bee5b2, not a
  regression from the naming work; fixed here so the 100% gate is honest.

Viewer (Kover with JaCoCo backend — line + branch, 100% gate)
  app (portable + gxr flavors) — 100% line (661/661), 100% branch (225/225)
  73 / 4508 instructions still missed — synthetic Kotlin bytecode that
  JaCoCo cannot drive (cooperative-cancellation while-false branches,
  serialization plugin scaffolding); not gated.
  ~50 inline class exclusions in
  `viewer/WindowStreamViewer/app/build.gradle.kts` (lifecycle/Compose/
  XR-runtime classes), each with a rationale comment.

Per-suite test counts
  WindowStream.Core.Tests          338 passed     0 skipped
  WindowStream.Server.Tests         44 passed     0 skipped
  WindowStream.Integration.Tests    41 passed     3 skipped
  viewer :app:testPortableDebugUnitTest          243 passed     0 skipped
                                   ─────         ─────
                                   666 total       3 skipped

Coverage commands
  dotnet test                                            # .NET (Coverlet)
  cd viewer/WindowStreamViewer && ./gradlew \
      :app:koverVerifyPortableDebug                      # viewer (Kover)

  Optional reporting:
  ./gradlew :app:koverXmlReportPortableDebug             # XML report
  ./gradlew :app:koverHtmlReportPortableDebug            # HTML report

═══════════════════════════════════════════
Lint rollout (branch linter-rollout) — target: 0 findings, superset of all linters
═══════════════════════════════════════════

Configured (Directory.Build.props + .editorconfig + WindowStream.sln.DotSettings):

- SDK Roslyn analyzers: EnableNETAnalyzers, AnalysisLevel=latest-All
- Roslynator.Analyzers 4.15.0 (RCS rules)
- EnforceCodeStyleInBuild + TreatWarningsAsErrors → findings are build errors
- Canonical fleet naming/style .editorconfig (IDE1006 _camelCase, etc.)
- jb inspectcode — planned CI deep/naming gate (not yet wired)

Opt-outs (.editorconfig, each with rationale):
  CA2007, CA1062, CA1515, CA1028, CA1008, CA1032 ; tests-only: CA1707, CA1861

Lint:     0 findings — `dotnet build WindowStream.sln` (warn-as-error) is GREEN
          across SDK latest-All + Roslynator 4.15.0 + EnforceCodeStyleInBuild.
          ~230 findings fixed in code; ~15 documented rule opt-outs (.editorconfig,
          each with rationale); ~14 per-site #pragma suppressions (FPs / framework
          / interop, each with rationale); 1 project NoWarn (CA5392 — vendor-
          generated WindowsAppSDK file in WindowStreamServer).
          NOTE: IDE1006 _camelCase is NOT enforced by `dotnet build` (Rider/jb
          only) → naming rename is a separate jb cleanupcode step (below).
Coverage: 100% line / 100% branch / 100% method held after all fixes
          (Core.Tests 338 pass, Server.Tests 44 pass).

Done (committed): format sweep · enable analyzers+policy · adopt fleet conventions
  +Roslynator · fix all CA/RCS findings → 0 (coverage held at 100%)

## jb inspectcode deep gate (branch chore/jb-inspectcode-cleanup, 2026-06-03)

Burn-down: 896 -> 0 findings. Build -warnaserror stays 0/0; Core 338 + Server 44
pass at 100% line/branch/method.

- Phase 1 (commit 093582c): scoped ReSharper cleanupcode (RedundanciesOnly
  profile) removed 474 - RedundantUsingDirective (389), RedundantNameQualifier
  (74), ArrangeThisQualifier (8) and assorted. One over-removal in CliServices.cs
  (the no-build cleanup analyzed the non-WINDOWS TFM) fixed by scoping the usings
  under #if WINDOWS.
- Phase 2 (commit 79a68c0): CsWinRT1028 (3 ViewModels to partial) + CS9191
  (10 ref to in at D3D11 COM sites).
- Phase 3 (hand-fix pass): cleared 61 (412 -> 351) - the "mechanical but
  cleanup-tool-unsafe" and "small correctness" categories, hand-fixed per site so
  the (nint)0 overload-resolution trap and named-argument stripping were avoided.
  Covered RedundantCast 26 (incl. the (nint)0 Assert.Equal asserts - generic
  inference still binds nint, build 0/0 confirms), RedundantSuppressNullable 12,
  RedundantArgumentDefaultValue 4, RedundantExplicitArrayCreation 4,
  RedundantAssignment 4 + AssignmentInsteadOfDiscard 2, EmptyConstructor 2,
  RedundantToStringCall 1, ConditionIsAlwaysTrueOrFalse 1, NullCoalescing 1,
  InvalidXmlDocComment 2 (`<paramref>` in a class-level summary changed to `<c>`).
- Phase 4 (non-naming burn-down): 351 -> 249, fixing in code where possible and
  suppressing per-site only where feedback_inspections_refactor_over_suppress
  sanctions it. Fixed: IntVariableOverflow 20 (dropped the redundant (uint) on
  int.ToString("X8") - output byte-identical), EmptyGeneralCatchClause 9 (intent
  comments; CA1031 pragmas stay the real guard), AccessToModifiedClosure 6
  (`StrongBox<T>` rewrite), UnusedMember.Local 3, UnusedParameter.Local,
  ConditionalAccessQualifierIsNonNullable 1. Suppressed per-case with inline
  rationale: AccessToDisposedClosure 21 (disposables shared across cooperative
  Task.Run loops, drained before disposal), FunctionNeverReturns 1,
  NotAccessedField.Local 2, UnusedAutoPropertyAccessor.Local 1, UnusedVariable 1.
  editorconfig opt-out (generated-code blind spot, .Global only):
  NotAccessedPositionalProperty.Global 12 + UnusedAutoPropertyAccessor.Global 11.
  naming-prep: Win32 interop names 7 + d3d11* locals 2 marked disable
  InconsistentNaming.
- Phase 5 (Rider semantic naming rename + delete-unused): 249 -> 5. The 244
  naming findings (InconsistentNaming 238 + ParameterHidesMember 6) cleared by
  Rider's "Fix inconsistent naming in solution" - private fields to `_camelCase`,
  members/static-readonly to PascalCase per the committed FDG `.editorconfig`.
  Wire-safe (System.Text.Json CamelCase policy keeps PascalCase members
  serializing lowercase). One #if WINDOWS rename-miss hand-fixed (WgcCapture's
  Handle/Options implementations).
- Phase 6 (eliminate the last 5 "exceptions", this pass): 5 -> 0. The four
  RedundantExtendsListEntry base types (App/AppShell/MainPage/Windows-App
  `.xaml.cs`) and the one Xaml.RedundantNamespaceAlias (`Platforms/Windows/App.xaml`
  `xmlns:local`) were re-verified and removed. Build stays 0/0 and
  WindowStreamServer holds 100% line/branch/method with them gone. The earlier
  belief that they protected coverage was stale: the real cause (the CsWinRT
  CCW-vtable class lacking `[GeneratedCode]`, pulled into the denominator by the
  loose `*ViewModels*` glob) had already been fixed by anchoring the coverage scope
  (see the coverage note above), so the base types were genuinely redundant. No
  suppressions, no exceptions.
