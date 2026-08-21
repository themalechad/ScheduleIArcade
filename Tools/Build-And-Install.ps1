param(
    [string]$GameDir = "",
    [switch]$BuildOnly
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$SourceRoot = Join-Path $ProjectRoot "src\ScheduleIArcade"
$OutDir = Join-Path $ProjectRoot "Build\Release"
$OutDll = Join-Path $OutDir "ScheduleIArcade.dll"
$OutPdb = Join-Path $OutDir "ScheduleIArcade.pdb"
$Rsp = Join-Path $OutDir "compiler.rsp"

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Resolve-GameDir([string]$Requested) {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($Requested) { $candidates.Add($Requested) }
    if ($env:SCHEDULE_I_DIR) { $candidates.Add($env:SCHEDULE_I_DIR) }
    $candidates.Add("C:\Program Files (x86)\Steam\steamapps\common\Schedule I")
    $candidates.Add("C:\Program Files\Steam\steamapps\common\Schedule I")

    foreach ($candidate in $candidates) {
        if (-not $candidate) { continue }
        $full = [System.IO.Path]::GetFullPath($candidate)
        if ((Test-Path (Join-Path $full "Schedule I.exe")) -or
            (Test-Path (Join-Path $full "Schedule I_Data"))) {
            return $full
        }
    }

    $picked = Read-Host "Enter your Schedule I game folder (the folder containing Schedule I.exe)"
    if (-not $picked) { throw "Schedule I folder was not supplied." }
    $picked = [System.IO.Path]::GetFullPath($picked.Trim('"'))
    if (-not (Test-Path $picked)) { throw "Schedule I folder does not exist: $picked" }
    return $picked
}

function Resolve-DotNet {
    $cmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $known = @(
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "${env:ProgramFiles(x86)}\dotnet\dotnet.exe"
    )
    foreach ($path in $known) {
        if ($path -and (Test-Path $path)) { return $path }
    }
    throw "dotnet.exe was not found. Install the .NET 6 SDK or newer SDK with the .NET 6 targeting pack."
}

function Resolve-Csc([string]$DotNetExe) {
    $dotnetRoot = Split-Path -Parent $DotNetExe
    $sdkRoot = Join-Path $dotnetRoot "sdk"
    if (-not (Test-Path $sdkRoot)) { throw "Could not find dotnet SDK folder: $sdkRoot" }

    $candidates = Get-ChildItem $sdkRoot -Directory |
        Sort-Object { [version]($_.Name -replace '-.*$','') } -Descending |
        ForEach-Object { Join-Path $_.FullName "Roslyn\bincore\csc.dll" } |
        Where-Object { Test-Path $_ }

    $csc = $candidates | Select-Object -First 1
    if (-not $csc) { throw "Roslyn csc.dll was not found under $sdkRoot" }
    return $csc
}

function Resolve-Net6ReferencePack([string]$DotNetExe) {
    $dotnetRoot = Split-Path -Parent $DotNetExe
    $packRoot = Join-Path $dotnetRoot "packs\Microsoft.NETCore.App.Ref"
    if (-not (Test-Path $packRoot)) {
        throw ".NET reference packs were not found under $packRoot"
    }

    $versions = Get-ChildItem $packRoot -Directory |
        Where-Object { $_.Name -like '6.*' } |
        Sort-Object { [version]($_.Name -replace '-.*$','') } -Descending

    foreach ($version in $versions) {
        $ref = Join-Path $version.FullName "ref\net6.0"
        if (Test-Path $ref) { return $ref }
    }

    throw "The .NET 6 targeting pack is missing. Install the .NET 6 SDK, then rerun Tools\\Build-And-Install.ps1."
}


function Add-Reference([System.Collections.Generic.Dictionary[string,string]]$Map, [string]$Path) {
    if (-not $Path -or -not (Test-Path $Path)) { return }
    $name = [System.IO.Path]::GetFileName($Path)
    if (-not $Map.ContainsKey($name)) { $Map[$name] = (Resolve-Path $Path).Path }
}

function Resolve-Blender {
    $cmd = Get-Command blender.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $cmd = Get-Command blender -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $roots = @()
    if ($env:ProgramFiles) { $roots += (Join-Path $env:ProgramFiles "Blender Foundation") }
    if (${env:ProgramFiles(x86)}) { $roots += (Join-Path ${env:ProgramFiles(x86)} "Blender Foundation") }
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $found = Get-ChildItem -Path $root -Filter blender.exe -Recurse -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
        if ($found) { return $found }
    }
    return $null
}

