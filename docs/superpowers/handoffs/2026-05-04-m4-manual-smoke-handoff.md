# M4 Manual Smoke Handoff — GPU-Resident NVENC Pipeline

**Status:** M4 implementation complete on `feature/m4-hwaccel-ingestion`, branch is **NOT merged to main**. Merge depends on this manual smoke check passing per the spec's regression rule.

## What's done

End-to-end GPU-resident pipeline:
- WGC captures BGRA D3D11 textures
- `D3D11VideoProcessorColorConverter` runs `VideoProcessorBlt` → NV12 D3D11 textures (M3, GPU-resident)
- NV12 textures come from FFmpeg's `hw_frames_ctx` pool managed by `FFmpegNvencEncoder` (M4)
- NVENC encodes the NV12 texture directly via D3D11VA hwaccel — no `sws_scale`, no CPU staging readback
- `Direct3D11DeviceManager` is shared per-worker between encoder and capture (hoisted from per-capture in M4)

**Automated test results on the worktree:**
- 296 unit tests pass; coverage gate green at 94.1%/89.55% line/branch (gate is 90/85 during M2-M4 transition window)
- 38 integration tests pass / 3 expected skips
- `WorkerProcessIntegrationTests.WorkerEmitsChunksThroughPipe` is GREEN (was broken-by-design at M3; M4's success signal)
- `FFmpegNvencEncoderHwaccelTests` (4 resolutions: 640×360, 800×450, 960×540, 1120×630) all pass

## What you need to verify (manual smoke — agent cannot do this part)

The spec's M4 section says:
> Manual smoke checkpoint — latency win should appear here. Capture [FRAMECOUNT] data and record in this design doc. This is also the first point at which we re-validate that the M1 → M3 work didn't regress anything visible in the demo.
>
> Regression rule: if this milestone shows latency *worse* than the pre-M1 baseline, stop and diagnose before proceeding to M5.

**The pre-M1 baseline:** ~100 ms median end-to-end (Unity 4K → GXR), per memory `project_gpu_pipeline_refactor.md`.

**M4 target:** measurably lower (spec says 5–15 ms median reduction at 4K, plus larger tail reduction).

**If M4 shows worse latency than pre-M1:** STOP. Don't merge. Don't proceed to M5. Diagnose and fix.

## How to run the smoke

### From this worktree (recommended — keeps main untouched until verdict)

```powershell
# In the worktree, NOT main
cd "C:\Users\mtsch\WindowStream\.worktrees\m4-hwaccel"

# Build the CLI on the Windows TFM
dotnet build src/WindowStream.Cli/WindowStream.Cli.csproj -f net8.0-windows10.0.19041.0

# Confirm Unity is running (any 4K-able window — the prior baseline was Unity 4K).
# Launch the coordinator. It listens on a dynamic TCP port and advertises mDNS.
dotnet run --project src/WindowStream.Cli -f net8.0-windows10.0.19041.0 -- serve 2>&1 | tee m4-smoke-server.log
```

Note the TCP port from the banner (`windowstream: serving on TCP <port>, UDP <port>`).

### Connect a viewer

**Galaxy XR (the latency baseline target):**
- HMD must be on-head (radio parks off-head per memory `project_xr_test_fleet.md`)
- ADB-launch DemoActivity bypasses the launcher icon (which is broken on alpha04 per memory `project_gxr_jetpack_xr_alpha04_broken`):

```bash
adb -s adb-R3GYB04E2WB-EFU6vk._adb-tls-connect._tcp shell am start -W \
    -n com.mtschoen.windowstream.viewer/.demo.DemoActivity \
    --es streamHost 192.168.50.76 --ei streamPort <tcp-port-from-banner>
```

(Replace 192.168.50.76 with current `ipconfig` LAN IP if the network has reassigned.)

**Quest 3 or Fold (portable flavor):**
```bash
adb shell am start -n com.mtschoen.windowstream.viewer/.demo.DemoActivity \
    --es streamHost <pc-lan-ip> --ei streamPort <tcp-port-from-banner>
```

### Capture FRAMECOUNT data

Server stderr emits `[FRAMECOUNT] stage=convert ptsUs=N wallMs=M` (M3 site, in `WgcFrameConverter`) and `[FRAMECOUNT] stage=enc ptsUs=N wallMs=M` (encoder site). Viewer logcat emits `stage=reasm` and `stage=dec` per memory `project_xr_test_fleet`'s notes.

```bash
# Server side (already piped via tee above to m4-smoke-server.log)
# Viewer side (Galaxy XR):
adb -s adb-R3GYB04E2WB-EFU6vk._adb-tls-connect._tcp logcat -d --pid=$(adb shell pidof com.mtschoen.windowstream.viewer) -s WindowStreamDemo:V FRAMECOUNT:V *:E > m4-smoke-viewer.log
```

Pair stage=convert (server) with stage=dec (viewer) by ptsUs to get end-to-end latency. Compare to the pre-M1 ~100 ms median baseline.

### What to look for

- **Demo runs end-to-end** — no crashes, no encoder exceptions, frame rate stable
- **Latency at 4K** — median should be measurably lower than pre-M1 baseline; if NOT, the regression rule kicks in
- **No new GPU memory leaks** — Task Manager → Performance → GPU memory should be stable across a multi-minute session, not climbing
- **CPU usage** — should be lower than pre-M1 (sws_scale was CPU-bound; we removed it)

## If the smoke passes

Merge to main:

```bash
cd "C:/Users/mtsch/WindowStream"
git checkout main
git merge feature/m4-hwaccel-ingestion --no-ff -m "Merge branch 'feature/m4-hwaccel-ingestion' (M4: NVENC hwaccel ingestion)"
git push origin main
git push gitea main

# Then clean up:
git worktree remove .worktrees/m4-hwaccel
git branch -d feature/m4-hwaccel-ingestion
```

Then update memory `project_gpu_pipeline_refactor.md` with the measured before/after [FRAMECOUNT] medians, mark M4 complete, and the GPU-resident pipeline is ready for M5 cleanup.

## If the smoke fails (latency worse than pre-M1)

Don't merge. Open an issue or note in the spec what the regression is. Common suspects:
- The `pendingPoolFramePointers` queue has bounded size implicitly by the pool's `initial_pool_size = 4`. If the converter outpaces the encoder, `av_hwframe_get_buffer` may block waiting for a frame to be released. Could reduce throughput at high frame rates.
- The `BindFlags = RenderTarget | ShaderResource` setting on the NV12 pool textures — required for `VideoProcessorBlt` to write to them, but might affect NVENC's choice of input surface format internally.
- The shared `Direct3D11DeviceManager`: if both the WGC framepool and the NVENC encoder serialize on the immediate context, contention could cause stalls. (M3 had per-capture device, no contention; M4 hoists to shared.)

Investigate with PerfView or NVIDIA Nsight Systems before reverting; the regression might be small + fixable.

## Why this handoff exists

The agent that landed M4 is autonomous overnight per "full send" intent. The spec mandates a manual smoke check at M4 (latency comparison vs hardware baseline). The agent has no viewer hardware connected and cannot honor the regression rule. So M4 lives on a feature branch, fully tested at the integration-test level, awaiting your manual verdict.

## Branch state at handoff

- 8 commits on `feature/m4-hwaccel-ingestion` (read with `git log --oneline 5f43ee4..feature/m4-hwaccel-ingestion`):
  - `0710a0e feat(encode): add IFrameTexturePool interface (M4)`
  - `639cb2f feat(encode): NVENC hwaccel ingestion via D3D11VA hw_frames_ctx (M4)`
  - `afa516a feat(capture): WgcCaptureSource accepts external device + texture pool (M4)`
  - `749c4cd feat(cli): WorkerCommandHandler shares D3D11 device across encoder + capture (M4)`
  - `5c77032 test(encode): multi-resolution hwaccel encode/decode round trip (M4 proof of life)`
  - `b64874d test(encode): rewrite NVENC smoke for texture path (M4)`
  - `ab1685e test(encode): move FFmpegNvencEncoderConstructionTests to integration project (M4)`
- Plus this handoff doc (will be the 9th commit on the branch)
- Worktree path: `C:\Users\mtsch\WindowStream\.worktrees\m4-hwaccel`

---

## After the smoke passes — M5 prep

M5 is the cleanup milestone (spec section "M5 — Cleanup and coverage restoration"). It cannot start until M4 manual smoke passes. When you're ready:

### What M5 needs to do (per spec)

1. **Scope `CapturedFrame.FromBytes` (and the public bytes constructor) to test-only visibility.** Mark `internal`; rely on `[InternalsVisibleTo]` so test fakes (`FakeWindowCapture`, `SolidColorFrameFactory`) keep working. Production code never constructs bytes-bearing frames after M4.
2. **Remove discriminated-union dispatch from production code.** Anywhere that branches on `frame.representation` collapses to the texture-only case. Concretely:
   - `FFmpegNvencEncoder.EncodeOnThread`'s `if (frame.representation != FrameRepresentation.Texture) throw ...` becomes redundant — keep as a defensive assert or remove entirely.
   - `WgcCapture.AcquireNv12Slot` currently branches on `sharedFrameTexturePool is not null` — production always supplies a pool now (`WorkerCommandHandler` wires it). M5 can drop the M3 hand-rolled NV12 ring fallback path entirely. (Watch out: this means `WgcCaptureSource.Start()` 3-arg interface impl won't work standalone anymore. Decide: keep the fallback for non-worker callers, or require the pool. Spec implies require.)
3. **Restore coverage thresholds.** Currently `<Threshold>90,85</Threshold>` in `tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj` (M2 relaxed). Restore to `<Threshold>100</Threshold>` with `<ThresholdType>line,branch</ThresholdType>`. **Spec says "in `Directory.Build.props`" but per CLAUDE.md the actual location is the test csproj — same M2 deviation; preserve the deviation.** Backfilling tests to actually hit 100/100 is the meaty part — see "Coverage gap analysis" below.
4. **Update spec doc with measured before/after FRAMECOUNT numbers** in the spec's "Measured results" section (add the section if it doesn't exist; place it after "Coverage gate strategy during transition"). Pre-M1 baseline ≈100ms median; record the M4 measurement and any M5 measurement.
5. **Update `CLAUDE.md`** — the demo runbook still references `serve --hwnd <handle>` (v2 dropped that arg; viewer drives window selection). Update the "Running the demo end-to-end" section to current shape. Also document the new pipeline: WGC → `D3D11VideoProcessorColorConverter` → encoder's `hw_frames_ctx` pool → NVENC; mention `Direct3D11DeviceManager` is the worker-scope composition root (created by `WorkerCommandHandler`).
6. **Final manual smoke** confirming no regression vs M4 numbers.

### Coverage gap analysis (the M5 backfill problem)

The 90/85 gate was relaxed from 100/100 because main was already failing at 94.01%/89.85% as of the M2 commit (memory `project_coverage_gate_red_on_main.md`). M3+M4 added `#if WINDOWS`-guarded code that's excluded from the unit project (which targets `net8.0`). So the gap is **pre-existing v2 hosting / coordinator / discovery code** that lacks unit tests — not the GPU pipeline work. M5's coverage restoration is therefore primarily a v2-era backfill task, not a GPU-pipeline task.

To find the gaps, run:
```bash
dotnet test C:/Users/mtsch/WindowStream/tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj
# Then open tests/WindowStream.Core.Tests/coverage.cobertura.xml
# Or use a tool: `dotnet tool install -g dotnet-reportgenerator-globaltool` then `reportgenerator -reports:coverage.cobertura.xml -targetdir:coverage-report -reporttypes:Html`
```

Likely uncovered hotspots (from the per-module numbers): the CLI module (`windowstream` at 86.9% line / 25% branch), and v2-era hosting coordination code in `WindowStream.Core` (worker supervisor, stream router, load shedder, focus relay). Each addition should be a unit test with a reasonable fake — don't write integration tests just to pad coverage.

### M5 plan does not yet exist

M5 needs its own plan written first, same shape as M3/M4 (use `superpowers:writing-plans`). The spec's M5 section is the input. Estimated size: ~200 LOC net (mostly deletions + tests). Smaller than M3/M4.

### Things that might bite during M5

- **`pendingPoolFramePointers` queue ordering.** The M4 encoder uses a FIFO queue keyed by acquire/encode order, with a defensive pointer-equality assertion in `EncodeOnThread`. If M5's cleanup work or M5 manual smoke shows that assertion firing in production, replace with a `Dictionary<nint, AVFrame*>` lookup keyed by texture pointer.
- **`InternalsVisibleTo` for test fakes.** When `CapturedFrame.FromBytes` becomes internal, `WindowStream.Integration.Tests` and `WindowStream.Core.Tests` both need IVT (Core.csproj already has both as of `ab1685e`). `SolidColorFrameFactory` lives in the integration project, so the IVT chain works. If you discover a test in another assembly that needs the bytes path, add IVT for it too.
- **`WgcCaptureSource.Start(handle, options, cancellationToken)` 3-arg interface impl.** If M5 drops the no-pool fallback, this 3-arg overload either becomes unsupported (throws) or stays as a back-compat path that creates its own internal pool (overkill). Decide before refactoring.
- **The `EncoderException` for byte-bearing frames in `EncodeOnThread`.** If you remove the discriminator check entirely, the encoder will silently produce garbage if a bytes-bearing frame ever reaches it (e.g. from a stale test or a misconfigured test fake). Recommend keeping as a `Debug.Assert` or a one-line guard. Don't drop the safety net just because it shouldn't fire.
- **Pre-M1 sws_scale gotcha is gone.** CLAUDE.md mentions "odd-height windows crash the sws_scale pump." That gotcha is OBSOLETE post-M4 — `sws_scale` is removed entirely. Update CLAUDE.md to drop or rephrase that note (the GPU video processor handles odd dimensions correctly).

### Quick "is M5 done" checklist

- `git grep "FromBytes" -- src/` returns zero hits in production code (only tests + the factory definition itself)
- `git grep "FrameRepresentation" -- src/` returns only the enum definition
- Coverage gate at 100/100 line/branch, suite green
- Spec has measured before/after numbers in a "Measured results" section
- CLAUDE.md describes the post-M4 pipeline and `Direct3D11DeviceManager` composition root
- Manual smoke passes again (no regression vs M4)
