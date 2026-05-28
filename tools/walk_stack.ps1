# tools/walk_stack.ps1
# ---------------------------------------------------------------------------
# Scan a raw stack dump (from tools/dump_stack.ps1) for 8-byte values that
# look like code return-addresses, then report the most frequent ones and
# any repeating cyclic pattern -- the signature of unbounded recursion.
#
# SharpOS address map (approx, per run -- adjust if imageBase differs):
#   0x1BD3_0000 .. 0x1CB0_0000   kernel image (BOOTX64.EFI .text) + CoreCLR
#   0x1C00_0000 .. 0x1F00_0000   JIT code heaps / stub heaps (dynamic)
#   0x5000_0000_0000+            managed GC heap (NOT code -- skip)
#
# Usage:
#   pwsh tools/walk_stack.ps1 -File stack.bin -Base 0xE283000
#   pwsh tools/walk_stack.ps1 -File stack.bin -Base 0xE283000 -Top 40
# ---------------------------------------------------------------------------
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$File,
    [Parameter(Mandatory=$true)][string]$Base,   # VA of byte 0 of the dump
    [int]$Top = 30,
    [uint64]$CodeLo = 0x1BD30000,
    [uint64]$CodeHi = 0x1F000000
)

if ($Base -match '^0x') { $baseVal = [Convert]::ToUInt64($Base.Substring(2), 16) }
else { $baseVal = [Convert]::ToUInt64($Base, 10) }

$bytes = [System.IO.File]::ReadAllBytes($File)
Write-Host "Loaded $($bytes.Length) bytes, base VA 0x$($baseVal.ToString('X'))"

$freq = @{}            # code addr -> count
$hits = New-Object System.Collections.Generic.List[object]
for ($off = 0; $off + 8 -le $bytes.Length; $off += 8) {
    $v = [BitConverter]::ToUInt64($bytes, $off)
    if ($v -ge $CodeLo -and $v -lt $CodeHi) {
        $hits.Add([pscustomobject]@{ SlotVA = $baseVal + [uint64]$off; Code = $v })
        if ($freq.ContainsKey($v)) { $freq[$v]++ } else { $freq[$v] = 1 }
    }
}

Write-Host "`n=== Code-like return addresses: $($hits.Count) slots, $($freq.Count) distinct ==="
Write-Host "`n--- Top $Top by frequency (repeated = recursion candidate) ---"
$freq.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First $Top | ForEach-Object {
    "{0,6}x  0x{1:X}" -f $_.Value, $_.Key
}

# Cyclic pattern detection: look at the sequence of code addrs as they
# appear walking up the stack; report the shortest repeating window.
Write-Host "`n--- First 60 code addrs in stack order (low->high VA) ---"
$seq = $hits | ForEach-Object { $_.Code }
for ($i = 0; $i -lt [Math]::Min(60, $seq.Count); $i++) {
    "  [{0,4}] slot=0x{1:X}  code=0x{2:X}" -f $i, $hits[$i].SlotVA, $seq[$i]
}
