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

.PARAMETER Cap
    requestAnimationFrame cap in fps passed as ?cap=N to latency-clock.html.
    Matches the 2026-05-11 baseline. Set to 0 to omit and let RAF run native.
    Default 165.

.PARAMETER ProbeMaxAttempts
    Max attempts at the WGC probe before giving up. The Chrome --kiosk WGC
    bug (project_chrome_kiosk_wgc_conversion_fail.md) often clears after one
    kill + relaunch, so the default of 3 is usually enough. Default 3.
#>
[CmdletBinding()]
param(
    [int]$Hwnd,
    [int]$Duration = 15,
    [int]$ProbeDuration = 4,
    [int]$DecThreshold = 20,
    [int]$Cap = 165,
    [int]$ProbeMaxAttempts = 3
)

# Continue (not Stop): PS 5.1 wraps native-exe stderr as NativeCommandError
# records at the binding boundary even when the stream is redirected to $null.
# Stop would abort on benign adb info like "* daemon not running; starting now".
# Error handling is explicit via Fail() with exit 1; we don't rely on Stop.
$ErrorActionPreference = 'Continue'

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

# === Step 2: HMD adb ==========================================================
Info "[2/8] HMD adb connect"

function Get-GxrDeviceId {
    $devicesOutput = & adb devices 2>$null
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
    & adb kill-server *> $null
    & adb start-server *> $null
    Start-Sleep -Seconds 2
    $DeviceId = Get-GxrDeviceId
    if ($DeviceId) { Ok "Found GXR after daemon restart: $DeviceId" }
}