function Export-BlenderArcadeIfPresent {
    $blend = Join-Path $ProjectRoot "BlenderEditable\ScheduleIArcade_Editable.blend"
    if (-not (Test-Path $blend)) { return }

    $blender = Resolve-Blender
    if (-not $blender) {
        throw "BlenderEditable\ScheduleIArcade_Editable.blend exists, but Blender.exe could not be found. Open/save the .blend with Blender installed or remove/rename the BlenderEditable file to use the packaged fallback layout."
    }

    $exporter = Join-Path $ProjectRoot "BlenderTools\export_runtime_bin.py"
    if (-not (Test-Path $exporter)) { throw "Missing Blender runtime exporter: $exporter" }

    $rawDir = Join-Path $ProjectRoot "dist\Mods\ScheduleIArcade\Assets\RawScene"
    New-Item $rawDir -ItemType Directory -Force | Out-Null
    $outBin = Join-Path $rawDir "arcade_runtime_meshes.bin"

    Write-Step "Exporting Blender-authored arcade layout"
    Write-Host "Blender: $blender" -ForegroundColor DarkGray
    Write-Host "Scene:   $blend" -ForegroundColor DarkGray
    & $blender --background $blend --python $exporter -- $ProjectRoot $outBin
    if ($LASTEXITCODE -ne 0) { throw "Blender arcade export failed with exit code $LASTEXITCODE" }
    if (-not (Test-Path $outBin)) { throw "Blender returned success but arcade_runtime_meshes.bin was not created." }
    Write-Host "Blender layout exported for this build: $outBin" -ForegroundColor Green
}

Write-Step "Resolving Schedule I installation"
$GameDir = Resolve-GameDir $GameDir
Write-Host "Game: $GameDir"

Write-Step "Checking MelonLoader 0.7.3 installation"
$melonRoot = Join-Path $GameDir "MelonLoader"
if (-not (Test-Path $melonRoot)) { throw "MelonLoader folder not found: $melonRoot" }

$melonDll = Get-ChildItem $melonRoot -Recurse -Filter "MelonLoader.dll" -File -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $melonDll) { throw "MelonLoader.dll was not found under $melonRoot" }
Write-Host "MelonLoader: $($melonDll.FullName)"

Write-Step "Resolving S1API"
$s1apiCandidates = @(
    (Join-Path $GameDir "Mods\S1API.Il2Cpp.MelonLoader.dll"),
    (Join-Path $GameDir "Mods\S1API.dll"),
    (Join-Path $GameDir "Mods\S1API.Forked.dll")
)
$s1apiDll = $s1apiCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $s1apiDll) {
    $s1apiDll = Get-ChildItem (Join-Path $GameDir "Mods") -Filter "S1API*.dll" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike '*Mono*' } |
        Select-Object -ExpandProperty FullName -First 1
}
if (-not $s1apiDll) { throw "S1API IL2CPP DLL was not found in Schedule I\Mods. Install S1API first." }
Write-Host "S1API: $s1apiDll"

Write-Step "Resolving Roslyn compiler and .NET 6 reference pack"
$dotnet = Resolve-DotNet
$csc = Resolve-Csc $dotnet
$net6Ref = Resolve-Net6ReferencePack $dotnet
Write-Host "dotnet: $dotnet"
Write-Host "csc:    $csc"
Write-Host "net6:   $net6Ref"

Write-Step "Collecting Schedule I IL2CPP support references"
$refs = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::OrdinalIgnoreCase)

Get-ChildItem $net6Ref -Filter "*.dll" -File | ForEach-Object { Add-Reference $refs $_.FullName }
Add-Reference $refs $melonDll.FullName
Add-Reference $refs $s1apiDll

# MelonLoader 0.7.3 dependencies required by its public types.
Get-ChildItem $melonRoot -Recurse -Filter "*.dll" -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -match '^(Il2CppInterop\.|0Harmony|MonoMod\.|MelonLoader\.Il2Cpp|Unity\.IL2CPP)' -or
        $_.Name -eq 'Il2CppInterop.Runtime.dll'
    } |
    ForEach-Object { Add-Reference $refs $_.FullName }

