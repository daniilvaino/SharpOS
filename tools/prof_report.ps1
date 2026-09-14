# tools/prof_report.ps1
# ---------------------------------------------------------------------------
# Where a measured interval spent its time, by function.
#
# The kernel's sampler (Sampler.cs) prints, at the end of every interval
# PerfCounters reports, the hottest addresses of that interval and their
# callers:
#
#   [prof] run.Bench hot 41x krnl+0x0012ab34
#   [prof] run.Bench caller 17x krnl+0x00345678
#
# This adds them up over every run of the interval in the log, names the
# kernel-image addresses with tools/symbolize.ps1 (CoreCLR is linked into the
# kernel, so its functions resolve too) and sums them per function.
#
# The sample rate is the timer's (Probes.TimerHz, 1000 a second since step173;
# 100 before): shares are what matter, and a function below a few percent of a
# short interval is noise.
#
# Under QEMU TCG the profile leans towards code that touches devices (HPET
# reads, xHCI and serial registers): the emulated timer fires under the same
# lock a device access takes, so ticks land right after one. Confirm such an
# entry with a [perf] counter before acting on it (Sampler.cs).
#
# Usage:
#   pwsh tools/prof_report.ps1                      # run.Bench from last_build.log
#   pwsh tools/prof_report.ps1 -Scope census -Top 30
#   pwsh tools/prof_report.ps1 -Log work/last_build.x.log
# ---------------------------------------------------------------------------
[CmdletBinding()]
param(
    [string] $Scope = "run.Bench",
    [int]    $Top = 25,
    [string] $Log = "$PSScriptRoot\..\last_build.log"
)

$ErrorActionPreference = "Stop"

$text = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $Log).Path)

$samples = 0
foreach ($m in [regex]::Matches($text, '\[perf\] ' + [regex]::Escape($Scope) + '\.prof\.samples=(\d+)')) {
    $samples += [long]$m.Groups[1].Value
}

$hits = @{ hot = @{}; caller = @{} }
$pattern = '\[prof\] ' + [regex]::Escape($Scope) + ' (hot|caller) (\d+)x (\S+)'
foreach ($m in [regex]::Matches($text, $pattern)) {
    $kind = $m.Groups[1].Value
    $address = $m.Groups[3].Value
    $table = $hits[$kind]
    $table[$address] = [long]($table[$address]) + [long]$m.Groups[2].Value
}

if ($hits.hot.Count -eq 0) {
    Write-Host "no [prof] lines for $Scope in $Log" -ForegroundColor Yellow
    exit 1
}

# One symbolize call for every kernel address.
$rvas = @($hits.hot.Keys + $hits.caller.Keys | Where-Object { $_ -like 'krnl+*' } | Sort-Object -Unique |
    ForEach-Object { '0x' + [Convert]::ToUInt64($_.Substring(7), 16).ToString('x') })
$names = @{}
if ($rvas.Count -gt 0) {
    $output = & pwsh -NoProfile -File "$PSScriptRoot\symbolize.ps1" -Rva ($rvas -join ',') 2>&1
    foreach ($line in $output) {
        $mm = [regex]::Match([string]$line, '^0x([0-9a-f]+)\s+(\S+)')
        if ($mm.Success) { $names[[Convert]::ToUInt64($mm.Groups[1].Value, 16)] = $mm.Groups[2].Value }
    }
}

function Get-Name([string]$address) {
    if ($address -notlike 'krnl+*') { return $address }
    $rva = [Convert]::ToUInt64($address.Substring(7), 16)
    if ($names.ContainsKey($rva)) { return $names[$rva] }
    return $address
}

# "Function+0x1c" and "Function+0x40" are the same function.
function Get-Function([string]$name) { return ($name -replace '\+0x[0-9a-f]+$', '') }

Write-Host ""
Write-Host "  $Scope -- $samples samples in the log" -ForegroundColor White
Write-Host "  (device-touching code -- HPET, xHCI, serial -- reads high under QEMU TCG; check it against a counter)"

foreach ($kind in @('hot', 'caller')) {
    $total = ($hits[$kind].Values | Measure-Object -Sum).Sum
    $byFunction = @{}
    foreach ($address in $hits[$kind].Keys) {
        $function = Get-Function (Get-Name $address)
        $byFunction[$function] = [long]($byFunction[$function]) + $hits[$kind][$address]
    }

    Write-Host ""
    $title = if ($kind -eq 'hot') { 'where the CPU was' } else { 'who called it (return address at RSP; leaf functions only)' }
    Write-Host "  $kind -- $title" -ForegroundColor White
    $byFunction.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First $Top | ForEach-Object {
        $share = if ($samples -gt 0) { 100.0 * $_.Value / $samples } else { 0 }
        "    {0,6} {1,5:0.0}%  {2}" -f $_.Value, $share, $_.Key
    }
    Write-Host ("    (the listed top of each run covers {0} of {1} samples)" -f $total, $samples)
}
