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
No window matching 'latency clock' in ``windowstream list`` output.
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