# Generated game/Unity interop assemblies. Exclude BCL facade assemblies so the .NET 6 ref pack wins.
$il2CppDirs = @(
    (Join-Path $melonRoot "Il2CppAssemblies"),
    (Join-Path $melonRoot "Dependencies\Il2CppAssemblyGenerator\Il2CppAssemblies")
) | Where-Object { Test-Path $_ }

if ($il2CppDirs.Count -eq 0) {
    throw "MelonLoader generated IL2CPP assemblies were not found. Start Schedule I once with MelonLoader 0.7.3, close the game, then build again."
}

foreach ($dir in $il2CppDirs) {
    Get-ChildItem $dir -Filter "*.dll" -File | Where-Object {
        $_.Name -notmatch '^(System\.|System\.dll$|mscorlib\.dll$|netstandard\.dll$|Microsoft\.)'
    } | ForEach-Object { Add-Reference $refs $_.FullName }
}

# Some installs place generated assemblies under UserData/Il2CppAssemblies.
$userDataIl2Cpp = Join-Path $GameDir "UserData\Il2CppAssemblies"
if (Test-Path $userDataIl2Cpp) {
    Get-ChildItem $userDataIl2Cpp -Filter "*.dll" -File | Where-Object {
        $_.Name -notmatch '^(System\.|System\.dll$|mscorlib\.dll$|netstandard\.dll$|Microsoft\.)'
    } | ForEach-Object { Add-Reference $refs $_.FullName }
}

Write-Host "References: $($refs.Count)"

Export-BlenderArcadeIfPresent

Write-Step "Compiling ScheduleIArcade.dll"
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item $OutDir -ItemType Directory -Force | Out-Null

$sourceFiles = Get-ChildItem $SourceRoot -Recurse -Filter "*.cs" -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    Sort-Object FullName
if ($sourceFiles.Count -eq 0) { throw "No C# source files found under $SourceRoot" }

$rspLines = New-Object System.Collections.Generic.List[string]
$rspLines.Add('/noconfig')
$rspLines.Add('/nostdlib+')
$rspLines.Add('/target:library')
$rspLines.Add('/langversion:latest')
$rspLines.Add('/nullable:enable')
$rspLines.Add('/optimize+')
$rspLines.Add('/deterministic+')
$rspLines.Add('/debug:portable')
$rspLines.Add('/warn:4')
$rspLines.Add('/nowarn:1701,1702')
$rspLines.Add('/out:"' + $OutDll + '"')
$rspLines.Add('/pdb:"' + $OutPdb + '"')

foreach ($ref in ($refs.Values | Sort-Object)) {
    $rspLines.Add('/reference:"' + $ref + '"')
}
foreach ($src in $sourceFiles) {
    $rspLines.Add('"' + $src.FullName + '"')
}

[System.IO.File]::WriteAllLines($Rsp, $rspLines, [System.Text.UTF8Encoding]::new($false))
& $dotnet $csc "@$Rsp"
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $OutDll)) { throw "Compiler returned success but ScheduleIArcade.dll was not created." }

Write-Host "Built: $OutDll" -ForegroundColor Green

