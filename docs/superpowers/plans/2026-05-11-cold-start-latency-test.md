# Cold-start latency-clock recording script — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `tools/record-latency-clock.bat` with a single PowerShell script that takes the HMD-camera latency test from cold-start (HMD on, nothing else running) to recording on disk, with fail-fast diagnostics at every step that has historically failed.

**Architecture:** One PowerShell file `tools/record-latency-clock.ps1` (~250 lines) — pure orchestration with no new C# or Kotlin code. Reuses `windowstream.exe list`, `windowstream.exe serve`, and the existing `DemoActivity` adb intent. Config cache in `tools/.latency-test-config.json` (gitignored) stores the last working GXR `ip:port` for the `adb connect` fallback path. The sibling `tools/record-latency-clock-vintage.bat` stays untouched (it serves a different measurement).

**Tech Stack:** PowerShell 5.1 (Windows-bundled), adb, `windowstream.exe`, ffmpeg.

**Banner format (verified against `src/WindowStream.Core/Hosting/CoordinatorLauncher.cs:154`):** `serve` writes `windowstream: serving on TCP <port>, UDP <port>` to **stdout** (not stderr). FRAMECOUNT and other diagnostics go to stderr. The script must capture both.

**Validation approach:** PowerShell orchestration scripts in this repo have no unit-test framework; validation is targeted smoke + failure-injection runs as defined in Task 10. This plan does not write fake unit tests — it lists the exact runs that prove correctness.

---

## Phase 1: Preflight (Steps 1–3 of spec)

### Task 1: Scaffold the script + Step 1 sanity checks

**Files:**
- Create: `tools/record-latency-clock.ps1`

- [ ] **Step 1: Create the script with parameter block and Step 1**

```powershell
<#
.SYNOPSIS
    Cold-start HMD-camera latency-clock recording.

.DESCRIPTION
    Takes the WindowStream latency-clock test from "HMD on, nothing else
    running" to "15s screenrecord on disk", with fail-fast diagnostics at
    every step that has historically failed. Replaces
    record-latency-clock.bat; sibling record-latency-clock-vintage.bat is
    untouched.

    Steps:
      1. Sanity check cwd and binaries
      2. HMD adb connect (adb-native mDNS, cached fallback, prompted last)
      3. Find latency-clock HWND via `windowstream list`
      4. Start `serve`, parse TCP port from banner
      5. Frame-flow probe (4s, HMD off-head OK) with three diagnostic branches
      6. Go-on-head gate
      7. Real 15s screenrecord
      8. Teardown prompt

.PARAMETER Hwnd
    Override HWND auto-discovery (decimal integer).

.PARAMETER Duration
    Real recording duration in seconds. Default 15.

.PARAMETER ProbeDuration
    Frame-flow probe duration in seconds. Default 4.

.PARAMETER DecThreshold
    Minimum FRAMECOUNT stage=dec lines required in probe to PASS. Default 20.
#>
[CmdletBinding()]
param(
    [int]$Hwnd,
    [int]$Duration = 15,
    [int]$ProbeDuration = 4,
    [int]$DecThreshold = 20
)

$ErrorActionPreference = 'Stop'

# Hardcoded per project memory; update site below if PC IP or GXR change.
$HostIp     = '192.168.50.75'
$GxrSerial  = 'R3GYB04E2WB'
$ViewerPkg  = 'com.mtschoen.windowstream.viewer'
$DemoActivity = "$ViewerPkg/.demo.DemoActivity"

$RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..')
$CliExe     = Join-Path $RepoRoot 'src\WindowStream.Cli\bin\Release\net8.0-windows10.0.19041.0\windowstream.exe'
$ConfigFile = Join-Path $PSScriptRoot '.latency-test-config.json'

function Fail($message) {
    Write-Host ""
    Write-Host "FAIL: $message" -ForegroundColor Red
    exit 1
}

function Info($message) { Write-Host $message -ForegroundColor Cyan }
function Ok($message)   { Write-Host "  OK: $message" -ForegroundColor Green }

# === Step 1: sanity ===========================================================
Info "[1/8] Sanity check"
if (-not (Test-Path $CliExe)) {
    Fail "windowstream.exe not found at $CliExe. Run: dotnet build -c Release src/WindowStream.Cli/"
}
$ffmpegDll = Get-ChildItem -Path (Split-Path $CliExe) -Filter 'avcodec-*.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $ffmpegDll) {
    Fail @"
FFmpeg DLLs missing next to windowstream.exe. Either:
  - Install OBS Studio (provides them at C:\Program Files\obs-studio\bin\64bit\)
  - Or drop avcodec-*.dll, avutil-*.dll, swscale-*.dll, swresample-*.dll, zlib.dll, libx264-*.dll
    into $(Split-Path $CliExe)
"@
}
Ok "Binaries present at $($CliExe)"
```

