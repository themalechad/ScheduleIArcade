# Assembles the PRIVATE 0.5.0 ZIP: the public package plus Mods\ScheduleIArcade\LocalAssets.
# This ZIP must stay on this PC. Do not upload it to GitHub, Vortex, or any other host.
#
# Prerequisite: a public Build\Package\ScheduleIArcade-0.5.0-s1-0.4.7f12.zip (or run
# Tools\Package-Release.ps1 first) and a Build\LocalAssets pack
# (Tools\Build-LocalAssetPack.ps1).
#
#   powershell -ExecutionPolicy Bypass -File Tools\Package-Private.ps1

param(
    [string]$Version = "",
    [string]$GameTag = "s1-0.4.7f12",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

if (-not $Version) {
    $constants = Get-Content (Join-Path $ProjectRoot "src\ScheduleIArcade\Utils\Constants.cs") -Raw
    if ($constants -notmatch 'ModVersion\s*=\s*"([^"]+)"') { throw "Could not read ModVersion from Constants.cs" }
    $Version = $Matches[1]
}

$publicZip = Join-Path $ProjectRoot ("Build\Package\ScheduleIArcade-" + $Version + "-" + $GameTag + ".zip")
$pack = Join-Path $ProjectRoot "Build\LocalAssets"
if (-not (Test-Path -LiteralPath $publicZip)) { throw "Public ZIP not found: $publicZip (run Tools\\Package-Release.ps1 first)" }
if (-not (Test-Path -LiteralPath (Join-Path $pack "manifest.json"))) { throw "Local asset pack not found: $pack (run Tools\\Build-LocalAssetPack.ps1 first)" }

$privateDir = Join-Path $ProjectRoot "Build\Private"
New-Item $privateDir -ItemType Directory -Force | Out-Null
$zip = Join-Path $privateDir ("ScheduleIArcade-" + $Version + "-PRIVATE-with-game-assets.zip")
if ((Test-Path -LiteralPath $zip) -and -not $Force) { throw "Refusing to overwrite existing $zip (use -Force)." }

$stage = Join-Path $privateDir ("stage-" + $Version + "-private")
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item $stage -ItemType Directory -Force | Out-Null
Expand-Archive -LiteralPath $publicZip -DestinationPath $stage -Force

$dest = Join-Path $stage "Mods\ScheduleIArcade\LocalAssets"
if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
Copy-Item -LiteralPath $pack -Destination $dest -Recurse

if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage "Mods") -DestinationPath $zip
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
$size = (Get-Item -LiteralPath $zip).Length
Write-Host "PRIVATE package (do not upload): $zip"
Write-Host ("Size {0:N2} MB" -f ($size / 1MB))
Write-Host "SHA256 $hash"
