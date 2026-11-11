# Builds a curated personal LocalAssets pack from a read-only AssetRipper PrimaryContent
# export. Output is written to Build\LocalAssets (gitignored) and is NEVER copied into
# the repo, dist, or the public ZIP.
#
#   powershell -ExecutionPolicy Bypass -File Tools\Build-LocalAssetPack.ps1
#   powershell -ExecutionPolicy Bypass -File Tools\Build-LocalAssetPack.ps1 -Source "D:\S1-AssetRipper\export\PrimaryContent"
#
# Only the meshes and albedo textures the arcade actually uses as a fallback are copied.
# Large PNGs are downscaled to 1024 px on the long side so the pack stays small.
# Do not redistribute this folder; it is for personal use with a Schedule I copy you own.

param(
    [string]$Source = "D:\S1-AssetRipper\export\PrimaryContent",
    [string]$Destination = "",
    [int]$MaxTextureSize = 1024,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Destination) {
    $Destination = Join-Path $ProjectRoot "Build\LocalAssets"
}

if (-not (Test-Path -LiteralPath $Source)) {
    throw "AssetRipper PrimaryContent not found at $Source (read-only export expected)."
}
$meshRoot = Join-Path $Source "Assets\Mesh"
$texRoot = Join-Path $Source "Assets\Texture2D"
$resRoot = Join-Path $Source "Assets\Resources"
if (-not (Test-Path -LiteralPath $meshRoot) -or -not (Test-Path -LiteralPath $texRoot)) {
    throw "Source does not look like PrimaryContent (missing Assets\Mesh or Assets\Texture2D): $Source"
}

if (Test-Path -LiteralPath $Destination) {
    if (-not $Force) {
        Write-Host "Clearing existing $Destination"
    }
    Remove-Item -LiteralPath $Destination -Recurse -Force
}
$meshDest = Join-Path $Destination "meshes"
$texDest = Join-Path $Destination "textures"
New-Item $meshDest -ItemType Directory -Force | Out-Null
New-Item $texDest -ItemType Directory -Force | Out-Null

# Curated fallback set. Runtime cloning of live vanilla objects is always tried first;
# these files are only used when a name is not loaded in the running game.
$meshes = @(
    @{ Src = Join-Path $meshRoot "Door.glb";                             Names = @("Metal Glass Door (Static)", "Basic Metal Glass Door", "Door"); Dest = "Door.glb" },
    @{ Src = Join-Path $meshRoot "DoorHandle.glb";                       Names = @("DoorHandle"); Dest = "DoorHandle.glb" },
    @{ Src = Join-Path $meshRoot "Glass.002.glb";                        Names = @("Glass.002"); Dest = "Glass.002.glb" },
    @{ Src = Join-Path $meshRoot "Glass.005.glb";                        Names = @("Glass.005"); Dest = "Glass.005.glb" },
    @{ Src = Join-Path $meshRoot "Glass.glb";                            Names = @("Glass"); Dest = "Glass.glb" },
    @{ Src = Join-Path $meshRoot "DoorFrame.glb";                        Names = @("DoorFrame"); Dest = "DoorFrame.glb"; Texture = "grocerystore_DoorFrame mat" },
    @{ Src = Join-Path $meshRoot "LargeWindowFrame.glb";                 Names = @("LargeWindowFrame"); Dest = "LargeWindowFrame.glb"; Texture = "grocerystore_LargeWindowFrame mat" },
    @{ Src = Join-Path $meshRoot "WindowFrame.glb";                      Names = @("WindowFrame"); Dest = "WindowFrame.glb"; Texture = "grocerystore_WindowFrame mat" },
    @{ Src = Join-Path $meshRoot "WindowWall.glb";                       Names = @("WindowWall"); Dest = "WindowWall.glb" },
    @{ Src = Join-Path $meshRoot "UpperWindowWall.glb";                  Names = @("UpperWindowWall"); Dest = "UpperWindowWall.glb" },
    @{ Src = Join-Path $meshRoot "Light_Fluorescent_Light_LOD0.glb";     Names = @("Fluorescent_Light", "Light_Fluorescent_Light_LOD0"); Dest = "Light_Fluorescent_Light_LOD0.glb" },
    @{ Src = Join-Path $meshRoot "Counter.glb";                          Names = @("Counter", "prize counter"); Dest = "Counter.glb" },
    @{ Src = Join-Path $meshRoot "Frontdesk.glb";                        Names = @("Frontdesk"); Dest = "Frontdesk.glb" },
    @{ Src = Join-Path $meshRoot "Desk.glb";                             Names = @("Desk"); Dest = "Desk.glb" },
    @{ Src = Join-Path $resRoot "lights\halogenlight\HalogenLight.glb";  Names = @("HalogenLight"); Dest = "HalogenLight.glb" },
    @{ Src = Join-Path $resRoot "lights\ledlight\LEDLight.glb";          Names = @("LEDLight"); Dest = "LEDLight.glb" }
)

