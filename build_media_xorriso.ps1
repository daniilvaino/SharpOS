param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoBuild,
    [string]$EspSource = (Join-Path $PSScriptRoot "OS\.qemu\esp"),
    [string]$OutputDir = (Join-Path $PSScriptRoot "OS\.qemu\media"),
    [int]$EspImageSizeMb = 64,
    [int]$VhdDiskSizeMb = 128,
    [int]$VhdEspStartLba = 2048,
    [int]$IsoBootImageSizeMb = 16,
    [string]$VhdPath,
    [ValidateSet("dynamic", "fixed")]
    [string]$VhdSubformat = "dynamic",
    [string]$IsoPath,
    [string]$IsoVolumeLabel = "SHARPOS",
    [string]$XorrisoPath,
    [string]$SfdiskPath,
    [string]$MtoolsBinDir,
    [switch]$KeepRawImage
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-ToolPath {
    param(
        [string]$Name,
        [string]$ExplicitPath,
        [string[]]$Fallbacks
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath)) {
            throw "Tool '$Name' not found at explicit path: $ExplicitPath"
        }

        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $fromPath = Get-Command $Name -ErrorAction SilentlyContinue
    if ($fromPath) {
        return $fromPath.Source
    }

    foreach ($candidate in $Fallbacks) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "Tool '$Name' is not available."
}

