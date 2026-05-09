# Test Coverage Status

**Current state (post-M4 / pre-M5):** The 100% line+branch coverage gate on `WindowStream.Core` is temporarily relaxed to **90% line / 85% branch** during the GPU-resident frame pipeline refactor (M2 through M4). Actual coverage on `main` is **94.1% line / 89.55% branch**, comfortably above the relaxed gate.

**M5 will restore the 100/100 gate.** See `docs/superpowers/specs/2026-05-03-gpu-resident-frame-pipeline-design.md` for the full refactor arc.

## Pre-existing coverage gaps

The original 100/100 gate was already failing before the GPU-resident refactor began — the v1-era demo work (commits at or before `7079049`) added production code without matching tests in: session adapters, `ViewerReadyMessage`, `IControlChannel.RemoteIpAddress`, `SessionHost` VIEWER_READY handling, `SessionHostLauncherAdapter`, and the v2 multi-window code paths. M5 will either backfill these or scope them via attribute exclusions with documented rationale.

The previous version of this document (in git history) had a detailed line-by-line punch list of the v1 gaps — useful as a starting point for the M5 cleanup pass.
