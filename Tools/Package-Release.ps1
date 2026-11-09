param(
    # Release label used in the ZIP name; defaults to Constants.ModVersion from the source.
    [string]$Version = "",
    [string]$GameTag = "s1-0.4.7f12",
    # Replace an existing ZIP with the same name (off by default so releases are never silently overwritten).
    [switch]$Force
)

# Assembles the installable ZIP from a completed Tools\Build-And-Install.ps1 -BuildOnly run:
#   Mods\ScheduleIArcade.dll
#   Mods\ScheduleIArcade\Assets\...                        (dist\Mods\ScheduleIArcade)
#   Mods\ScheduleIArcade\Doom\ScheduleIArcade.Doom.dll     (bundled engine, GPL-2.0)
#   Mods\ScheduleIArcade\Doom\freedoom1.wad, freedoom2.wad (Freedoom 0.13.0, BSD-style)
#   Mods\ScheduleIArcade\Doom\licenses\, NOTICE.md, ScheduleIArcade.Doom-source.zip

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Release = Join-Path $ProjectRoot "Build\Release"

if (-not $Version) {
    $constants = Get-Content (Join-Path $ProjectRoot "src\ScheduleIArcade\Utils\Constants.cs") -Raw
    if ($constants -notmatch 'ModVersion\s*=\s*"([^"]+)"') { throw "Could not read ModVersion from Constants.cs" }
    $Version = $Matches[1]
}

$modDll = Join-Path $Release "ScheduleIArcade.dll"
$doom = Join-Path $Release "Doom"
foreach ($required in @($modDll, (Join-Path $doom "ScheduleIArcade.Doom.dll"), (Join-Path $doom "freedoom1.wad"), (Join-Path $doom "freedoom2.wad"), (Join-Path $doom "ScheduleIArcade.Doom-source.zip"), (Join-Path $doom "licenses\ManagedDoom-and-ScheduleIArcade.Doom-GPL-2.0.txt"), (Join-Path $doom "licenses\Freedoom-COPYING.txt"))) {
    if (-not (Test-Path $required)) { throw "Missing build output: $required (run Tools\Build-And-Install.ps1 -BuildOnly first)" }
}

$packageDir = Join-Path $ProjectRoot "Build\Package"
$zip = Join-Path $packageDir ("ScheduleIArcade-" + $Version + "-" + $GameTag + ".zip")
if ((Test-Path $zip) -and -not $Force) { throw "Refusing to overwrite existing $zip (use -Force)." }

$stage = Join-Path $packageDir ("stage-" + $Version)
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item (Join-Path $stage "Mods") -ItemType Directory -Force | Out-Null
Copy-Item $modDll (Join-Path $stage "Mods\ScheduleIArcade.dll")
Copy-Item (Join-Path $ProjectRoot "dist\Mods\ScheduleIArcade") (Join-Path $stage "Mods") -Recurse
$doomDest = Join-Path $stage "Mods\ScheduleIArcade\Doom"
New-Item $doomDest -ItemType Directory -Force | Out-Null
Get-ChildItem $doom -Exclude "*.pdb" | Copy-Item -Destination $doomDest -Recurse

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage "Mods") -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Write-Host "Packaged $zip"
Write-Host "SHA256 $hash"