# 2c: try cached ip:port
if (-not $DeviceId) {
    $cached = Read-CachedHmdIpPort
    if ($cached) {
        Info "  Trying cached HMD ip:port $cached..."
        & adb connect $cached *> $null
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
    & adb connect $userInput *> $null
    Start-Sleep -Seconds 1
    $DeviceId = Get-GxrDeviceId
    if (-not $DeviceId) {
        Fail "adb connect $userInput did not produce a device. Check HMD is awake, on Wi-Fi, and adb-wifi paired (`adb pair <ip>:<pair-port> <code>` once if first time)."
    }
    Save-HmdIpPort $userInput
    Ok "Connected via prompted $userInput (cached for next run)"
}

# === Step 3: source-window HWND ==============================================
Info "[3/8] Find latency-clock HWND"

function Find-LatencyClockMatch {
    $listOutput = & $CliExe list 2>$null
    return ($listOutput | Where-Object { $_ -match '(?i)latency clock' } | Select-Object -First 1)
}

function Resolve-ChromePath {
    foreach ($p in @(
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
        "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
    )) {
        if (Test-Path $p) { return $p }
    }
    $found = Get-Command chrome.exe -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    return $null
}

function Resolve-EdgePath {
    $found = Get-Command msedge.exe -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    foreach ($p in @(
        'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
        'C:\Program Files\Microsoft\Edge\Application\msedge.exe'
    )) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

function Stop-LatencyClockBrowsers {
    # Kill any chrome.exe / msedge.exe whose CommandLine references
    # latency-clock.html. Filtered by CommandLine, NEVER by title
    # (memory: feedback_never_kill_by_window_title). The user's other
    # browser tabs (latency-clock not in their CommandLine) are untouched.
    $procs = Get-CimInstance Win32_Process |
        Where-Object {
            ($_.Name -eq 'chrome.exe' -or $_.Name -eq 'msedge.exe') -and
            $_.CommandLine -and $_.CommandLine -match 'latency-clock\.html'
        }
    foreach ($p in $procs) {
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    if ($procs) { Start-Sleep -Milliseconds 800 }
    return ($procs | Measure-Object).Count
}

function Start-LatencyClockBrowser($ClockUrl) {
    # Chrome --kiosk first (matches the 2026-05-11 baseline measurement command);
    # Edge --app= fallback (chromeless without Fullscreen Optimizations).
    # Both keep the page foregrounded with no browser chrome --
    # "fullscreen-but-not-fullscreen" from the prior session.
    $chromePath = Resolve-ChromePath
    if ($chromePath) {
        Info "  Launching Chrome --kiosk..."
        Start-Process -FilePath $chromePath -ArgumentList '--kiosk', $ClockUrl | Out-Null
        return
    }
    $edgePath = Resolve-EdgePath
    if ($edgePath) {
        Info "  Chrome not found; launching Edge --app..."
        Start-Process -FilePath $edgePath -ArgumentList "--app=$ClockUrl" | Out-Null
        return
    }
    Fail "Neither Chrome nor Edge found on this machine. Install one or open tools/latency-clock.html manually and re-run with -Hwnd."
}

function Wait-LatencyClockHwnd($timeoutSeconds = 8) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $m = Find-LatencyClockMatch
        if ($m) {
            # Settle after detection. The Chrome --kiosk transition (size,
            # fullscreen, DWM compositing) is still in flight when the title
            # first appears, and a WGC attach during that window busts the
            # frame-conversion path -- the bug that drove the kill+relaunch
            # retry. ~2s gives Chrome time to finish the transition before
            # the caller starts the probe.
            Start-Sleep -Seconds 2
            return $m
        }
    }
    return $null
}

function Get-LatencyClockHwndFromMatch($match) {
    # `windowstream list` format: "HANDLE       PROCESS              TITLE"
    $h = ($match -split '\s+', 4)[0]
    if (-not ($h -match '^\d+$')) {
        Fail "Could not parse HWND from list output line: '$match'"
    }
    return [int]$h
}

# Build the URL with ?cap=N param (matches 2026-05-11 baseline at 165).
$ClockHtml = Join-Path $PSScriptRoot 'latency-clock.html'
if (-not (Test-Path $ClockHtml)) {
    Fail "latency-clock.html not found at $ClockHtml"
}
$ClockUrl = 'file:///' + ($ClockHtml -replace '\\', '/')
if ($Cap -gt 0) { $ClockUrl += "?cap=$Cap" }

if ($Hwnd) {
    Ok "Using HWND override: $Hwnd"
    $TargetHwnd = $Hwnd
} else {
    # Reap stale latency-clock browsers from prior failed runs first --
    # the Edge / Chrome WGC bust state (project_chrome_kiosk_wgc_conversion_fail,
    # project_edge_kiosk_wgc_session_bust) persists in the existing process
    # group, so a fresh launch in the same session needs the old ones gone.
    $reaped = Stop-LatencyClockBrowsers
    if ($reaped) { Info "  Reaped $reaped stale browser process(es) hosting latency-clock" }

    $match = Find-LatencyClockMatch
    if (-not $match) {
        Start-LatencyClockBrowser $ClockUrl
        $match = Wait-LatencyClockHwnd
    }
    if (-not $match) {
        Fail @"
No window matching 'latency clock' in ``windowstream list`` output, even
after launching the browser. Possible causes:
  - Browser launched but didn't open the file URL (check the window).
  - The latency-clock.html <title> changed and no longer contains
    'latency clock'.

Pass -Hwnd <int> to override and target a different window.
"@
    }
    $TargetHwnd = Get-LatencyClockHwndFromMatch $match
    Ok "Source HWND: $TargetHwnd ('$match')"
}

# === Step 4: start serve, parse banner ========================================
Info "[4/8] Start serve and parse TCP port"

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$ServerStdoutLog = Join-Path $env:TEMP "windowstream-serve-$stamp.out.log"
$ServerStderrLog = Join-Path $env:TEMP "windowstream-serve-$stamp.err.log"

$ServerProcess = Start-Process -FilePath $CliExe `
    -ArgumentList 'serve' `
    -RedirectStandardOutput $ServerStdoutLog `
    -RedirectStandardError  $ServerStderrLog `
    -WindowStyle Hidden `
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

# === Step 5: frame-flow probe =================================================
Info "[5/8] Frame-flow probe ($ProbeDuration s, HMD off-head OK)"

function Invoke-FrameFlowProbe {
    & adb -s $DeviceId shell am force-stop $ViewerPkg *> $null
    & adb -s $DeviceId logcat -c *> $null

    & adb -s $DeviceId shell am start -n $DemoActivity `
        --es streamHost $HostIp `
        --ei streamPort $TcpPort `
        --ela selectedWindowHwnds $TargetHwnd *> $null

    Start-Sleep -Seconds $ProbeDuration

    $logcatDump = & adb -s $DeviceId logcat -d 2>$null
    $decCount = ($logcatDump | Select-String 'FRAMECOUNT.*stage=dec').Count

    $encCount = 0
    if (Test-Path $ServerStderrLog) {
        $encCount = (Get-Content $ServerStderrLog | Select-String 'stage=enc').Count
    }

    # No force-stop here on purpose. The probe-start force-stop above
    # handles the retry case cleanly; leaving the viewer ALIVE after a
    # successful probe lets the stream survive across the on-head gate
    # without a disconnect/reconnect cycle that re-triggers the WGC bust
    # on the same source HWND.
    return @{ Dec = $decCount; Enc = $encCount }
}