- [ ] **Step 2: Verify the script parses and Step 1 runs**

Run:
```powershell
pwsh -NoProfile -File tools/record-latency-clock.ps1
```

Expected: prints `[1/8] Sanity check` and `OK: Binaries present at ...`, then errors at the next step (which doesn't exist yet). If the windowstream.exe path is wrong, fix the path before continuing.

- [ ] **Step 3: Commit**

```bash
git add tools/record-latency-clock.ps1
git commit -m "tools: scaffold cold-start latency-clock script (preflight only)"
```

---

### Task 2: Step 2 — HMD adb (mDNS-native, cached, prompted)

**Files:**
- Modify: `tools/record-latency-clock.ps1` (append below Step 1)

- [ ] **Step 1: Add the adb discovery block**

```powershell
# === Step 2: HMD adb ==========================================================
Info "[2/8] HMD adb connect"

function Get-GxrDeviceId {
    $devicesOutput = & adb devices 2>&1
    foreach ($line in $devicesOutput) {
        if ($line -match "^(\S+$GxrSerial\S*)\s+device") {
            return $matches[1]
        }
    }
    return $null
}

function Read-CachedHmdIpPort {
    if (Test-Path $ConfigFile) {
        try { return (Get-Content $ConfigFile -Raw | ConvertFrom-Json).hmdIpPort }
        catch { return $null }
    }
    return $null
}

function Save-HmdIpPort($ipPort) {
    @{ hmdIpPort = $ipPort } | ConvertTo-Json | Set-Content -Path $ConfigFile -Encoding utf8
}

# 2a: try adb devices first (mDNS-native path)
$DeviceId = Get-GxrDeviceId
if ($DeviceId) { Ok "Found GXR: $DeviceId"; }

# 2b: kick the adb mDNS subsystem
if (-not $DeviceId) {
    Info "  GXR not in adb devices; restarting adb daemon to refresh mDNS..."
    & adb kill-server 2>&1 | Out-Null
    & adb start-server 2>&1 | Out-Null
    Start-Sleep -Seconds 2
    $DeviceId = Get-GxrDeviceId
    if ($DeviceId) { Ok "Found GXR after daemon restart: $DeviceId" }
}

# 2c: try cached ip:port
if (-not $DeviceId) {
    $cached = Read-CachedHmdIpPort
    if ($cached) {
        Info "  Trying cached HMD ip:port $cached..."
        & adb connect $cached 2>&1 | Out-Null
        Start-Sleep -Seconds 1
        $DeviceId = Get-GxrDeviceId
        if ($DeviceId) { Ok "Connected via cached $cached" }
    }
}

# 2d: prompt user
if (-not $DeviceId) {
    Write-Host ""
    Write-Host "  GXR not auto-discovered. On the HMD: Developer options ->" -ForegroundColor Yellow
    Write-Host "  Wireless debugging -> look at 'IP address & Port'." -ForegroundColor Yellow
    $userInput = Read-Host "  Enter GXR ip:port (e.g. 192.168.50.42:5555)"
    if (-not $userInput) { Fail "No ip:port provided." }
    & adb connect $userInput 2>&1 | Out-Null
    Start-Sleep -Seconds 1
    $DeviceId = Get-GxrDeviceId
    if (-not $DeviceId) {
        Fail "adb connect $userInput did not produce a device. Check HMD is awake, on Wi-Fi, and adb-wifi paired (`adb pair <ip>:<pair-port> <code>` once if first time)."
    }
    Save-HmdIpPort $userInput
    Ok "Connected via prompted $userInput (cached for next run)"
}
```

- [ ] **Step 2: Test with HMD already connected**

Run:
```powershell
pwsh -NoProfile -File tools/record-latency-clock.ps1
```

Expected: prints `[2/8] HMD adb connect` and `OK: Found GXR: adb-R3GYB04E2WB-...`. If GXR is not connected, you'll get prompted for ip:port — that path is tested in Task 10.

- [ ] **Step 3: Commit**

```bash
git add tools/record-latency-clock.ps1
git commit -m "tools: add HMD adb mDNS-native discovery with cached fallback"
```

---

### Task 3: Step 3 — Source-window HWND discovery

**Files:**
- Modify: `tools/record-latency-clock.ps1` (append)

- [ ] **Step 1: Add HWND discovery**

```powershell
# === Step 3: source-window HWND ==============================================
Info "[3/8] Find latency-clock HWND"

if ($Hwnd) {
    Ok "Using HWND override: $Hwnd"
    $TargetHwnd = $Hwnd
} else {
    $listOutput = & $CliExe list 2>&1
    $match = $listOutput | Where-Object { $_ -match '(?i)latency clock' } | Select-Object -First 1
    if (-not $match) {
        Fail @"
No window matching 'latency clock' in `windowstream list` output.
Open tools/latency-clock.html in a browser (Edge or Chrome, fullscreen
ideal; AVOID Chrome --kiosk — known WGC frame-conversion bug, see
project_chrome_kiosk_wgc_conversion_fail.md). Then re-run this script.

Pass -Hwnd <int> to override and target a different window.
"@
    }
    # `windowstream list` format: "HANDLE       PROCESS              TITLE"
    # First token is the HWND.
    $TargetHwnd = ($match -split '\s+', 4)[0]
    if (-not ($TargetHwnd -match '^\d+$')) {
        Fail "Could not parse HWND from list output line: '$match'"
    }
    Ok "Source HWND: $TargetHwnd ('$match')"
}
```

- [ ] **Step 2: Test with latency-clock.html open**

In Edge or Chrome (not --kiosk), open `file:///C:/Users/mtsch/WindowStream/tools/latency-clock.html` and press F11. Then:

```powershell
pwsh -NoProfile -File tools/record-latency-clock.ps1
```

Expected: prints `[3/8] Find latency-clock HWND` and `OK: Source HWND: <number> ('<line>')`.

Test the negative path by closing the browser, re-running, and verifying the fail message includes the "Open tools/latency-clock.html" instruction.

- [ ] **Step 3: Commit**

```bash
git add tools/record-latency-clock.ps1
git commit -m "tools: add latency-clock HWND discovery via windowstream list"
```

---

## Phase 2: Server lifecycle + frame-flow probe (Steps 4–5)

### Task 4: Step 4 — Start `serve` and parse the TCP port

**Files:**
- Modify: `tools/record-latency-clock.ps1` (append)

- [ ] **Step 1: Add serve launch + banner parse**

```powershell
# === Step 4: start serve, parse banner ========================================
Info "[4/8] Start serve and parse TCP port"

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$ServerStdoutLog = Join-Path $env:TEMP "windowstream-serve-$stamp.out.log"
$ServerStderrLog = Join-Path $env:TEMP "windowstream-serve-$stamp.err.log"

$ServerProcess = Start-Process -FilePath $CliExe `
    -ArgumentList 'serve' `
    -RedirectStandardOutput $ServerStdoutLog `
    -RedirectStandardError  $ServerStderrLog `
    -WindowStyle Normal `
    -PassThru

# Banner is written to stdout (confirmed via CoordinatorLauncher.cs:154):
#   windowstream: serving on TCP <port>, UDP <port>
# Poll up to 10 seconds.
$TcpPort = $null
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline) {
    if (Test-Path $ServerStdoutLog) {
        $content = Get-Content $ServerStdoutLog -Raw -ErrorAction SilentlyContinue
        if ($content -match 'windowstream: serving on TCP (\d+), UDP (\d+)') {
            $TcpPort = [int]$matches[1]
            $UdpPort = [int]$matches[2]
            break
        }
    }
    Start-Sleep -Milliseconds 100
}
if (-not $TcpPort) {
    Fail @"
serve banner did not appear within 10s.
Server process PID: $($ServerProcess.Id)
stdout log: $ServerStdoutLog
stderr log: $ServerStderrLog
Investigate, then kill PID and try again.
"@
}
Ok "serve PID $($ServerProcess.Id), TCP $TcpPort, UDP $UdpPort"
Ok "stderr log: $ServerStderrLog"
```

- [ ] **Step 2: Test end-to-end up through this step**

With the latency-clock browser open, run:
```powershell
pwsh -NoProfile -File tools/record-latency-clock.ps1
```

Expected: Steps 1-4 print OK lines including the parsed TCP port. The `serve` window opens. After the script exits at the (still-missing) next step, the server is left running — kill it manually for now:

```powershell
Stop-Process -Id <pid-printed-above>
```

UAC may prompt for firewall rules on first launch; allow if it does.

- [ ] **Step 3: Commit**

```bash
git add tools/record-latency-clock.ps1
git commit -m "tools: start serve and parse TCP port from banner"
```

---

### Task 5: Step 5 — Frame-flow probe with diagnostic branches

**Files:**
- Modify: `tools/record-latency-clock.ps1` (append)

- [ ] **Step 1: Add the probe**

```powershell
# === Step 5: frame-flow probe =================================================
Info "[5/8] Frame-flow probe ($ProbeDuration s, HMD off-head OK)"

