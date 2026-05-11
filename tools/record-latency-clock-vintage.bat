@echo off
setlocal enabledelayedexpansion

REM === record-latency-clock-vintage.bat ======================================
REM Vintage (v1, swimmy-era 83384b6) variant of record-latency-clock.bat.
REM Differs in one place: v1 DemoActivity has no `--ela selectedWindowHwnds`
REM extra because the v1 server already chose its window via `--hwnd`.
REM
REM Stable home (moved from .claude/scripts/ on 2026-05-11). See git history
REM for prior location.
REM ===========================================================================

set HOST_IP=192.168.50.75
set GXR_SERIAL=R3GYB04E2WB
if "%TCP_PORT%"=="" set TCP_PORT=49702
set DURATION=15
set REMOTE=/sdcard/vintage-recording.mp4

set DEV=
for /f "tokens=1" %%a in ('adb devices ^| findstr "%GXR_SERIAL%"') do set DEV=%%a
if "%DEV%"=="" (
    echo ERROR: no adb device matching serial %GXR_SERIAL%
    echo Run: adb devices  -- and verify the HMD is paired over Wi-Fi.
    exit /b 1
)
echo Using adb device: %DEV%

set OUTDIR=%~dp0
for /f "delims=" %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set STAMP=%%i
set OUTPUT=%OUTDIR%vintage-recording-%STAMP%.mp4

echo [1/5] Stopping prior viewer + clearing logcat
adb -s %DEV% shell am force-stop com.mtschoen.windowstream.viewer
adb -s %DEV% logcat -c

echo [2/5] Firing v1 DemoActivity (host=%HOST_IP% port=%TCP_PORT%)
adb -s %DEV% shell am start -n com.mtschoen.windowstream.viewer/.demo.DemoActivity --es streamHost %HOST_IP% --ei streamPort %TCP_PORT%

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

echo.
echo Done.
echo   Video: %OUTPUT%