$textures = @(
    @{ Src = Join-Path $texRoot "Ceiling1_Albedo.png"; Names = @("Ceiling", "ceiling_0"); Dest = "Ceiling1_Albedo.png" },
    @{ Src = Join-Path $texRoot "Wallpaper_B.png"; Names = @("Wallpaper_B", "Wallpaper_B1", "Wallpaper_B2", "Wallpaper_B*"); Dest = "Wallpaper_B.png" },
    @{ Src = Join-Path $texRoot "Carpet_basecolor.png"; Names = @("Carpet", "Carpet1"); Dest = "Carpet_basecolor.png" },
    @{ Src = Join-Path $texRoot "Doors_albedo.png"; Names = @("Doors Black", "Doors_albedo"); Dest = "Doors_albedo.png" },
    @{ Src = Join-Path $texRoot "grocerystore_DoorFrame_AlbedoTransparency.png"; Names = @("grocerystore_DoorFrame mat"); Dest = "grocerystore_DoorFrame_AlbedoTransparency.png" },
    @{ Src = Join-Path $texRoot "grocerystore_LargeWindowFrame_AlbedoTransparency.png"; Names = @("grocerystore_LargeWindowFrame mat"); Dest = "grocerystore_LargeWindowFrame_AlbedoTransparency.png" },
    @{ Src = Join-Path $texRoot "grocerystore_WindowFrame_AlbedoTransparency.png"; Names = @("grocerystore_WindowFrame mat", "WindowFrame"); Dest = "grocerystore_WindowFrame_AlbedoTransparency.png" },
    @{ Src = Join-Path $texRoot "cash register_Base_Color.png"; Names = @("Cash Register", "CashCounter", "cash register_Base_Color"); Dest = "cash register_Base_Color.png" },
    @{ Src = Join-Path $texRoot "cashcounter_Body_AlbedoTransparency.png"; Names = @("cashcounter_Body"); Dest = "cashcounter_Body_AlbedoTransparency.png" },
)

function Copy-Mesh([hashtable]$item) {
    if (-not (Test-Path -LiteralPath $item.Src)) {
        Write-Host "SKIP mesh (not in export): $($item.Dest)"
        return $false
    }
    Copy-Item -LiteralPath $item.Src -Destination (Join-Path $meshDest $item.Dest) -Force
    $len = (Get-Item -LiteralPath (Join-Path $meshDest $item.Dest)).Length
    Write-Host ("  mesh  {0,8:N1} KB  {1}" -f ($len / 1KB), $item.Dest)
    return $true
}

