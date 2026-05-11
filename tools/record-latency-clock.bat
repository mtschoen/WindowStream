@echo off
setlocal enabledelayedexpansion

REM === record-latency-clock.bat ==============================================
REM Records a 15s GXR screenrecord of the WindowStream viewer showing a
REM tools/latency-clock.html source, for HMD-camera input->present
REM measurement. Outputs land next to this script under tools/.
REM
REM Stable home (moved from .claude/scripts/ on 2026-05-11 after the wrap
REM hygiene there kept deleting it). See git history for prior location.
REM
REM Tunables:
REM   HOST_IP     PC's LAN IP the viewer connects to (hardcoded; update on
REM               PC IP change). Server picks an ephemeral TCP port each
REM               launch -- pass it via env var TCP_PORT.
REM   GXR_SERIAL  HMD device serial. The full mDNS-form `-s` identifier is
REM               auto-discovered from `adb devices` so it survives
REM               re-pairings.
REM ===========================================================================

set HOST_IP=192.168.50.75
set GXR_SERIAL=R3GYB04E2WB
if "%TCP_PORT%"=="" set TCP_PORT=61613
set DURATION=15
set REMOTE=/sdcard/feasibility-recording.mp4

REM Auto-discover the GXR's full adb identifier (mDNS string or ip:port).
set DEV=
for /f "tokens=1" %%a in ('adb devices ^| findstr "%GXR_SERIAL%"') do set DEV=%%a
if "%DEV%"=="" (
    echo ERROR: no adb device matching serial %GXR_SERIAL%
    echo Run: adb devices  -- and verify the HMD is paired over Wi-Fi.
    exit /b 1
)
echo Using adb device: %DEV%

REM Auto-discover the HWND of the latency-clock browser window via
REM `windowstream list`. Override by passing an HWND as the first arg.
set EXE=%~dp0..\src\WindowStream.Cli\bin\Release\net8.0-windows10.0.19041.0\windowstream.exe
set HWND=
if not "%~1"=="" (
    set HWND=%~1
) else (
    powershell -NoProfile -Command "$line = (& '%EXE%' list | Select-String -Pattern 'latency clock' | Select-Object -First 1); if ($line) { ($line.Line.Trim() -split '\s+')[0] }" 2>nul > "%TEMP%\record-latency-hwnd.txt"
    set /p HWND=<"%TEMP%\record-latency-hwnd.txt"
    del "%TEMP%\record-latency-hwnd.txt" 2>nul
)
if "%HWND%"=="" (
    echo ERROR: could not find a window matching 'latency clock' via windowstream list
    echo Open tools\latency-clock.html in Edge/Chrome fullscreen and try again,
    echo or pass an HWND explicitly: record-latency-clock.bat 12345678
    exit /b 1
)

set OUTDIR=%~dp0
for /f "delims=" %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set STAMP=%%i
set OUTPUT=%OUTDIR%feasibility-recording-%STAMP%.mp4
set FRAME=%OUTDIR%feasibility-recording-%STAMP%-frame.jpg

echo [1/5] Stopping prior viewer + clearing logcat
adb -s %DEV% shell am force-stop com.mtschoen.windowstream.viewer
adb -s %DEV% logcat -c

echo [2/5] Firing DemoActivity (hwnd=%HWND% port=%TCP_PORT%)
adb -s %DEV% shell am start -n com.mtschoen.windowstream.viewer/.demo.DemoActivity --es streamHost %HOST_IP% --ei streamPort %TCP_PORT% --ela selectedWindowHwnds %HWND%

echo [3/5] Waiting 3s for handshake
timeout /t 3 /nobreak > nul

echo [4/5] Recording %DURATION%s -- position both clocks in your gaze NOW
adb -s %DEV% shell screenrecord --time-limit %DURATION% --bit-rate 20M %REMOTE%

echo [5/5] Pulling and cleaning up
adb -s %DEV% pull %REMOTE% "%OUTPUT%"
if errorlevel 1 (
    echo ERROR: pull failed -- leaving remote file at %REMOTE% for manual recovery
    exit /b 1
)
adb -s %DEV% shell rm %REMOTE%

if exist "%OUTPUT%" (
    echo Extracting midpoint frame for quick inspection
    ffmpeg -ss 8 -i "%OUTPUT%" -frames:v 1 -y "%FRAME%" 2>nul
)

echo.
echo Done.
echo   Video: %OUTPUT%
echo   Frame: %FRAME%
