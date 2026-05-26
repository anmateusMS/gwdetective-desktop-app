# Pushes the <Version> from GatewayTracer.Desktop.csproj into
# GWDetective.iss's MyAppVersion define, so AppVersion and the exe version
# stay aligned. Run before each ISCC build:
#   powershell -ExecutionPolicy Bypass -File installer\sync-version.ps1
#
# AppId in the .iss is intentionally NOT touched \u2014 Inno uses it to detect
# an existing per-user install and upgrade in place.

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$csproj      = Join-Path $projectRoot 'GatewayTracer.Desktop.csproj'
$iss         = Join-Path $PSScriptRoot 'GWDetective.iss'

[xml]$xml = Get-Content -LiteralPath $csproj
$version = $xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $csproj" }

$content = Get-Content -LiteralPath $iss -Raw
$updated = [System.Text.RegularExpressions.Regex]::Replace(
    $content,
    '(?m)^#define\s+MyAppVersion\s+".*"$',
    "#define MyAppVersion   `"$version`"")

if ($updated -eq $content) {
    Write-Host "MyAppVersion already at $version \u2014 no change."
} else {
    Set-Content -LiteralPath $iss -Value $updated -NoNewline
    Write-Host "Set MyAppVersion to $version in GWDetective.iss"
}
