# Vintage input→present A/B — 2026-05-10 handoff

**Status:** Current-main numbers landed (this session). Swimmy-vintage measurement on the same methodology is the only remaining row in the A/B comparison.

## What's been measured (current main, post-M5)

Source: native 165 Hz Chrome kiosk (`tools/latency-clock.html`), GXR via Wi-Fi, post-M5 GPU-resident pipeline + `KEY_LOW_LATENCY` MediaCodec.

- **HMD-camera passthrough Δ:** ~48 ms median, σ near zero (4 sweep-bar reads at 3 s spacing across a 15 s recording).
- **FRAMECOUNT (1067 paired frames):** convert→present p50 **30 ms** / p95 **40 ms**.
  - convert→enc (NVENC): p50 8 ms, p95 11 ms
  - enc→reasm (network): p50 2 ms, p95 7 ms
  - reasm→dec (MediaCodec wait): p50 12 ms, p95 17 ms
  - dec→present (Choreographer): p50 11 ms, p95 17 ms
- NVENC queue depth: median 1, max 2.
- Source-rate insensitive: 165 → 30 fps cap moved the HMD-camera Δ by <2 ms (within sample noise). Source rate is not the bottleneck at this scale.

Memory snapshot: `~/.claude/projects/C--Users-mtsch-WindowStream/memory/project_input_present_2026_05_10_measurement.md`.

## What this handoff is for

Run the same HMD-camera methodology against swimmy-era vintage (`83384b6`) and land the resulting row in the spec timeline. The previous swimmy-era test (2026-05-09, FRAMECOUNT-based on Unity 4K source) showed per-frame latency was the same as M5; the gift was variance reduction. **This A/B is a different methodology** (input→present including WGC-arrival + HMD passthrough chain) and the comparison may or may not show the same flat result — that's the question.

## Resume sequence

```powershell
# 1. Worktree at swimmy-era so current main stays clean.
cd C:\Users\mtsch\WindowStream
git worktree add ../WindowStream-swimmy 83384b6
cd ../WindowStream-swimmy

# 2. Build .NET in Release. (`dotnet run` works; `dotnet <dll>` crashes
#    at avcodec_find_encoder_by_name — see
#    `~/.claude/notes/reference_windowstream_swimmy_vintage_gotchas.md`.)
dotnet build -c Release src/WindowStream.Cli/WindowStream.Cli.csproj `
  -f net8.0-windows10.0.19041.0

# 3. Build vintage portable APK from this worktree (gradle is fast,
#    flavor split exists at this commit despite what the 2026-05-09
#    handoff said).
cd viewer/WindowStreamViewer
./gradlew :app:assemblePortableDebug
adb -s 192.168.50.111:40393 install -r app/build/outputs/apk/portable/debug/app-portable-debug.apk
cd ../..