# Probe with retry. The Chrome --kiosk / Edge --kiosk WGC frame-conversion
# bug is intermittent and typically clears after kill + relaunch (per the
# 2026-05-11 baseline session). Only retry when the symptom matches
# dec=0 + enc=0 -- that's the WGC bust signature. Other failures (low
# frame rate, network blocked) won't benefit from a relaunch.
$result = $null
for ($attempt = 1; $attempt -le $ProbeMaxAttempts; $attempt++) {
    if ($attempt -gt 1) {
        Info "  attempt $attempt/$ProbeMaxAttempts after WGC bust: reaping browser + relaunching..."
        if (-not $Hwnd) {
            Stop-LatencyClockBrowsers | Out-Null
            Start-LatencyClockBrowser $ClockUrl
            $match = Wait-LatencyClockHwnd
            if (-not $match) {
                Fail "After WGC retry, no latency-clock window appeared in 8s."
            }
            $TargetHwnd = Get-LatencyClockHwndFromMatch $match
            Info "  new HWND: $TargetHwnd"
        } else {
            Info "  -Hwnd override in use; cannot relaunch source -- retrying probe in place"
        }
    }
    $result = Invoke-FrameFlowProbe
    Info "  probe attempt ${attempt}: dec=$($result.Dec), enc=$($result.Enc) (in ${ProbeDuration}s)"
    if ($result.Dec -ge $DecThreshold) { break }
    # Only the dec=0 + enc=0 case is worth retrying.
    if (-not ($result.Dec -eq 0 -and $result.Enc -eq 0)) { break }
}

if ($result.Dec -ge $DecThreshold) {
    Ok "Frames flowing healthy (dec=$($result.Dec) at or above threshold $DecThreshold)"
} elseif ($result.Dec -eq 0 -and $result.Enc -eq 0) {
    Fail @"
Server isn't producing frames after $ProbeMaxAttempts attempts. WGC capture
pump can't attach to the source window even after browser relaunch. Try:
  - A different source window (Windows Terminal with a spinner; a Unity
    Editor scene; Fork; etc.).
  - Re-run with -ProbeMaxAttempts 5 to give Chrome more retries.
  - Memory: project_chrome_kiosk_wgc_conversion_fail.md,
            project_edge_kiosk_wgc_session_bust.md,
            project_firefox_wgc_silent_fail.md,
            project_orphan_worker_wgc_lock.md
Server stderr: $ServerStderrLog (leave running for inspection;
PID $($ServerProcess.Id))
"@
} elseif ($result.Dec -eq 0 -and $result.Enc -gt 0) {
    Fail @"
Server is encoding ($($result.Enc) enc lines) but viewer received nothing.
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
Low frame rate (dec=$($result.Dec), threshold=$DecThreshold).
Could be source window unfocused / minimised, or -Cap $Cap is throttling
too aggressively. Bring the latency-clock window to foreground and re-run.
"@
}

