# Dumps EH-related sections from the built kernel for Phase 1 unwinder
# work. Reads:
#   .pdata         — array of RUNTIME_FUNCTION { Begin, End, UnwindRVA }
#   .xdata         — UNWIND_INFO records + NativeAOT trailer
#                    (unwindBlockFlags, dataRVA, ehInfoRVA, GCInfo varints)
#
# Output: one summary line + raw hex dump of first ~200 bytes of each.
# Sanity-shows that ILC emits Windows-SEH unwind format on win-x64 EFI.
#
# Usage:
#   .\probe_eh_binary.ps1
#   .\probe_eh_binary.ps1 -BinaryPath OS\bin\Release\net7.0\win-x64\native\OS.exe

param(
    [string]$BinaryPath = "OS\bin\Release\net7.0\win-x64\native\OS.exe"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $BinaryPath)) {
    throw "Binary not found: $BinaryPath. Run .\run_build.ps1 -NoRun first."
}

Write-Host "=== Binary: $BinaryPath ==="
$fi = Get-Item -LiteralPath $BinaryPath
Write-Host "Size: $($fi.Length) bytes"
Write-Host ""

# Locate dumpbin.exe (ships with MSVC Build Tools)
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
    throw "dumpbin.exe not found. Install VS Build Tools or run from 'x64 Native Tools Command Prompt'."
}
$dumpbinPath = if ($dumpbin -is [string]) { $dumpbin } else { $dumpbin.Source }
Write-Host "dumpbin: $dumpbinPath"
Write-Host ""

# 1. Section headers — confirm .pdata / .xdata exist and their sizes.
Write-Host "=== Section headers (filtered) ==="
& $dumpbinPath /HEADERS $BinaryPath |
    Select-String -Pattern "^(SECTION|\s+.*name|\s+\w+\s+(virtual|file|\d))",
                            "(\.pdata|\.xdata|\.text|\.rdata|\.data)" |
    Select-Object -First 60 | ForEach-Object { $_.Line }
Write-Host ""

# 2. UNWINDINFO summary — first few records to confirm format.
Write-Host "=== /UNWINDINFO (first ~100 lines) ==="
$unwind = & $dumpbinPath /UNWINDINFO $BinaryPath
$unwind | Select-Object -First 100 | ForEach-Object { $_ }
Write-Host ""

$totalRecords = ($unwind | Select-String -Pattern "^\s+\d+\s+[0-9A-F]{8}\s+[0-9A-F]{8}\s+[0-9A-F]{8}").Count
Write-Host "Total RUNTIME_FUNCTION records: $totalRecords"
Write-Host ""

# 3. Raw hex of .pdata / .xdata for trailer inspection.
Write-Host "=== Raw .pdata (first 256 bytes via /SECTION:.pdata /RAWDATA) ==="
& $dumpbinPath /SECTION:.pdata /RAWDATA:8 $BinaryPath |
    Select-String -Pattern "^\s+[0-9A-F]{8}:" |
    Select-Object -First 16 | ForEach-Object { $_.Line }
Write-Host ""

Write-Host "=== Raw .xdata (first 512 bytes) ==="
& $dumpbinPath /SECTION:.xdata /RAWDATA:8 $BinaryPath |
    Select-String -Pattern "^\s+[0-9A-F]{8}:" |
    Select-Object -First 32 | ForEach-Object { $_.Line }
Write-Host ""

# 4. Search for our RhpThrowEx and EH-related symbols in the export table /
#    static symbols. [RuntimeExport] surface should be visible.
Write-Host "=== EH-related exports ==="
& $dumpbinPath /EXPORTS $BinaryPath |
    Select-String -Pattern "Rhp(Throw|Rethrow|Call.*Funclet|EH)", "FailFast", "AppendException", "OnFirstChance", "OnUnhandled", "GetRuntimeException" |
    ForEach-Object { $_.Line }
Write-Host ""

Write-Host "=== Done. ==="
Write-Host "Notes:"
Write-Host "- .pdata records are 12 bytes each (3 RVAs)."
Write-Host "- .xdata records: standard UNWIND_INFO followed by NativeAOT trailer:"
Write-Host "    [UNWIND_INFO N bytes] [unwindBlockFlags(1)] [dataRVA(4)?] [ehInfoRVA(4)?] [GCInfo varints]"
Write-Host "- unwindBlockFlags bits: 0x03=funcKind(ROOT/HANDLER/FILTER), 0x04=HAS_EHINFO,"
Write-Host "  0x08=REVERSE_PINVOKE, 0x10=HAS_ASSOCIATED_DATA"
