WindowStream test report — 2026-05-17
═══════════════════════════════════════════

Status:   PASS
Tests:    663 passed, 3 skipped (total 666)
Git:      ead4fc8 (main, post T12 PipelineEvent landing)

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
