# WGC frame-delivery probe matrix. Drives a static (Notepad) and an animated
# (Edge --app latency clock) window through capture states and records how many
# WGC frames arrive. PID-snapshot cleanup kills ONLY windows we launch.
#
# Usage:
#   pwsh tools/frame-delivery-probe/run-probe-matrix.ps1
param(
    [int]$Seconds = 6,
    [string]$OutputPath = "$PSScriptRoot\results.tsv"
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$probeProject = "$PSScriptRoot\FrameDeliveryProbe.csproj"

if (Test-Path $OutputPath) { Remove-Item $OutputPath }
$clockUri = "file:///" + ($repoRoot + "\tools\latency-clock.html").Replace('\','/')

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ProbeNativeMethods {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@

function Probe([string]$Title,[string]$Action,[string]$Label) {
  & dotnet run --project $probeProject -- --title $Title --action $Action --label $Label --seconds $Seconds --output $OutputPath
  Write-Host "  probed: $Label"
}

$beforeEdge = (Get-Process msedge -ErrorAction SilentlyContinue).Id
$beforeNote = (Get-Process notepad -ErrorAction SilentlyContinue).Id

try {
  Write-Host "launching edge --app latency clock (no anti-throttle flags)..."
  Start-Process msedge.exe -ArgumentList @(
    "--app=$clockUri","--new-window","--no-first-run","--disable-extensions",
    "--user-data-dir=$env:TEMP\ws-probe-edge") | Out-Null
  Start-Sleep -Seconds 3

  Probe "latency clock" "none" "animated-foreground"

  Write-Host "launching notepad (will cover edge)..."
  Start-Process notepad.exe | Out-Null
  Start-Sleep -Seconds 2
  $np = Get-Process notepad | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($np) { [ProbeNativeMethods]::ShowWindow($np.MainWindowHandle, 3) | Out-Null; [ProbeNativeMethods]::SetForegroundWindow($np.MainWindowHandle) | Out-Null }
  Start-Sleep -Seconds 1

  Probe "latency clock" "none"     "animated-occluded"
  Probe "Notepad"       "none"     "static-normal"
  Probe "Notepad"       "offscreen" "static-offscreen"
  Probe "latency clock" "minimize" "animated-minimized"
  Probe "Notepad"       "minimize" "static-minimized"
}
finally {
  $afterEdge = (Get-Process msedge -ErrorAction SilentlyContinue).Id
  $afterNote = (Get-Process notepad -ErrorAction SilentlyContinue).Id
  foreach ($id in ($afterEdge | Where-Object { $_ -notin $beforeEdge })) { taskkill /PID $id /T /F *> $null }
  foreach ($id in ($afterNote | Where-Object { $_ -notin $beforeNote })) { taskkill /PID $id /T /F *> $null }
  Remove-Item "$env:TEMP\ws-probe-edge" -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`n===== RESULTS ====="
Get-Content $OutputPath