# 4. Open the latency clock fresh on the 165 Hz LG TV (the only active
#    monitor on chonkers; DWM compositor at 165 Hz independently).
$url = 'file:///' + (Resolve-Path 'tools/latency-clock.html').Path.Replace('\','/')
Start-Process 'C:\Program Files\Google\Chrome\Application\chrome.exe' `
  -ArgumentList '--kiosk',$url

# 5. Find the kiosk HWND. v1 server takes the HWND on the command line.
.\src\WindowStream.Cli\bin\Release\net8.0-windows10.0.19041.0\windowstream.exe list `
  | Select-String -Pattern 'latency clock'

# 6. Start v1 server with the HWND. Read TCP port from banner stderr.
$env:WINDOWSTREAM_SKIP_NVENC = $null
dotnet run --project src/WindowStream.Cli `
  -f net8.0-windows10.0.19041.0 --no-build -- serve --hwnd <HWND> `
  > .claude/scripts/vintage-server.out.log `
  2> .claude/scripts/vintage-server.err.log
# (run in background or new terminal; read the banner for the port)

# 7. Fire the recording script. v1 viewer intent has NO
#    `selectedWindowHwnds` extra — server already chose the window.
#    The current `record-latency-clock.bat` includes that extra and will
#    need a small edit (or a separate vintage variant) — easiest is to
#    fire the intent manually and run screenrecord by hand:
adb -s 192.168.50.111:40393 shell am force-stop com.mtschoen.windowstream.viewer
adb -s 192.168.50.111:40393 logcat -c
adb -s 192.168.50.111:40393 shell am start `
  -n com.mtschoen.windowstream.viewer/.demo.DemoActivity `
  --es streamHost 192.168.50.75 --ei streamPort <TCP_PORT>
# Wait ~3 s for handshake. HMD on, position both clocks in your gaze.
adb -s 192.168.50.111:40393 shell screenrecord --time-limit 15 --bit-rate 20M `
  /sdcard/vintage-recording.mp4
adb -s 192.168.50.111:40393 pull /sdcard/vintage-recording.mp4 `
  .claude/scripts/vintage-recording.mp4
```

## Reading the recording

- **Trust the sweep bar, not the digit text.** Camera blur produces
  convincing false reads on the millisecond digit. Confirmed this
  session: text OCR'd `.480`, sweep marker said `.450`. Off by 30 ms.
- Extract frames at native resolution: `ffmpeg -ss <t> -i ... -frames:v
  1 -y <out>.png` (no `-vf scale`, no `-vf crop` — recording is
  2880×2880 PNG-friendly).
- 4 reads at 3 s spacing across the 15 s recording is enough — variance
  was effectively zero this session, so a small N is fine.

## Comparison target

Land the result as a new row in
`docs/superpowers/specs/2026-04-25-frame-counter-and-pipeline-lag-fix-design.md`
"Latency timeline" section, alongside:
- 2026-05-09 swimmy-era FRAMECOUNT row (per-frame transit)
- 2026-04-26 M5 baseline FRAMECOUNT row
- 2026-05-10 current-main FRAMECOUNT row (this session)
- **NEW: 2026-05-10 swimmy-vintage HMD-camera row + current-main HMD-camera row** as the A/B pair on the input→present methodology

## Gotchas to anticipate

- **Orphan worker bug is still open.** This session left worker 55392
  running indefinitely after viewer self-exit (concrete instance of
  `project_orphan_worker_wgc_lock`). Before each fresh measurement, kill
  any stray `windowstream.exe` worker processes:
  ```powershell
  Get-CimInstance Win32_Process -Filter "Name='windowstream.exe'" |
    Where-Object { $_.CommandLine -like '*worker*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
  ```
- **Edge kiosk WGC bust** is documented in
  `project_edge_kiosk_wgc_session_bust` — use Chrome, not Edge.
- **GXR off-head sandbox** kills the viewer's TCP via `procState=TPSL`.
  Wear the HMD or block the proximity sensor with a card during the
  whole 18 s window. Don't leave the card on long (thermal).
- **`Out-File` without `-Encoding utf8`** writes UTF-16 LE BOM that
  defeats the framecount-analyzer regex. Always pass `-Encoding utf8`
  when piping logcat to a file.

## Other open work carried over from the 2026-05-10 feasibility handoff

- **Write a formal spec** at
  `docs/superpowers/specs/2026-05-10-input-present-measurement.md`
  documenting the HMD-camera methodology (sweep-bar reading, source-rate
  insensitivity finding, FRAMECOUNT-vs-HMD-camera relationship). The
  memory file + this handoff cover the methodology informally; a real
  spec is the durable home for it.
- **File Gitea issues** for the three bugs surfaced in the 2026-05-10
  feasibility session — most are already in memory but not yet ticketed:
  - `project_orphan_worker_wgc_lock` — viewer self-exit leaves worker
    holding WGC. Concrete recurrence this session (worker 55392 lived
    13+ min after viewer disconnect). Fix: tear down the worker when
    `ServeViewerAsync`'s TCP receive throws.
  - `project_edge_kiosk_wgc_session_bust` — Edge kiosk transitions to
    WGC-hostile state mid-session. Workaround in memory; no upstream
    fix scoped.
  - GXR SurfaceView destroyed-during-startup race — already captured in
    `docs/superpowers/specs/2026-04-20-multi-window-followups.md` but
    not in Gitea.

## Useful artifacts left from this session

In `.claude/scripts/` (gitignored):
- `feasibility-recording-20260510-035519.mp4` + extracted PNGs — current-main 165 fps run
- `feasibility-recording-20260510-050218.mp4` + extracted PNGs — current-main 30 fps cap run
- `framecount-server-30fps.log` + `framecount-viewer-30fps.log` — sliced FRAMECOUNT logs that produced the per-stage table
- `latency-server.err.log` — full session server stderr (filter by `[worker:NNNNN]` to slice to a stream)

Tools added this session:
- `tools/latency-clock.html` now accepts `?cap=N` query param (titled `WindowStream latency clock (Nfps cap)`) for rate-cap diagnostics.
- `tools/raf-rate-probe.html` — standalone RAF/sec meter; useful for verifying Chrome isn't being throttled below the monitor's refresh.
- `.claude/scripts/record-latency-clock.bat` honors `$env:TCP_PORT` (defaults to 61613) so it works across server restarts without editing.