# === Step 6: go-on-head gate =================================================
Info "[6/8] Go on-head"
Write-Host ""
Write-Host "  Put the HMD on, position the host monitor in your gaze," -ForegroundColor Yellow
Write-Host "  then press ENTER to record ${Duration}s." -ForegroundColor Yellow
Read-Host | Out-Null

# Refocus the kiosk browser. Read-Host left focus on the PS console; without
# this the user has to alt-tab back to the latency clock. Plain
# SetForegroundWindow works without the AttachThreadInput hack because the
# PS console IS the foreground process at this instant (user just pressed
# Enter), which is the one condition under which SetForegroundWindow can
# grant foreground to another HWND.
if (-not ([System.Management.Automation.PSTypeName]'WindowStream.Focus').Type) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace WindowStream {
    public static class Focus {
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
"@
}
[void][WindowStream.Focus]::SetForegroundWindow([IntPtr]$TargetHwnd)

# === Step 7: real record =====================================================
Info "[7/8] Recording ${Duration}s"

$OutputDir = $PSScriptRoot
$RecordingMp4   = Join-Path $OutputDir "feasibility-recording-$stamp.mp4"
$RecordingFrame = Join-Path $OutputDir "feasibility-recording-$stamp-frame.jpg"
$RemoteMp4 = '/sdcard/feasibility-recording.mp4'

# The viewer + server stream are already live from the successful probe.
# We deliberately do NOT relaunch the viewer here -- a disconnect/reconnect
# cycle re-attaches WGC to the same source HWND, which has historically
# tripped the bust state and produced a black recording. Clear logcat for
# clean post-record inspection and let any in-flight frames settle.
& adb -s $DeviceId logcat -c *> $null
Start-Sleep -Milliseconds 800

Info "  recording NOW -- position both clocks in your gaze"
& adb -s $DeviceId shell screenrecord --time-limit $Duration --bit-rate 20M $RemoteMp4

# Recording is locked in on /sdcard. Force-stop the viewer to give the
# on-head user a black-screen "done" signal while we pull and process.
& adb -s $DeviceId shell am force-stop $ViewerPkg *> $null

& adb -s $DeviceId pull $RemoteMp4 $RecordingMp4 *> $null
if (-not (Test-Path $RecordingMp4)) {
    Fail "adb pull failed; recording left at $RemoteMp4 on device $DeviceId for manual recovery."
}
& adb -s $DeviceId shell rm $RemoteMp4 *> $null

# Midpoint frame extraction (ffmpeg must be on PATH)
$ffmpegOnPath = Get-Command ffmpeg -ErrorAction SilentlyContinue
if ($ffmpegOnPath) {
    & ffmpeg -ss ([int]($Duration / 2)) -i $RecordingMp4 -frames:v 1 -y $RecordingFrame 2>$null
}

Ok "Recording: $RecordingMp4"
if (Test-Path $RecordingFrame) { Ok "Frame:     $RecordingFrame" }

# === Step 8: teardown ========================================================
Info "[8/8] Off-head"
Write-Host ""
Write-Host "  Recording done. Come off-head when you're ready." -ForegroundColor Yellow
$tearDown = Read-Host "  Tear down server + firewall rules? [y/N]"

if ($tearDown -match '^[Yy]') {
    Stop-Process -Id $ServerProcess.Id -Force -ErrorAction SilentlyContinue
    Get-NetFirewallRule -DisplayName 'WindowStream-Session-*' -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
    $reaped = Stop-LatencyClockBrowsers
    Ok "Server stopped, firewall rules removed, $reaped browser process(es) closed."
} else {
    Write-Host ""
    Write-Host "  Server PID: $($ServerProcess.Id) left running." -ForegroundColor Yellow
    Write-Host "  To stop later:  Stop-Process -Id $($ServerProcess.Id)" -ForegroundColor Yellow
    Write-Host "  Firewall cleanup (also handled by /wrap):" -ForegroundColor Yellow
    Write-Host "    Get-NetFirewallRule -DisplayName WindowStream-Session-* | Remove-NetFirewallRule" -ForegroundColor Yellow
}
