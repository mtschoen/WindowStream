@echo off
rem Convenience wrapper for record-latency-clock.ps1. Forwards all args.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0record-latency-clock.ps1" %*