& adb -s $DeviceId shell am force-stop $ViewerPkg 2>&1 | Out-Null
& adb -s $DeviceId logcat -c 2>&1 | Out-Null

& adb -s $DeviceId shell am start -n $DemoActivity `
    --es streamHost $HostIp `
    --ei streamPort $TcpPort `
    --ela selectedWindowHwnds $TargetHwnd 2>&1 | Out-Null

Start-Sleep -Seconds $ProbeDuration

$logcatDump = & adb -s $DeviceId logcat -d 2>&1
$decCount = ($logcatDump | Select-String 'FRAMECOUNT.*stage=dec').Count

# Server stderr emits [FRAMECOUNT] stage=enc lines (per CLAUDE.md).
$encCount = 0
if (Test-Path $ServerStderrLog) {
    $encCount = (Get-Content $ServerStderrLog | Select-String 'stage=enc').Count
}

Info "  probe results: dec=$decCount, enc=$encCount (in ${ProbeDuration}s)"

# Always force-stop the viewer after the probe — we'll restart it for the real record.
& adb -s $DeviceId shell am force-stop $ViewerPkg 2>&1 | Out-Null

if ($decCount -ge $DecThreshold) {
    Ok "Frames flowing healthy ($decCount >= $DecThreshold)"
} elseif ($decCount -eq 0 -and $encCount -eq 0) {
    Fail @"
Server isn't producing frames. Most likely the WGC capture pump can't
attach to the source window. Try:
  - A different source window (Windows Terminal with a spinner; a Unity
    Editor scene; non-kiosk Edge with the clock).
  - Memory: project_chrome_kiosk_wgc_conversion_fail.md,
            project_edge_kiosk_wgc_session_bust.md,
            project_firefox_wgc_silent_fail.md,
            project_orphan_worker_wgc_lock.md
Server stderr: $ServerStderrLog (leave running for inspection;
PID $($ServerProcess.Id))
"@
} elseif ($decCount -eq 0 -and $encCount -gt 0) {
    Fail @"
Server is encoding ($encCount enc lines) but viewer received nothing.
Check:
  - Windows Firewall allowed ports $TcpPort/TCP and $TcpPort/UDP
    (UAC may have been denied on serve launch).
  - HMD on the same Wi-Fi subnet as PC ($HostIp).
  - HMD is awake (off-head with proximity card sometimes wedges
    the radio; put HMD on briefly to test).
adb logcat -d ran against device: $DeviceId
"@
} else {
    Fail @"
Low frame rate (dec=$decCount, threshold=$DecThreshold).
The latency-clock.html page is self-animating, so a low rate usually
means the browser tab is unfocused or minimised. Bring it to the
foreground and re-run.
"@
}
```

