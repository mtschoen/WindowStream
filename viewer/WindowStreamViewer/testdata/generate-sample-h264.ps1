# testdata/generate-sample-h264.ps1
# Produces a deterministic 60-frame 320x240 H.264 Annex-B stream used by the loopback test.
param(
    [string]$OutputPath = "$PSScriptRoot/sample-h264.bin"
)
$ErrorActionPreference = "Stop"
& ffmpeg -y `
    -f lavfi -i "testsrc=duration=1:size=320x240:rate=60" `
    -c:v libx264 -preset veryfast -tune zerolatency `
    -g 60 -keyint_min 60 -x264-params "annexb=1:repeat-headers=1" `
    -profile:v baseline -pix_fmt yuv420p `
    -f h264 $OutputPath
if (-not (Test-Path $OutputPath)) {
    throw "ffmpeg failed to produce $OutputPath"
}
Write-Host "Wrote $OutputPath ($((Get-Item $OutputPath).Length) bytes)"
