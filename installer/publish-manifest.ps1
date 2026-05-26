# Produces a release manifest (latest.json) for the auto-updater by
# hashing the two Inno Setup outputs in installer\Output\. Publish the
# resulting JSON next to the setup .exe files at the URL referenced by
# Updater.ManifestUrl (currently set to a placeholder in Updater.cs).
#
# Usage (after both x64 + arm64 ISCC builds have produced their setups):
#   powershell -ExecutionPolicy Bypass -File installer\publish-manifest.ps1 `
#     -Version 0.1.3 `
#     -BaseUrl https://github.com/you/gwdetective/releases/download/v0.1.3 `
#     -Notes   "Fixes the Full-mode OOM and adds dark scrollbars."

param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $BaseUrl,
    [string] $Notes = ""
)

$ErrorActionPreference = 'Stop'

$outDir = Join-Path $PSScriptRoot 'Output'

function Get-Entry([string]$arch) {
    $exe = Join-Path $outDir "GWDetective-Setup-$arch.exe"
    if (-not (Test-Path $exe)) { throw "Missing $exe \u2014 build it first." }
    $sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash
    return [ordered]@{
        url    = "$($BaseUrl.TrimEnd('/'))/GWDetective-Setup-$arch.exe"
        sha256 = $sha
    }
}

$manifest = [ordered]@{
    version = $Version
    notes   = $Notes
    x64     = Get-Entry 'x64'
    arm64   = Get-Entry 'arm64'
}

$json = $manifest | ConvertTo-Json -Depth 5
$out  = Join-Path $outDir 'latest.json'
Set-Content -LiteralPath $out -Value $json -Encoding utf8
Write-Host "Wrote $out"
Write-Host $json
