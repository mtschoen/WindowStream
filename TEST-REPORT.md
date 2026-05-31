WindowStream test report — 2026-05-17
═══════════════════════════════════════════

Status:   PASS (coverage) · IN PROGRESS (lint gate — see "Lint rollout" below)
Mode:     close-the-gap (establishing the superset lint gate is the active task)
Tests:    663 passed, 3 skipped (total 666)
Git:      ead4fc8 (main, post T12 PipelineEvent landing)
          lint rollout on branch linter-rollout (worktree off main @ 17687d2)

.NET (Coverlet — line + branch + method, 100% gate)
  WindowStream.Core      — 100% line, 100% branch, 100% method
  WindowStreamServer     — 100% line, 100% branch, 100% method
  windowstream (CLI)     — 100% line, 100% branch, 100% method
  20 `[ExcludeFromCodeCoverage]` annotations across 11 production files
  (native I/O wrappers — D3D11/COM, FFmpeg, raw sockets — per AGENTS.md
  rationale.)

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
  WindowStream.Integration.Tests    38 passed     3 skipped
  viewer :app:testPortableDebugUnitTest          243 passed     0 skipped
                                   ─────         ─────
                                   663 total       3 skipped

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
Remaining: _camelCase rename (jb cleanupcode) · jb inspectcode → 0 · CI lint job
  · PostToolUse hook · aislop (config + hook + gate, DISABLED until aislop ships a
  C# engine) · open PR
