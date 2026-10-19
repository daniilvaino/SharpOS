# Test-FatImage — an outside check of the FAT volume the guest wrote
# (step208): dosfstools' fsck.fat, read-only (-n), on the image file.
#
# The kernel's FAT writer creates, grows, deletes and makes directories; a
# mistake there spoils the whole volume, not one file, and nothing inside
# the guest would notice — the writer and the reader share their mistakes.
# fsck.fat is the second opinion: the same tool a host runs on the stick.
#
# fsck.fat comes from the host (Linux, macOS with dosfstools) or, on
# Windows, from WSL. Neither: the check is skipped and said so.
#
# "Free cluster summary uninitialized" is not a problem: the writer marks
# FSInfo's count unknown on its first change (Fat32Files.FreeCountUnknown),
# which the FAT32 specification allows; fsck.fat exits 0 on it.

function Test-FatImage {
    param(
        [Parameter(Mandatory)] [string]$ImagePath,
        [Parameter(Mandatory)] [string]$LogPath
    )
    # $true clean, $false problems found, $null no fsck.fat to ask.
    if (-not (Test-Path -LiteralPath $ImagePath)) { return $null }

    $native = Get-Command fsck.fat -ErrorAction SilentlyContinue
    if ($native) {
        $output = & $native.Source -n -v $ImagePath 2>&1
        $code = $LASTEXITCODE
    }
    elseif ($env:OS -eq 'Windows_NT' -and (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
        $inWsl = (& wsl.exe wslpath -a ($ImagePath -replace '\\', '/') 2>$null | Out-String).Trim()
        if (-not $inWsl) { return $null }
        & wsl.exe sh -c "command -v fsck.fat >/dev/null" 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        $output = & wsl.exe fsck.fat -n -v $inWsl 2>&1
        $code = $LASTEXITCODE
    }
    else {
        return $null
    }

    # WSL's console hands back UTF-16 with NULs between the bytes on some hosts.
    $text = (($output | ForEach-Object { "$_" }) -join "`n") -replace "`0", ''
    Set-Content -LiteralPath $LogPath -Value $text -Encoding UTF8
    return $code -eq 0
}
