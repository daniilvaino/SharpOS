# Empirical NativeAOT EH trailer reader.
#
# Reads the kernel PE binary directly:
#   1. Parses section headers, picks .pdata.
#   2. For each RUNTIME_FUNCTION (Begin, End, UnwindInfoRva), follow
#      UnwindInfoRva into .xdata, decode standard UNWIND_INFO header
#      (Version | Flags, SizeOfProlog, CountOfCodes, FrameRegister | FrameOffset),
#      compute size of the standard blob = 4 + 2*CountOfCodes (rounded
#      to dword), then read the NativeAOT trailer immediately after.
#   3. Trailer byte 0 = unwindBlockFlags. Bits:
#        0x03 = func kind  (0=ROOT, 1=HANDLER, 2=FILTER)
#        0x04 = UBF_FUNC_HAS_EHINFO
#        0x08 = UBF_FUNC_REVERSE_PINVOKE
#        0x10 = UBF_FUNC_HAS_ASSOCIATED_DATA
#   4. If HAS_ASSOCIATED_DATA ->read 4-byte RVA.
#   5. If HAS_EHINFO ->read 4-byte ehInfoRVA. Map this RVA back to a
#      section header to identify which section holds the EH blob.
#
# Outputs:
#   - Distribution of func kinds (ROOT vs HANDLER vs FILTER).
#   - How many records carry HAS_EHINFO + section name where EH blob lives.
#   - Sample of ehInfoRVA values mapped to (section, offset).
#
# Usage:
#   .\probe_eh_trailer.ps1
#   .\probe_eh_trailer.ps1 -BinaryPath OS\bin\Release\net7.0\win-x64\native\OS.exe

param(
    [string]$BinaryPath = "OS\bin\Release\net7.0\win-x64\native\OS.exe"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $BinaryPath)) {
    throw "Binary not found: $BinaryPath. Run .\run_build.ps1 -NoRun first."
}

$bytes = [System.IO.File]::ReadAllBytes($BinaryPath)
Write-Host "Binary: $BinaryPath ($($bytes.Length) bytes)"

# --- PE header parsing ---
$peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
if ([BitConverter]::ToInt32($bytes, $peOffset) -ne 0x4550) {
    throw "Not a PE file."
}

$coffHeaderOffset = $peOffset + 4
$numSections      = [BitConverter]::ToUInt16($bytes, $coffHeaderOffset + 2)
$optHeaderSize    = [BitConverter]::ToUInt16($bytes, $coffHeaderOffset + 16)
$optHeaderOffset  = $coffHeaderOffset + 20
$sectionTableOffset = $optHeaderOffset + $optHeaderSize

# Magic (PE32 = 0x10B / PE32+ = 0x20B)
$magic = [BitConverter]::ToUInt16($bytes, $optHeaderOffset)
if ($magic -ne 0x20B) {
    throw "Expected PE32+ image (magic 0x20B), got 0x$($magic.ToString('X4'))."
}

# Parse section headers
class SectionInfo {
    [string]$Name
    [uint32]$VirtualSize
    [uint32]$VirtualAddress
    [uint32]$SizeOfRawData
    [uint32]$PointerToRawData
}
$sections = New-Object System.Collections.Generic.List[SectionInfo]

for ($i = 0; $i -lt $numSections; $i++) {
    $off = $sectionTableOffset + $i * 40
    $nameBytes = $bytes[$off..($off + 7)]
    $nameStr = [System.Text.Encoding]::ASCII.GetString($nameBytes).TrimEnd([char]0)
    if ([string]::IsNullOrEmpty($nameStr)) { $nameStr = "<unnamed#$i>" }

    $s = [SectionInfo]::new()
    $s.Name             = $nameStr
    $s.VirtualSize      = [BitConverter]::ToUInt32($bytes, $off + 8)
    $s.VirtualAddress   = [BitConverter]::ToUInt32($bytes, $off + 12)
    $s.SizeOfRawData    = [BitConverter]::ToUInt32($bytes, $off + 16)
    $s.PointerToRawData = [BitConverter]::ToUInt32($bytes, $off + 20)
    $sections.Add($s)
}