function Copy-TextureDownscaled([hashtable]$item) {
    if (-not (Test-Path -LiteralPath $item.Src)) {
        Write-Host "SKIP texture (not in export): $($item.Dest)"
        return $false
    }
    $dest = Join-Path $texDest $item.Dest
    $copied = $false
    try {
        Add-Type -AssemblyName System.Drawing -ErrorAction Stop
        $img = [System.Drawing.Image]::FromFile($item.Src)
        try {
            $w = $img.Width; $h = $img.Height
            $scale = 1.0
            $long = [Math]::Max($w, $h)
            if ($long -gt $MaxTextureSize) { $scale = $MaxTextureSize / $long }
            $nw = [Math]::Max(1, [int][Math]::Round($w * $scale))
            $nh = [Math]::Max(1, [int][Math]::Round($h * $scale))
            if ($scale -lt 1.0) {
                $bmp = New-Object System.Drawing.Bitmap $nw, $nh
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $g.DrawImage($img, 0, 0, $nw, $nh)
                $g.Dispose()
                $bmp.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
                $bmp.Dispose()
                $copied = $true
                $len = (Get-Item -LiteralPath $dest).Length
                Write-Host ("  tex   {0,8:N1} KB  {1} (downscaled {2}x{3} -> {4}x{5})" -f ($len / 1KB), $item.Dest, $w, $h, $nw, $nh)
            }
        } finally {
            $img.Dispose()
        }
    } catch {
        Write-Host "  (System.Drawing resize failed: $($_.Exception.Message); copying original)"
    }
    if (-not $copied) {
        Copy-Item -LiteralPath $item.Src -Destination $dest -Force
        $len = (Get-Item -LiteralPath $dest).Length
        Write-Host ("  tex   {0,8:N1} KB  {1}" -f ($len / 1KB), $item.Dest)
    }
    return $true
}

function Json-Escape([string]$s) { $s.Replace('\', '\\').Replace('"', '\"') }
function Json-Names([string[]]$names) {
    return "[" + (($names | ForEach-Object { '"' + (Json-Escape $_) + '"' }) -join ", ") + "]"
}

$entries = New-Object System.Collections.Generic.List[string]
# Transparent glass is generated at runtime (no ripped glass texture is required).
$entries.Add('    {"kind": "texture", "names": ["window_glass_material", "Glass", "glass", "window_glass"], "file": "", "transparent": true}')

Write-Host "Copying curated meshes..."
$meshCount = 0
foreach ($item in $meshes) {
    if (Copy-Mesh $item) {
        $meshCount++
        $texField = ""
        if ($item.ContainsKey("Texture") -and $item.Texture) {
            $texField = ', "texture": "' + (Json-Escape $item.Texture) + '"'
        }
        $entries.Add(('    {{"kind": "mesh", "names": {0}, "file": "meshes/{1}"{2}}}' -f (Json-Names $item.Names), (Json-Escape $item.Dest), $texField))
    }
}

Write-Host "Copying curated textures (max $MaxTextureSize px)..."
$texCount = 0
foreach ($item in $textures) {
    if (Copy-TextureDownscaled $item) {
        $texCount++
        $entries.Add(('    {{"kind": "texture", "names": {0}, "file": "textures/{1}"}}' -f (Json-Names $item.Names), (Json-Escape $item.Dest)))
    }
}

$notice = "Personal local pack built from a Schedule I install you own. Do not redistribute this folder or the private ZIP."
$manifest = @"
{
  "version": "0.6.0",
  "notice": "$notice",
  "source": "AssetRipper PrimaryContent (Schedule I 0.4.7f12), curated fallback subset only",
  "entries": [
$($entries -join ",`r`n")
  ]
}
"@
Set-Content -LiteralPath (Join-Path $Destination "manifest.json") -Value $manifest -Encoding UTF8

$noticeText = @"
Schedule I Arcade — personal local asset pack
============================================

This folder was built from a Schedule I copy you own (via a local AssetRipper
export). It is for personal use with friends who also own the game.

Do not commit this folder to git.
Do not upload it to GitHub or include it in the public ScheduleIArcade ZIP.
The public mod works without this folder: it clones live vanilla objects first
and only uses these files when a name is not loaded in the running game.
"@
Set-Content -LiteralPath (Join-Path $Destination "NOTICE.txt") -Value $noticeText -Encoding UTF8

$total = (Get-ChildItem -LiteralPath $Destination -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ""
Write-Host "Local asset pack written to $Destination"
Write-Host ("  {0} meshes, {1} textures, {2:N2} MB total (gitignored)." -f $meshCount, $texCount, ($total / 1MB))
Write-Host "  manifest: $meshCount mesh + $($texCount + 1) texture entries (includes generated glass)."
