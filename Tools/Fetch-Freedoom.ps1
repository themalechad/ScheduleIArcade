param(
    # Where the verified Freedoom files are cached (inside this repository, git-ignored).
    [string]$CacheDir = ""
)

# Downloads Freedoom 0.13.0 from the official Freedoom GitHub release, verifies the archive against
# the SHA-256 published in the project's signed freedoom-0.13.0-CHECKSUM file, and extracts only
# the files the arcade ships: freedoom1.wad, freedoom2.wad and the licence/credits.
#
# Freedoom is distributed under a BSD-style licence (COPYING.txt) and contains no id Software data.

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $CacheDir) { $CacheDir = Join-Path $ProjectRoot "Build\third_party\freedoom-0.13.0" }

$Url = "https://github.com/freedoom/freedoom/releases/download/v0.13.0/freedoom-0.13.0.zip"
$ExpectedSha256 = "3F9B264F3E3CE503B4FB7F6BDCB1F419D93C7B546F4DF3E874DD878DB9688F59"
$Wanted = @("freedoom1.wad", "freedoom2.wad", "COPYING.txt", "CREDITS.txt", "CREDITS-MUSIC.txt")

$haveAll = $true
foreach ($name in $Wanted) { if (-not (Test-Path (Join-Path $CacheDir $name))) { $haveAll = $false } }
if ($haveAll) {
    Write-Host "Freedoom 0.13.0 already present: $CacheDir"
    return
}

New-Item $CacheDir -ItemType Directory -Force | Out-Null
$zip = Join-Path (Split-Path -Parent $CacheDir) "freedoom-0.13.0.zip"
if (-not (Test-Path $zip)) {
    Write-Host "Downloading $Url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing
}

$actual = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($actual -ne $ExpectedSha256) {
    Remove-Item $zip -Force
    throw "freedoom-0.13.0.zip SHA-256 mismatch: expected $ExpectedSha256, got $actual"
}
Write-Host "Verified freedoom-0.13.0.zip SHA-256 $actual"

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($entry in $archive.Entries) {
        if ($Wanted -contains $entry.Name) {
            $target = Join-Path $CacheDir $entry.Name
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    }
} finally {
    $archive.Dispose()
}

foreach ($name in $Wanted) {
    if (-not (Test-Path (Join-Path $CacheDir $name))) { throw "Freedoom archive did not contain $name" }
}
Write-Host "Freedoom 0.13.0 extracted to $CacheDir"
