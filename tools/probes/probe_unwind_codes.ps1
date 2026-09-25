# Scan ALL 703 RUNTIME_FUNCTION records and build a histogram of:
#   - Unwind flag values (None / EHANDLER / UHANDLER / CHAININFO)
#   - Unwind opcode types (PUSH_NONVOL / ALLOC_SMALL / ALLOC_LARGE / SAVE_NONVOL / SAVE_NONVOL_FAR / SET_FPREG / SAVE_XMM128 / SAVE_XMM128_FAR / PUSH_MACHFRAME / EPILOG)
#   - Distribution of prologue sizes
#   - Distribution of (Count of codes) values
#
# Decision input for Phase 1 step 4: do we really only need PUSH_NONVOL +
# ALLOC_SMALL in the StackFrameIterator unwind decoder, or does ILC emit
# something larger somewhere across all 703 records.
#
# Usage:
#   .\probe_unwind_codes.ps1
#   .\probe_unwind_codes.ps1 -BinaryPath OS\bin\Release\net7.0\win-x64\native\OS.exe
#   .\probe_unwind_codes.ps1 > unwind_scan.txt    # save full output

param(
    [string]$BinaryPath = "OS\bin\Release\net7.0\win-x64\native\OS.exe"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $BinaryPath)) {
    throw "Binary not found: $BinaryPath. Run .\run_build.ps1 -NoRun first."
}

# Locate dumpbin.exe
$dumpbin = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
if (-not $dumpbin) {
    $vsRoot = "${env:ProgramFiles}\Microsoft Visual Studio"
    if (Test-Path $vsRoot) {
        $candidate = Get-ChildItem -Path $vsRoot -Filter dumpbin.exe -Recurse -ErrorAction SilentlyContinue |
                     Where-Object { $_.FullName -match "Hostx64\\x64" } |
                     Select-Object -First 1
        if ($candidate) { $dumpbin = $candidate.FullName }
    }
}
if (-not $dumpbin) {
    throw "dumpbin.exe not found."
}
$dumpbinPath = if ($dumpbin -is [string]) { $dumpbin } else { $dumpbin.Source }

Write-Host "=== Scanning $BinaryPath ==="
Write-Host "Tool: $dumpbinPath"
Write-Host ""

$lines = & $dumpbinPath /UNWINDINFO $BinaryPath

# Parse state
$flags = @{}
$opcodes = @{}
$prologueSizes = @{}
$codeCounts = @{}
$totalRecords = 0
$recordsWithCodes = 0
$recordsWithFlags = 0  # records whose flags != "None"

# Examples to keep for the report.
$nonStandardOpcodeExamples = New-Object System.Collections.Generic.List[string]
$flaggedRecordExamples = New-Object System.Collections.Generic.List[string]
$largeStackExamples = New-Object System.Collections.Generic.List[string]   # ALLOC_LARGE if any

# Track last seen function header for example collection.
$currentFunc = $null
$currentFlag = $null

