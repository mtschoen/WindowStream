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
