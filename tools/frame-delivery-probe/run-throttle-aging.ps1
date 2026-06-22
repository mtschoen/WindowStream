# Follow-up: is Chromium background throttling TIME-PROGRESSIVE? Backgrounds
# Edge (covered + unfocused) for 30s before capturing, with vs without the
# anti-throttle flags. If aged-no-flags frames collapse vs aged-with-flags,
# the time hypothesis holds.
#
# Usage:
#   pwsh tools/frame-delivery-probe/run-throttle-aging.ps1
param(
    [int]$AgeSeconds = 30,
    [int]$ProbeSeconds = 6,
    [string]$OutputPath = "$PSScriptRoot\results-aging.tsv"
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$probeProject = "$PSScriptRoot\FrameDeliveryProbe.csproj"

if (Test-Path $OutputPath) { Remove-Item $OutputPath }
$clockUri = "file:///" + ($repoRoot + "\tools\latency-clock.html").Replace('\','/')

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class AgingNativeMethods {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@

function Probe([string]$Title,[string]$Label) {
  & dotnet run --project $probeProject -- --title $Title --action none --label $Label --seconds $ProbeSeconds --output $OutputPath
  Write-Host "  probed: $Label"
}

function RunCase([string]$Label,[string[]]$ExtraFlags) {
  $beforeEdge = (Get-Process msedge -ErrorAction SilentlyContinue).Id
  $beforeNote = (Get-Process notepad -ErrorAction SilentlyContinue).Id
  try {
    $edgeArguments = @("--app=$clockUri","--new-window","--no-first-run","--disable-extensions",
              "--user-data-dir=$env:TEMP\ws-probe-edge-age") + $ExtraFlags
    Start-Process msedge.exe -ArgumentList $edgeArguments | Out-Null
    Start-Sleep -Seconds 2
    Start-Process notepad.exe | Out-Null
    Start-Sleep -Seconds 2
    $np = Get-Process notepad | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($np) { [AgingNativeMethods]::ShowWindow($np.MainWindowHandle,3) | Out-Null; [AgingNativeMethods]::SetForegroundWindow($np.MainWindowHandle) | Out-Null }
    Write-Host "$Label : backgrounding edge ${AgeSeconds}s before capture..."
    Start-Sleep -Seconds $AgeSeconds
    Probe "latency clock" $Label
  }
  finally {
    foreach ($id in ((Get-Process msedge -ErrorAction SilentlyContinue).Id | Where-Object { $_ -notin $beforeEdge })) { taskkill /PID $id /T /F *> $null }
    foreach ($id in ((Get-Process notepad -ErrorAction SilentlyContinue).Id | Where-Object { $_ -notin $beforeNote })) { taskkill /PID $id /T /F *> $null }
    Remove-Item "$env:TEMP\ws-probe-edge-age" -Recurse -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
  }
}

RunCase "aged${AgeSeconds}s-noflags"   @()
RunCase "aged${AgeSeconds}s-withflags" @("--disable-background-timer-throttling","--disable-backgrounding-occluded-windows","--disable-renderer-backgrounding","--disable-features=CalculateNativeWinOcclusion")

Write-Host "`n===== AGING RESULTS ====="
Get-Content $OutputPath
