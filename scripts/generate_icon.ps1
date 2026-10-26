param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\SerialBridge.App\Assets\app.ico'),
    [string]$LogoOutputPath = (Join-Path $PSScriptRoot '..\src\SerialBridge.App\Assets\app-logo.png')
)

# The hand-drawn artwork is the canonical asset. Do not procedurally redraw
# the previous icon or overwrite app.ico with an unrelated design.
$ErrorActionPreference = 'Stop'
$sourcePath = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\src\SerialBridge.App\Assets\app.ico'))
$targetPath = [System.IO.Path]::GetFullPath($OutputPath)
$logoPath = [System.IO.Path]::GetFullPath($LogoOutputPath)
if ($logoPath -eq $sourcePath -or $logoPath -eq $targetPath) {
    throw 'Logo output must not overwrite the ICO asset'
}
$logoFrame = $null
$requiredSizes = @(16, 20, 24, 32, 48, 64, 128, 256)

if (-not [System.IO.File]::Exists($sourcePath)) {
    throw "Canonical app icon not found: $sourcePath"
}

$bytes = [System.IO.File]::ReadAllBytes($sourcePath)
if ($bytes.Length -lt 6) { throw 'Invalid ICO header' }
$stream = [System.IO.MemoryStream]::new($bytes, $false)
$reader = [System.IO.BinaryReader]::new($stream)
try {
    if ($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1) {
        throw 'Invalid ICO signature'
    }
    $count = [int]$reader.ReadUInt16()
    if ($bytes.Length -lt 6 + 16 * $count) {
        throw 'Truncated ICO directory'
    }
    $foundSizes = [System.Collections.Generic.HashSet[int]]::new()
    $pngSignature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    for ($i = 0; $i -lt $count; $i++) {
        $stream.Position = 6 + 16 * $i
        $width = [int]$reader.ReadByte()
        $height = [int]$reader.ReadByte()
        if ($width -eq 0) { $width = 256 }
        if ($height -eq 0) { $height = 256 }
        [void]$reader.ReadByte() # color count
        [void]$reader.ReadByte() # reserved
        [void]$reader.ReadUInt16() # planes
        $bitsPerPixel = $reader.ReadUInt16()
        $length = [uint64]$reader.ReadUInt32()
        $offset = [uint64]$reader.ReadUInt32()
        if ($width -ne $height -or $bitsPerPixel -ne 32 -or
            $length -lt 24 -or $offset -lt (6 + 16 * $count) -or
            $offset + $length -gt $bytes.Length) {
            throw "Invalid ICO frame $i ($width x $height)"
        }
        for ($j = 0; $j -lt $pngSignature.Length; $j++) {
            if ($bytes[[int]$offset + $j] -ne $pngSignature[$j]) {
                throw "Frame $i must contain a PNG image"
            }
        }
        # PNG IHDR stores width and height as big-endian uint32.
        $pngWidth = 0
        $pngHeight = 0
        for ($j = 0; $j -lt 4; $j++) {
            $pngWidth = $pngWidth * 256 + $bytes[[int]$offset + 16 + $j]
            $pngHeight = $pngHeight * 256 + $bytes[[int]$offset + 20 + $j]
        }
        if ($pngWidth -ne $width -or $pngHeight -ne $height) {
            throw "PNG size mismatch in frame $i"
        }
        if (-not $foundSizes.Add($width)) {
            throw "Duplicate ICO frame size: $width"
        }
        if ($width -eq 256) {
            $logoFrame = [byte[]]::new([int]$length)
            [System.Array]::Copy($bytes, [int]$offset, $logoFrame, 0, [int]$length)
        }
    }
    foreach ($size in $requiredSizes) {
        if (-not $foundSizes.Contains($size)) {
            throw "Missing Windows icon size: ${size}x$size"
        }
    }
}
finally {
    $reader.Dispose()
    $stream.Dispose()
}

if ($sourcePath -ne $targetPath) {
    [System.IO.Directory]::CreateDirectory(
        [System.IO.Path]::GetDirectoryName($targetPath)) | Out-Null
    [System.IO.File]::Copy($sourcePath, $targetPath, $true)
    Write-Host "Copied validated app icon to $targetPath"
}
else {
    Write-Host "Validated existing app icon: $sourcePath"
}
Write-Host "Verified PNG icon frames: $($requiredSizes -join ', ')"

# WPF ImageSourceConverter selects the ICO's first (16px) frame for an Image.
# Extract the original 256px PNG bytes losslessly for the 42-DIP header logo.
[System.IO.Directory]::CreateDirectory(
    [System.IO.Path]::GetDirectoryName($logoPath)) | Out-Null
[System.IO.File]::WriteAllBytes($logoPath, $logoFrame)
Write-Host "Extracted original 256px logo: $logoPath"