function Convert-ToMsysPath {
    param([string]$WindowsPath)

    $full = [System.IO.Path]::GetFullPath($WindowsPath)
    $normalized = $full.Replace('\', '/')
    if ($normalized -match '^([A-Za-z]):/(.*)$') {
        $drive = $matches[1].ToLowerInvariant()
        $rest = $matches[2]
        return "/$drive/$rest"
    }

    return $normalized
}

function Write-U16BE {
    param([byte[]]$Buffer, [int]$Offset, [uint16]$Value)
    $Buffer[$Offset + 0] = [byte](($Value -shr 8) -band 0xFF)
    $Buffer[$Offset + 1] = [byte]($Value -band 0xFF)
}

function Write-U32LE {
    param([byte[]]$Buffer, [int]$Offset, [uint32]$Value)
    $Buffer[$Offset + 0] = [byte]($Value -band 0xFF)
    $Buffer[$Offset + 1] = [byte](($Value -shr 8) -band 0xFF)
    $Buffer[$Offset + 2] = [byte](($Value -shr 16) -band 0xFF)
    $Buffer[$Offset + 3] = [byte](($Value -shr 24) -band 0xFF)
}

function Write-U32BE {
    param([byte[]]$Buffer, [int]$Offset, [uint32]$Value)
    $Buffer[$Offset + 0] = [byte](($Value -shr 24) -band 0xFF)
    $Buffer[$Offset + 1] = [byte](($Value -shr 16) -band 0xFF)
    $Buffer[$Offset + 2] = [byte](($Value -shr 8) -band 0xFF)
    $Buffer[$Offset + 3] = [byte]($Value -band 0xFF)
}

function Write-U64BE {
    param([byte[]]$Buffer, [int]$Offset, [uint64]$Value)
    for ($i = 0; $i -lt 8; $i++) {
        $shift = (7 - $i) * 8
        $Buffer[$Offset + $i] = [byte](($Value -shr $shift) -band 0xFF)
    }
}

function Build-FatEspImage {
    param(
        [string]$RawPath,
        [string]$SourceDir,
        [int]$SizeMb,
        [string]$MformatExe,
        [string]$McopyExe
    )

    if (Test-Path -LiteralPath $RawPath) {
        Remove-Item -LiteralPath $RawPath -Force
    }

    $sizeBytes = [int64]$SizeMb * 1MB
    $stream = [System.IO.File]::Open($RawPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $stream.SetLength($sizeBytes)
    }
    finally {
        $stream.Dispose()
    }

    & $MformatExe -i $RawPath -F "::"
    if ($LASTEXITCODE -ne 0) {
        throw "mformat failed with exit code $LASTEXITCODE"
    }

    $items = Get-ChildItem -LiteralPath $SourceDir -Force
    foreach ($item in $items) {
        & $McopyExe -i $RawPath -s $item.FullName "::/"
        if ($LASTEXITCODE -ne 0) {
            throw "mcopy failed for '$($item.FullName)' with exit code $LASTEXITCODE"
        }
    }
}

function Build-RawDiskWithGptEsp {
    param(
        [string]$EspRawPath,
        [string]$DiskRawPath,
        [int64]$DiskSizeBytes,
        [int]$EspStartLba,
        [string]$SfdiskExe
    )

    if (Test-Path -LiteralPath $DiskRawPath) {
        Remove-Item -LiteralPath $DiskRawPath -Force
    }

    $diskBytes = $DiskSizeBytes
    if (($diskBytes % 512) -ne 0) {
        throw "Disk size must be sector-aligned (512 bytes): $diskBytes"
    }

    $espBytes = (Get-Item -LiteralPath $EspRawPath).Length
    $espSectors = [uint32][Math]::Ceiling($espBytes / 512.0)
    $startLba = [uint32]$EspStartLba
    $endSectorExclusive = [uint64]$startLba + [uint64]$espSectors
    $diskSectors = [uint64]($diskBytes / 512)

    if ($endSectorExclusive -gt $diskSectors) {
        throw "ESP image does not fit disk: startLba=$startLba espSectors=$espSectors diskSectors=$diskSectors"
    }

    $stream = [System.IO.File]::Open($DiskRawPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $stream.SetLength($diskBytes)
    }
    finally {
        $stream.Dispose()
    }

    $sfdiskInput = "$startLba,$espSectors,U,*`n"
    $sfdiskInput | & $SfdiskExe --wipe always --no-reread --label gpt $DiskRawPath
    if ($LASTEXITCODE -ne 0) {
        throw "sfdisk failed with exit code $LASTEXITCODE"
    }

    $diskWriteStream = [System.IO.File]::Open($DiskRawPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $espReadStream = [System.IO.File]::Open($EspRawPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $diskWriteStream.Position = [int64]$startLba * 512
        $espReadStream.CopyTo($diskWriteStream)
        $diskWriteStream.Flush()
    }
    finally {
        $espReadStream.Dispose()
        $diskWriteStream.Dispose()
    }
}

function Resolve-VpcVirtualSizeBytes {
    param(
        [string]$QemuImgExe,
        [string]$OutputDirPath,
        [int]$RequestedMb,
        [ValidateSet("dynamic", "fixed")]
        [string]$Subformat
    )

    $probePath = Join-Path $OutputDirPath "_vpc_size_probe.vhd"
    if (Test-Path -LiteralPath $probePath) {
        Remove-Item -LiteralPath $probePath -Force
    }

    try {
        & $QemuImgExe create -f vpc -o ("subformat=$Subformat") $probePath ("$RequestedMb" + "M") | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "qemu-img create probe failed with exit code $LASTEXITCODE"
        }

        $infoJson = & $QemuImgExe info --output=json $probePath
        if ($LASTEXITCODE -ne 0) {
            throw "qemu-img info probe failed with exit code $LASTEXITCODE"
        }

        $parsed = $infoJson | ConvertFrom-Json
        $virtualSize = [int64]$parsed.'virtual-size'
        if ($virtualSize -le 0) {
            throw "Invalid probe virtual-size: $virtualSize"
        }

        return $virtualSize
    }
    finally {
        if (Test-Path -LiteralPath $probePath) {
            Remove-Item -LiteralPath $probePath -Force
        }
    }
}

function Build-VhdFromDiskRaw {
    param(
        [string]$DiskRawPath,
        [string]$VhdOutPath,
        [string]$QemuImgExe,
        [ValidateSet("dynamic", "fixed")]
        [string]$Subformat
    )

    if (Test-Path -LiteralPath $VhdOutPath) {
        Remove-Item -LiteralPath $VhdOutPath -Force
    }

    & $QemuImgExe convert -f raw -O vpc -o ("subformat=$Subformat") $DiskRawPath $VhdOutPath
    if ($LASTEXITCODE -ne 0) {
        throw "qemu-img convert failed with exit code $LASTEXITCODE"
    }
}

function Build-IsoFromEsp {
    param(
        [string]$IsoOutPath,
        [string]$EspSourceDir,
        [string]$EfiBootImagePath,
        [string]$XorrisoExe,
        [string]$VolumeLabel
    )

    $isoRoot = Join-Path ([System.IO.Path]::GetDirectoryName($IsoOutPath)) "_iso_root"
    if (Test-Path -LiteralPath $isoRoot) {
        Remove-Item -LiteralPath $isoRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $isoRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $EspSourceDir "*") -Destination $isoRoot -Recurse -Force

    $efiBootDir = Join-Path $isoRoot "EFI\BOOT"
    New-Item -ItemType Directory -Path $efiBootDir -Force | Out-Null
    Copy-Item -LiteralPath $EfiBootImagePath -Destination (Join-Path $efiBootDir "efiboot.img") -Force

    if (Test-Path -LiteralPath $IsoOutPath) {
        Remove-Item -LiteralPath $IsoOutPath -Force
    }

    $isoOutMsys = Convert-ToMsysPath -WindowsPath $IsoOutPath
    $isoRootMsys = Convert-ToMsysPath -WindowsPath $isoRoot

    & $XorrisoExe `
        -as mkisofs `
        -iso-level 3 `
        -R `
        -J `
        -V $VolumeLabel `
        -eltorito-alt-boot `
        -e "EFI/BOOT/efiboot.img" `
        -no-emul-boot `
        -o $isoOutMsys `
        $isoRootMsys

    if ($LASTEXITCODE -ne 0) {
        throw "xorriso failed with exit code $LASTEXITCODE"
    }

    Remove-Item -LiteralPath $isoRoot -Recurse -Force
}

if (-not (Test-Path -LiteralPath $EspSource)) {
    throw "ESP source path not found: $EspSource"
}

if ([string]::IsNullOrWhiteSpace($VhdPath)) {
    $VhdPath = Join-Path $OutputDir "sharpos-esp.vhd"
}

if ([string]::IsNullOrWhiteSpace($IsoPath)) {
    $IsoPath = Join-Path $OutputDir "sharpos-boot.iso"
}

if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$mformatFallback = @()
$mcopyFallback = @()
if (-not [string]::IsNullOrWhiteSpace($MtoolsBinDir)) {
    $mformatFallback += (Join-Path $MtoolsBinDir "mformat.exe")
    $mcopyFallback += (Join-Path $MtoolsBinDir "mcopy.exe")
}

$mformatFallback += "C:\msys64\mingw64\bin\mformat.exe"
$mcopyFallback += "C:\msys64\mingw64\bin\mcopy.exe"
$xorrisoFallback = @("C:\msys64\usr\bin\xorriso.exe")
$qemuImgFallback = @("C:\msys64\mingw64\bin\qemu-img.exe")
$sfdiskFallback = @("C:\msys64\usr\bin\sfdisk.exe")

$mformatExe = Resolve-ToolPath -Name "mformat.exe" -ExplicitPath $null -Fallbacks $mformatFallback
$mcopyExe = Resolve-ToolPath -Name "mcopy.exe" -ExplicitPath $null -Fallbacks $mcopyFallback
$xorrisoExe = Resolve-ToolPath -Name "xorriso.exe" -ExplicitPath $XorrisoPath -Fallbacks $xorrisoFallback
$qemuImgExe = Resolve-ToolPath -Name "qemu-img.exe" -ExplicitPath $null -Fallbacks $qemuImgFallback
$sfdiskExe = Resolve-ToolPath -Name "sfdisk.exe" -ExplicitPath $SfdiskPath -Fallbacks $sfdiskFallback

Write-Host "mformat: $mformatExe"
Write-Host "mcopy  : $mcopyExe"
Write-Host "xorriso: $xorrisoExe"
Write-Host "qemu-img: $qemuImgExe"
Write-Host "sfdisk: $sfdiskExe"

if (-not $NoBuild) {
    Write-Host "Building SharpOS (NoRun)..."
    & (Join-Path $PSScriptRoot "run_build.ps1") -Configuration $Configuration -NoRun
    if ($LASTEXITCODE -ne 0) {
        throw "run_build.ps1 failed with exit code $LASTEXITCODE"
    }
}

$rawPath = Join-Path $OutputDir "sharpos-esp.raw"
$isoBootPath = Join-Path $OutputDir "sharpos-efiboot.img"
$diskRawPath = Join-Path $OutputDir "sharpos-disk.raw"

Write-Host "Creating FAT ESP image..."
Build-FatEspImage -RawPath $rawPath -SourceDir $EspSource -SizeMb $EspImageSizeMb -MformatExe $mformatExe -McopyExe $mcopyExe

Write-Host "Creating FAT EFI boot image for ISO..."
Build-FatEspImage -RawPath $isoBootPath -SourceDir $EspSource -SizeMb $IsoBootImageSizeMb -MformatExe $mformatExe -McopyExe $mcopyExe

Write-Host "Resolving VPC-compatible virtual disk size..."
$normalizedDiskBytes = Resolve-VpcVirtualSizeBytes -QemuImgExe $qemuImgExe -OutputDirPath $OutputDir -RequestedMb $VhdDiskSizeMb -Subformat $VhdSubformat
Write-Host ("VPC virtual size (bytes): " + $normalizedDiskBytes)

Write-Host "Creating raw GPT disk with ESP partition..."
Build-RawDiskWithGptEsp -EspRawPath $rawPath -DiskRawPath $diskRawPath -DiskSizeBytes $normalizedDiskBytes -EspStartLba $VhdEspStartLba -SfdiskExe $sfdiskExe

Write-Host "Creating VHD..."
Build-VhdFromDiskRaw -DiskRawPath $diskRawPath -VhdOutPath $VhdPath -QemuImgExe $qemuImgExe -Subformat $VhdSubformat

Write-Host "Creating ISO..."
Build-IsoFromEsp -IsoOutPath $IsoPath -EspSourceDir $EspSource -EfiBootImagePath $isoBootPath -XorrisoExe $xorrisoExe -VolumeLabel $IsoVolumeLabel

if (-not $KeepRawImage) {
    if (Test-Path -LiteralPath $rawPath) {
        Remove-Item -LiteralPath $rawPath -Force
    }

    if (Test-Path -LiteralPath $isoBootPath) {
        Remove-Item -LiteralPath $isoBootPath -Force
    }

    if (Test-Path -LiteralPath $diskRawPath) {
        Remove-Item -LiteralPath $diskRawPath -Force
    }
}

Write-Host "Done."
Write-Host "VHD: $VhdPath"
Write-Host "ISO: $IsoPath"