- [ ] **Step 2: Test the happy path**

With latency-clock open in a foreground browser and HMD reachable but
off-head, run the full script. Steps 1-5 should print OK lines and end
with `Frames flowing healthy (NN >= 20)`. Manually kill the server
process printed in Step 4.

- [ ] **Step 3: Test the dec=0 enc=0 branch (no HMD time needed)**

Close the latency-clock browser. Re-open it but minimise it (or open
on a secondary monitor that's powered off). Re-run. The probe should
hit the WGC-capture-failed branch with the diagnostic memory list.

(Optional negative test for the dec=0 enc>0 branch: temporarily remove
the WindowStream-Session firewall rules. Skip if you don't want to
fiddle with firewall.)

- [ ] **Step 4: Commit**

```bash
git add tools/record-latency-clock.ps1
git commit -m "tools: add 4s frame-flow probe with 3 diagnostic branches"
```

---

## Phase 3: Recording + teardown (Steps 6–8)

### Task 6: Steps 6–7 — Go-on-head gate + real record

**Files:**
- Modify: `tools/record-latency-clock.ps1` (append)

- [ ] **Step 1: Add gate and recording**

```powershell
# === Step 6: go-on-head gate =================================================
Info "[6/8] Go on-head"
Write-Host ""
Write-Host "  Put the HMD on, position the host monitor in your gaze," -ForegroundColor Yellow
Write-Host "  then press ENTER to record ${Duration}s." -ForegroundColor Yellow
Read-Host | Out-Null

# === Step 7: real record =====================================================
Info "[7/8] Recording ${Duration}s"

$OutputDir = $PSScriptRoot
$RecordingMp4   = Join-Path $OutputDir "feasibility-recording-$stamp.mp4"
$RecordingFrame = Join-Path $OutputDir "feasibility-recording-$stamp-frame.jpg"
$RemoteMp4 = '/sdcard/feasibility-recording.mp4'

& adb -s $DeviceId logcat -c 2>&1 | Out-Null
& adb -s $DeviceId shell am start -n $DemoActivity `
    --es streamHost $HostIp `
    --ei streamPort $TcpPort `
    --ela selectedWindowHwnds $TargetHwnd 2>&1 | Out-Null

# Handshake settle
Start-Sleep -Seconds 3

Info "  recording NOW -- position both clocks in your gaze"
& adb -s $DeviceId shell screenrecord --time-limit $Duration --bit-rate 20M $RemoteMp4

& adb -s $DeviceId pull $RemoteMp4 $RecordingMp4 2>&1 | Out-Null
if (-not (Test-Path $RecordingMp4)) {
    Fail "adb pull failed; recording left at $RemoteMp4 on device $DeviceId for manual recovery."
}
& adb -s $DeviceId shell rm $RemoteMp4 2>&1 | Out-Null

# Midpoint frame extraction (ffmpeg must be on PATH)
$ffmpegOnPath = Get-Command ffmpeg -ErrorAction SilentlyContinue
if ($ffmpegOnPath) {
    & ffmpeg -ss ([int]($Duration / 2)) -i $RecordingMp4 -frames:v 1 -y $RecordingFrame 2>$null
}

Ok "Recording: $RecordingMp4"
if (Test-Path $RecordingFrame) { Ok "Frame:     $RecordingFrame" }
```

- [ ] **Step 2: End-to-end smoke test up through this step**

Run the full script. Go on-head when prompted. After 15s, you should
have an mp4 in `tools/` showing the HMD passthrough with the latency
clock visible. Inspect the midpoint frame.

If the recording is dead/black, the issue is in Step 7 alone (since
Step 5 already proved frames flow). Most likely cause: the HMD entered
sleep or lost the WiFi during the 3s handshake settle. Check
`adb logcat -d --pid=$(adb shell pidof com.mtschoen.windowstream.viewer)`.

- [ ] **Step 3: Commit**

```bash
git add tools/record-latency-clock.ps1
git commit -m "tools: add on-head gate and 15s record/pull/frame-extract"
```

---

### Task 7: Step 8 — Teardown prompt

**Files:**
- Modify: `tools/record-latency-clock.ps1` (append)

- [ ] **Step 1: Add teardown**

```powershell
# === Step 8: teardown ========================================================
Info "[8/8] Off-head"
Write-Host ""
Write-Host "  Recording done. Come off-head when you're ready." -ForegroundColor Yellow
$tearDown = Read-Host "  Tear down server + firewall rules? [y/N]"

if ($tearDown -match '^[Yy]') {
    Stop-Process -Id $ServerProcess.Id -Force -ErrorAction SilentlyContinue
    Get-NetFirewallRule -DisplayName 'WindowStream-Session-*' -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
    Ok "Server stopped, firewall rules removed."
} else {
    Write-Host ""
    Write-Host "  Server PID: $($ServerProcess.Id) left running." -ForegroundColor Yellow
    Write-Host "  To stop later:  Stop-Process -Id $($ServerProcess.Id)" -ForegroundColor Yellow
    Write-Host "  Firewall cleanup (also handled by /wrap):" -ForegroundColor Yellow
    Write-Host "    Get-NetFirewallRule -DisplayName WindowStream-Session-* | Remove-NetFirewallRule" -ForegroundColor Yellow
}
```

- [ ] **Step 2: Test both branches**

Run the full script; at the prompt, answer `y` once and `N` once
(separate runs). Verify the server window closes on `y` and stays open
on `N` with the manual commands printed.

- [ ] **Step 3: Commit**

```bash
git add tools/record-latency-clock.ps1
git commit -m "tools: add teardown prompt and manual-cleanup hint"
```

---

## Phase 4: Cleanup and docs

### Task 8: Delete the old `.bat` and gitignore the config cache

**Files:**
- Delete: `tools/record-latency-clock.bat`
- Modify: `.gitignore` (add `tools/.latency-test-config.json`)

- [ ] **Step 1: Delete the old script**

```bash
git rm tools/record-latency-clock.bat
```

- [ ] **Step 2: Gitignore the cache**

Append to `.gitignore`:

```
# Local latency-test config (cached HMD ip:port)
tools/.latency-test-config.json
```

- [ ] **Step 3: Verify**

```powershell
git status
```

Expected: `tools/record-latency-clock.bat` shown deleted, `.gitignore` shown modified. The `.latency-test-config.json` (if it exists from prior runs) should NOT appear in git status.

- [ ] **Step 4: Commit**

```bash
git add .gitignore
git commit -m "tools: drop record-latency-clock.bat in favor of .ps1"
```

---

### Task 9: Update CLAUDE.md

**Files:**
- Modify: `CLAUDE.md` — "Running the demo end-to-end" section

- [ ] **Step 1: Add the new one-liner at the top of "Running the demo end-to-end"**

Find the section starting `## Running the demo end-to-end`. Insert immediately after that heading (above `### Server side (Windows)`):

```markdown
### Fast path: HMD-camera latency-clock test

For the standard latency measurement (cold start, with HMD on but
nothing else running):

```powershell
pwsh tools/record-latency-clock.ps1
```

The script handles adb wifi connect, source-window detection, server
launch, and a 4-second frame-flow probe before asking you to go
on-head. Diagnostics on every common failure mode (no HMD,
no source window, WGC capture failed, network blocked).

The manual recipe below is the fallback when the script itself is
broken or you want to test something the script doesn't cover.
```

(Keep the existing `### Server side (Windows)` and `### Viewer side`
subsections below this — they become the "Manual fallback" content.)

- [ ] **Step 2: Verify**

```powershell
Get-Content CLAUDE.md | Select-String 'Fast path|record-latency-clock' | Select-Object -First 5
```

Expected: shows the new section header.

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: point CLAUDE.md at new cold-start latency script"
```

---

### Task 10: Smoke test pass

**Goal:** Prove the script works end-to-end from a cold start and that
each failure-injection scenario produces the right diagnostic.

- [ ] **Step 1: Cold-start happy path**

1. Close all browsers showing latency-clock.
2. Stop any running `windowstream.exe` (`Get-Process windowstream | Stop-Process`).
3. Ensure HMD is reachable but off-head.
4. Open Edge to `file:///C:/Users/mtsch/WindowStream/tools/latency-clock.html` and F11.
5. Run: `pwsh -NoProfile -File tools/record-latency-clock.ps1`
6. Expect: Steps 1-5 print OK with `dec >= 20` from the probe.
7. Go on-head, press ENTER.
8. After 15s, verify the .mp4 and -frame.jpg land in `tools/`.
9. Inspect the frame: both clocks should be visible.
10. Answer `y` to teardown.

If the .mp4 looks healthy and the frame shows readable clocks, mark this step done.

- [ ] **Step 2: Failure injection — no source window**

Close the latency-clock browser. Re-run. Expect the script to fail at
Step 3 with the "Open tools/latency-clock.html ..." instruction.

- [ ] **Step 3: Failure injection — source window static / minimised**

Open Notepad, type nothing. Re-run with `-Hwnd <notepad-hwnd>` (get the
HWND from `windowstream list`). Expect Step 5 to hit the "Low frame
rate" branch (Notepad emits ≤1 frame to WGC).

- [ ] **Step 4: Failure injection — HMD unreachable**

Run `adb kill-server`, then disconnect the HMD from Wi-Fi (or just
power it down briefly). Re-run. Expect Step 2 to prompt for ip:port,
fail the connect, and exit 1 with the "Check HMD is awake..."
diagnostic. After this, reconnect the HMD before the next test.

- [ ] **Step 5: Commit a record of what was validated**

If all four smoke tests pass, no code change is needed; just commit a
note in the project log. If something failed, fix the script and
re-run the affected smoke test.

```bash
# Only if you needed to make fixes during smoke testing:
git add tools/record-latency-clock.ps1
git commit -m "tools: fix <specific issue found during smoke test>"
```

- [ ] **Step 6: Save a memory of the working flow**

Add to `~/.claude/projects/C--Users-mtsch-WindowStream/memory/`:

`project_cold_start_latency_script.md` — one paragraph describing the
script's purpose, what it auto-discovers (GXR via mDNS, HWND via
windowstream list, TCP port via banner parse), and the four
failure-injection cases proved on this date.

Add the corresponding line to `MEMORY.md`.

---

## Spec Coverage Summary

| Spec section | Tasks |
|---|---|
| Step 1 (sanity) | Task 1 |
| Step 2 (HMD adb) | Task 2 |
| Step 3 (HWND) | Task 3 |
| Step 4 (serve + port) | Task 4 |
| Step 5 (probe + diagnostics) | Task 5 |
| Steps 6-7 (gate + record) | Task 6 |
| Step 8 (teardown) | Task 7 |
| Deletion of old .bat | Task 8 |
| CLAUDE.md update | Task 9 |
| Smoke tests | Task 10 |

All spec sections have implementing tasks. No placeholders remain in
the plan. Type/name consistency: `$DeviceId`, `$TargetHwnd`,
`$TcpPort`, `$ServerProcess`, `$ServerStdoutLog`, `$ServerStderrLog`,
`$stamp` are defined in their first use task and reused consistently
through subsequent tasks.
