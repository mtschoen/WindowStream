@echo off
setlocal

REM === Vintage (v1, swimmy-era 83384b6) variant of record-latency-clock.bat ===
REM Differs from main: no `--ela selectedWindowHwnds` extra (v1 server already
REM chose the window via its own --hwnd). Otherwise identical flow.

set DEV=192.168.50.111:40393
set HOST_IP=192.168.50.75
if "%TCP_PORT%"=="" set TCP_PORT=49702
set DURATION=15
set REMOTE=/sdcard/vintage-recording.mp4

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