if (-not $BuildOnly) {
    Write-Step "Installing test build into Schedule I"
    $modsDir = Join-Path $GameDir "Mods"
    $modData = Join-Path $modsDir "ScheduleIArcade"
    $assets = Join-Path $modData "Assets"
    New-Item $modsDir -ItemType Directory -Force | Out-Null
    New-Item $assets -ItemType Directory -Force | Out-Null

    # M2.23 removed the experimental ATM/Cuke asset pipeline completely. Clean any files left
    # behind by older test builds so they cannot be picked up accidentally.
    $legacyRippedNative = Join-Path $assets "RippedNative"
    if (Test-Path $legacyRippedNative) { Remove-Item $legacyRippedNative -Recurse -Force }

    Copy-Item $OutDll (Join-Path $modsDir "ScheduleIArcade.dll") -Force
    if (Test-Path $OutPdb) { Copy-Item $OutPdb (Join-Path $modsDir "ScheduleIArcade.pdb") -Force }

    # Runtime-converted copy of the user's supplied FBX + real texture maps. Unity player builds
    # cannot import FBX directly, so RawArcadeSceneService reads this preconverted mesh package.
    $rawSource = Join-Path $ProjectRoot "dist\Mods\ScheduleIArcade\Assets\RawScene"
    $rawDest = Join-Path $assets "RawScene"
    if (Test-Path $rawSource) {
        if (Test-Path $rawDest) { Remove-Item $rawDest -Recurse -Force }
        Copy-Item $rawSource $rawDest -Recurse -Force
        Write-Host "Installed real arcade FBX runtime geometry/textures: $rawDest" -ForegroundColor Green
    } else {
        Write-Host "Raw arcade scene package is missing from this build; procedural fallback will be used." -ForegroundColor Yellow
    }

    # User-supplied arcade ambience. This is loaded directly by Unity at runtime and does not
    # require a Unity Editor import or AssetBundle rebuild.
    $audioSource = Join-Path $ProjectRoot "dist\Mods\ScheduleIArcade\Assets\Audio\arcade_ambience.wav"
    $audioDest = Join-Path $assets "Audio"
    if (Test-Path $audioSource) {
        New-Item $audioDest -ItemType Directory -Force | Out-Null
        Copy-Item $audioSource (Join-Path $audioDest "arcade_ambience.wav") -Force
        Write-Host "Installed arcade ambience: $(Join-Path $audioDest 'arcade_ambience.wav')" -ForegroundColor Green
    } else {
        Write-Host "Arcade ambience WAV is missing from this package; the arcade will run silently." -ForegroundColor Yellow
    }

    # Visual art extracted from the user-supplied PacMan v1.1.1 Unity data files. The source game
    # remains a separate Mono Unity executable, so Schedule I's IL2CPP mod uses these compatible
    # PNG assets inside the in-table renderer rather than attempting to load its managed DLL.
    $pacManSource = Join-Path $ProjectRoot "dist\Mods\ScheduleIArcade\Assets\PacMan"
    $pacManDest = Join-Path $assets "PacMan"
    if (Test-Path $pacManSource) {
        if (Test-Path $pacManDest) { Remove-Item $pacManDest -Recurse -Force }
        Copy-Item $pacManSource $pacManDest -Recurse -Force
        Write-Host "Installed Pac-Man source visual assets: $pacManDest" -ForegroundColor Green
    }

    # Optional real AssetBundle. The raw FBX runtime package above is now the normal test path,
    # so the test does not require Unity Editor or a prebuilt bundle.
    $bundleCandidates = @(
        (Join-Path $ProjectRoot "dist\Mods\ScheduleIArcade\Assets\scheduleiarcade"),
        (Join-Path $ProjectRoot "UnityProject\AssetBundles\Windows\scheduleiarcade"),
        (Join-Path $ProjectRoot "scheduleiarcade")
    )
    $bundle = $bundleCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($bundle) {
        Copy-Item $bundle (Join-Path $assets "scheduleiarcade") -Force
        Write-Host "Installed custom arcade AssetBundle: $bundle"
    } else {
        Write-Host "No custom AssetBundle found - this is OK. The packaged real FBX runtime scene will be used instead." -ForegroundColor Yellow
    }

    Write-Host "Installed DLL: $(Join-Path $modsDir 'ScheduleIArcade.dll')" -ForegroundColor Green
}

Write-Host ""
Write-Host "TEST FLOW" -ForegroundColor Cyan
Write-Host "1. Launch Schedule I and load a save."
Write-Host "2. Go to the vanilla Arcade door and interact/knock."
Write-Host "3. Choose Enter Arcade."
Write-Host "4. Confirm the real textured FBX arcade room appears and walk around it."
Write-Host "5. Look at a playable machine; confirm its prompt and attract screen show a `$10 cash price."
Write-Host "6. With less than `$10, press E; confirm play is blocked and no cash is removed."
Write-Host "7. With at least `$10, press E; confirm the session starts and exactly `$10 is deducted."
Write-Host "8. Press ESC to exit; confirm no second charge occurs."
Write-Host "9. Press F to pick up a machine; confirm its HUD slot and full-model ghost appear."
Write-Host "10. Q/E rotates, left click places, and right click/ESC cancels; placement remains free."
Write-Host "11. Test Pac-Man, pinball, Doom/test cabinets, and the Arcade TV."
Write-Host "12. Use both interior exits and confirm normal entrance/exit flow still works."