foreach ($line in $lines) {
    # Function record header line (RVA Begin End InfoRVA Name)
    if ($line -match '^\s*[0-9A-F]{8}\s+([0-9A-F]{8})\s+([0-9A-F]{8})\s+[0-9A-F]{8}\s+(.+)$') {
        $totalRecords++
        $currentFunc = $matches[3].Trim()
        continue
    }

    if ($line -match '^\s*Unwind flags:\s+(\S+)') {
        $f = $matches[1]
        if (-not $flags.ContainsKey($f)) { $flags[$f] = 0 }
        $flags[$f]++
        $currentFlag = $f
        if ($f -ne "None") {
            $recordsWithFlags++
            if ($flaggedRecordExamples.Count -lt 30) {
                $flaggedRecordExamples.Add("[$f] $currentFunc")
            }
        }
        continue
    }

    if ($line -match '^\s*Size of prologue:\s+0x([0-9A-Fa-f]+)') {
        $sz = [int]([Convert]::ToUInt32($matches[1], 16))
        if (-not $prologueSizes.ContainsKey($sz)) { $prologueSizes[$sz] = 0 }
        $prologueSizes[$sz]++
        continue
    }

    if ($line -match '^\s*Count of codes:\s+(\d+)') {
        $cnt = [int]$matches[1]
        if (-not $codeCounts.ContainsKey($cnt)) { $codeCounts[$cnt] = 0 }
        $codeCounts[$cnt]++
        if ($cnt -gt 0) { $recordsWithCodes++ }
        continue
    }

    # Unwind code lines look like:
    #   "      0A: ALLOC_SMALL, size=0x40"
    #   "      06: PUSH_NONVOL, register=rbx"
    if ($line -match '^\s+[0-9A-F]{2,4}:\s+([A-Z_]+)') {
        $op = $matches[1]
        if (-not $opcodes.ContainsKey($op)) { $opcodes[$op] = 0 }
        $opcodes[$op]++

        # Track non-PUSH_NONVOL / non-ALLOC_SMALL examples --these matter
        # for our minimal decoder decision.
        if ($op -ne "PUSH_NONVOL" -and $op -ne "ALLOC_SMALL") {
            if ($nonStandardOpcodeExamples.Count -lt 50) {
                $nonStandardOpcodeExamples.Add("[$op] $currentFunc | $($line.Trim())")
            }
        }

        # ALLOC_LARGE means stack > 128 bytes --interesting separately.
        if ($op -eq "ALLOC_LARGE" -and $largeStackExamples.Count -lt 20) {
            $largeStackExamples.Add("$currentFunc | $($line.Trim())")
        }
        continue
    }
}

Write-Host "=== TOTAL ==="
Write-Host "RUNTIME_FUNCTION records: $totalRecords"
Write-Host "Records with at least one unwind code: $recordsWithCodes"
Write-Host "Records with non-None Unwind flags: $recordsWithFlags"
Write-Host ""

Write-Host "=== Unwind flags histogram ==="
$flags.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
    Write-Host ("  {0,-20} {1,8}" -f $_.Key, $_.Value)
}
Write-Host ""

Write-Host "=== Unwind opcode histogram ==="
$opcodes.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
    Write-Host ("  {0,-20} {1,8}" -f $_.Key, $_.Value)
}
Write-Host ""

Write-Host "=== Code count histogram (top 15) ==="
$codeCounts.GetEnumerator() | Sort-Object Name | Select-Object -First 15 | ForEach-Object {
    Write-Host ("  count={0,-3}  records={1,8}" -f $_.Key, $_.Value)
}
Write-Host ""

Write-Host "=== Prologue size histogram (top 15 sorted by size) ==="
$prologueSizes.GetEnumerator() | Sort-Object Name | Select-Object -First 15 | ForEach-Object {
    Write-Host ("  prologue=0x{0:X2}  records={1,8}" -f $_.Key, $_.Value)
}
Write-Host ""

if ($flaggedRecordExamples.Count -gt 0) {
    Write-Host "=== Records with non-None Unwind flags (sample) ==="
    foreach ($ex in $flaggedRecordExamples) { Write-Host "  $ex" }
    Write-Host ""
}

if ($nonStandardOpcodeExamples.Count -gt 0) {
    Write-Host "=== Examples of opcodes other than PUSH_NONVOL / ALLOC_SMALL ==="
    foreach ($ex in $nonStandardOpcodeExamples) { Write-Host "  $ex" }
    Write-Host ""
} else {
    Write-Host "=== Only PUSH_NONVOL + ALLOC_SMALL observed in all records ==="
    Write-Host ""
}

if ($largeStackExamples.Count -gt 0) {
    Write-Host "=== ALLOC_LARGE examples (stack > 128 bytes) ==="
    foreach ($ex in $largeStackExamples) { Write-Host "  $ex" }
    Write-Host ""
}

Write-Host "=== Done. ==="
Write-Host "Decision input for Phase 1 step 4 (StackFrameIterator unwind decoder):"
Write-Host "- If only PUSH_NONVOL + ALLOC_SMALL ->minimal decoder is sufficient for current binary."
Write-Host "- If any other opcode ->must extend decoder before step 4 runs in production."
Write-Host "- Non-None flags signal funclets (HANDLER/FILTER) or chained unwind --affects step 2 walker."