Write-Host ""
Write-Host "=== Sections ==="
foreach ($s in $sections) {
    Write-Host ("  {0,-12} VA=0x{1:X8} VSize=0x{2:X6} RawPtr=0x{3:X8} RawSize=0x{4:X6}" -f `
                $s.Name, $s.VirtualAddress, $s.VirtualSize, $s.PointerToRawData, $s.SizeOfRawData)
}

# RVA ->file offset
function RvaToOffset($rva) {
    foreach ($s in $sections) {
        if ($rva -ge $s.VirtualAddress -and $rva -lt ($s.VirtualAddress + $s.VirtualSize)) {
            return $s.PointerToRawData + ($rva - $s.VirtualAddress)
        }
    }
    return -1
}

# RVA ->section name
function RvaToSection($rva) {
    foreach ($s in $sections) {
        if ($rva -ge $s.VirtualAddress -and $rva -lt ($s.VirtualAddress + $s.VirtualSize)) {
            return $s.Name
        }
    }
    return "<not found>"
}

# Find .pdata section
$pdata = $sections | Where-Object { $_.Name -eq ".pdata" } | Select-Object -First 1
if (-not $pdata) { throw ".pdata section not found." }

Write-Host ""
Write-Host "=== Walking .pdata (size 0x$($pdata.VirtualSize.ToString('X4'))) ==="

$pdataFileOffset = $pdata.PointerToRawData
$recordCount = [int]($pdata.VirtualSize / 12)
Write-Host "Total RUNTIME_FUNCTION records: $recordCount"
Write-Host ""

# Histograms
$kindHist = @{ 0 = 0; 1 = 0; 2 = 0; 3 = 0 }
$kindNames = @{ 0 = "ROOT"; 1 = "HANDLER"; 2 = "FILTER"; 3 = "FILTERED?" }
$hasEhInfo = 0
$hasAssocData = 0
$reversePInvoke = 0

# Section histogram for ehInfoRVA
$ehInfoSectionHist = @{}

# Section histogram for associated-data RVA
$assocDataSectionHist = @{}

# Sample first 20 records with EH info for the report.
$samples = New-Object System.Collections.Generic.List[string]

# Sample first 20 funclet records (HANDLER/FILTER kind).
$funcletSamples = New-Object System.Collections.Generic.List[string]

for ($i = 0; $i -lt $recordCount; $i++) {
    $recOff = $pdataFileOffset + $i * 12
    $beginRva   = [BitConverter]::ToUInt32($bytes, $recOff)
    $endRva     = [BitConverter]::ToUInt32($bytes, $recOff + 4)
    $unwindRva  = [BitConverter]::ToUInt32($bytes, $recOff + 8)

    $unwindOff = RvaToOffset $unwindRva
    if ($unwindOff -lt 0) { continue }

    # Standard UNWIND_INFO header
    $verFlags     = $bytes[$unwindOff]
    $version      = $verFlags -band 0x07
    $flagsByte    = ($verFlags -shr 3) -band 0x1F
    $sizeOfProlog = $bytes[$unwindOff + 1]
    $countOfCodes = $bytes[$unwindOff + 2]
    # Frame register / offset at byte +3 (we don't need it here)

    # Standard blob size = 4 (header) + 2*CountOfCodes, rounded up to dword.
    $codesBytes = 2 * $countOfCodes
    if ($codesBytes -band 2) { $codesBytes += 2 }   # round up to dword
    $stdSize = 4 + $codesBytes

    # If flags include EHANDLER (0x01) or UHANDLER (0x02), there's an extra
    # 4-byte handler RVA followed by language-specific data. NativeAOT
    # typically does NOT set those for managed methods, but check.
    $hasHandler = ($flagsByte -band 0x03) -ne 0
    if ($hasHandler) {
        # Skip 4 bytes of handler RVA. Language-specific data follows but
        # for NativeAOT-style trailer we still expect unwindBlockFlags
        # right after --the handler RVA bridges to it.
        $stdSize += 4
    }

    $trailerOff = $unwindOff + $stdSize

    if ($trailerOff -ge $bytes.Length) { continue }

    $blockFlags = $bytes[$trailerOff]
    $kind = $blockFlags -band 0x03
    $kindHist[$kind]++

    $cursor = $trailerOff + 1

    $assocRva = 0
    $ehRva    = 0

    if (($blockFlags -band 0x10) -ne 0) {
        $hasAssocData++
        $assocRva = [BitConverter]::ToUInt32($bytes, $cursor)
        $cursor += 4
        $sec = RvaToSection $assocRva
        if (-not $assocDataSectionHist.ContainsKey($sec)) { $assocDataSectionHist[$sec] = 0 }
        $assocDataSectionHist[$sec]++
    }

    if (($blockFlags -band 0x04) -ne 0) {
        $hasEhInfo++
        $ehRva = [BitConverter]::ToUInt32($bytes, $cursor)
        $cursor += 4
        $sec = RvaToSection $ehRva
        if (-not $ehInfoSectionHist.ContainsKey($sec)) { $ehInfoSectionHist[$sec] = 0 }
        $ehInfoSectionHist[$sec]++

        if ($samples.Count -lt 20) {
            $samples.Add(("rec[{0,4}] beginRVA=0x{1:X6} endRVA=0x{2:X6} unwindRVA=0x{3:X6} bf=0x{4:X2} kind={5} ehRVA=0x{6:X6} ehSection={7}" -f `
                $i, $beginRva, $endRva, $unwindRva, $blockFlags, $kindNames[$kind], $ehRva, $sec))
        }
    }

    if (($blockFlags -band 0x08) -ne 0) {
        $reversePInvoke++
    }

    if ($kind -ne 0 -and $funcletSamples.Count -lt 20) {
        $funcletSamples.Add(("funclet rec[{0,4}] beginRVA=0x{1:X6} kind={2} bf=0x{3:X2}" -f `
            $i, $beginRva, $kindNames[$kind], $blockFlags))
    }
}

Write-Host "=== Record kind histogram (UBF_FUNC_KIND_*) ==="
foreach ($k in 0..3) {
    Write-Host ("  {0,-10} {1,6}" -f $kindNames[$k], $kindHist[$k])
}
Write-Host ""

Write-Host "=== Trailer flags totals ==="
Write-Host ("  HAS_EHINFO          : {0,6}" -f $hasEhInfo)
Write-Host ("  HAS_ASSOCIATED_DATA : {0,6}" -f $hasAssocData)
Write-Host ("  REVERSE_PINVOKE     : {0,6}" -f $reversePInvoke)
Write-Host ""

Write-Host "=== ehInfoRVA ->section histogram ==="
$ehInfoSectionHist.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
    Write-Host ("  {0,-20} {1,6}" -f $_.Key, $_.Value)
}
Write-Host ""

if ($assocDataSectionHist.Count -gt 0) {
    Write-Host "=== associated-data RVA ->section histogram ==="
    $assocDataSectionHist.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
        Write-Host ("  {0,-20} {1,6}" -f $_.Key, $_.Value)
    }
    Write-Host ""
}

Write-Host "=== Sample records with EH info (first 20) ==="
foreach ($s in $samples) { Write-Host "  $s" }
Write-Host ""

if ($funcletSamples.Count -gt 0) {
    Write-Host "=== Sample funclet records (first 20) ==="
    foreach ($s in $funcletSamples) { Write-Host "  $s" }
    Write-Host ""
}

Write-Host "=== Done. ==="
