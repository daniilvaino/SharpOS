# tools/New-EspImage.ps1
# ---------------------------------------------------------------------------
# Build a raw FAT32 image out of the staged ESP directory.
#
# Why this exists: QEMU's `-drive file=fat:rw:esp` (VVFAT) does not store a
# filesystem, it synthesises one and tries to map guest writes back onto host
# files. That makes it useless as a target for testing our own FAT writer —
# we would be measuring QEMU's reconstruction, not our correctness. A real
# image is just bytes, and the host can verify the result independently with
# mdir/mtype.
#
# Dot-source and call:
#   . tools/New-EspImage.ps1
#   New-EspImage -SourceDir ...\esp -RawPath ...\esp.img -SizeMb 512
#
# Returns $true on success, $false when mtools are unavailable (the caller is
# expected to fall back to VVFAT rather than fail the build).
# ---------------------------------------------------------------------------

function Resolve-MtoolsPath {
    param([string]$Name)

    # На Windows инструмент зовётся mformat.exe, на macOS и Linux — mformat.
    # Пробуем оба имени, поэтому вызывающему можно передавать любое.
    $bare = [System.IO.Path]::GetFileNameWithoutExtension($Name)
    foreach ($candidate in @($Name, $bare, "$bare.exe")) {
        $cmd = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }

    $roots = @(
        "C:\msys64\mingw64\bin",   # MSYS2
        "/opt/homebrew/bin",         # Homebrew на Apple Silicon
        "/usr/local/bin",            # Homebrew на Intel
        "/usr/bin"                   # пакет mtools в Linux
    )
    foreach ($root in $roots) {
        foreach ($candidate in @($Name, $bare, "$bare.exe")) {
            $fallback = Join-Path $root $candidate
            if (Test-Path -LiteralPath $fallback) { return $fallback }
        }
    }

    return $null
}

function New-EspImage {
    param(
        [Parameter(Mandatory=$true)][string]$SourceDir,
        [Parameter(Mandatory=$true)][string]$RawPath,
        [int]$SizeMb = 512
    )

    $mformat = Resolve-MtoolsPath "mformat.exe"
    $mcopy = Resolve-MtoolsPath "mcopy.exe"
    if (-not $mformat -or -not $mcopy) {
        Write-Warning "mtools (mformat/mcopy) not found - falling back to QEMU VVFAT. Filesystem writes cannot be tested against it; install mingw-w64-x86_64-mtools (MSYS2), brew install mtools (macOS) или apt install mtools (Linux)."
        return $false
    }

    if (Test-Path -LiteralPath $RawPath) {
        Remove-Item -LiteralPath $RawPath -Force
    }

    $stream = [System.IO.File]::Open($RawPath, [System.IO.FileMode]::CreateNew,
                                     [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try { $stream.SetLength([int64]$SizeMb * 1MB) } finally { $stream.Dispose() }

    # -F forces FAT32. No partition table: the image is a "superfloppy", which
    # both OVMF and our own Fat32.Mount already handle.
    & $mformat -i $RawPath -F "::"
    if ($LASTEXITCODE -ne 0) { throw "mformat failed with exit code $LASTEXITCODE" }

    foreach ($item in Get-ChildItem -LiteralPath $SourceDir -Force) {
        & $mcopy -i $RawPath -s $item.FullName "::/"
        if ($LASTEXITCODE -ne 0) { throw "mcopy failed for '$($item.FullName)' with exit code $LASTEXITCODE" }
    }

    return $true
}
