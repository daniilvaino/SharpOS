# tools/perf_report.ps1
# ----------------------------------------------------------------------------
# Collect the [perf] name=value lines of one run and compare them with the
# previous one.
#
# Usage:
#   pwsh tools/perf_report.ps1                      # last_build.log in repo root
#   pwsh tools/perf_report.ps1 -Log boot.log -Baseline OS\.qemu\reports\perf-X.csv
#
# The kernel reports on COM1 (last_build.log): run.<app>.*, init.*, census.*.
# Programs report on their own output (last_app.log when the run had COM3):
# bench.*. Every run is saved as perf-<timestamp>.csv; without -Baseline the
# newest earlier CSV is the comparison, so running this after each build
# shows what the last change did.
# ----------------------------------------------------------------------------
# ASCII-only, like probe_report.ps1.

[CmdletBinding()]
param(
    [string]$Log = (Join-Path (Split-Path -Parent $PSScriptRoot) 'last_build.log'),
    [string]$AppLog = '',
    [string]$Baseline = '',
    [string]$ReportDir = (Join-Path (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'OS') '.qemu') 'reports'),
    # Do not write this run's CSV (for looking at an old log).
    [switch]$NoSave
)

if (-not (Test-Path -LiteralPath $Log)) {
    Write-Error "Log file not found: $Log"
    exit 1
}

$text = Get-Content -Raw -LiteralPath $Log

# Same rule as probe_report.ps1: the kernel log says whether program output
# had its own port, so a stale last_app.log is never mixed into another run.
$sources = @($text)
if ($text -match '\[ebs\] program output -> COM3') {
    if (-not $AppLog) {
        $AppLog = Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $Log)) 'last_app.log'
    }
    if (Test-Path -LiteralPath $AppLog) {
        $sources += Get-Content -Raw -LiteralPath $AppLog
    } else {
        Write-Warning "program output went to COM3, but $AppLog is missing - bench.* will be absent"
    }
}

# name -> value, in order of appearance. A name seen twice (the same program
# run twice in one boot) keeps both, the second as name#2.
$values = [ordered]@{}
foreach ($src in $sources) {
    foreach ($m in [regex]::Matches($src, '\[perf\] ([A-Za-z0-9_.:#-]+)=(-?\d+)')) {
        $name = $m.Groups[1].Value
        $n = 2
        $key = $name
        while ($values.Contains($key)) { $key = "$name#$n"; $n++ }
        $values[$key] = [long]$m.Groups[2].Value
    }
}

if ($values.Count -eq 0) {
    Write-Host "No [perf] lines in $Log" -ForegroundColor Yellow
    exit 0
}

if (-not $Baseline -and (Test-Path -LiteralPath $ReportDir)) {
    $prev = Get-ChildItem -LiteralPath $ReportDir -Filter 'perf-*.csv' |
        Sort-Object Name | Select-Object -Last 1
    if ($prev) { $Baseline = $prev.FullName }
}

$base = @{}
if ($Baseline -and (Test-Path -LiteralPath $Baseline)) {
    foreach ($row in Import-Csv -LiteralPath $Baseline) { $base[$row.Name] = [long]$row.Value }
}

Write-Host ""
Write-Host "=== SharpOS perf -- $Log ===" -ForegroundColor White
if ($base.Count -gt 0) { Write-Host "    baseline -- $Baseline" -ForegroundColor White }
Write-Host ""

$rows = foreach ($key in $values.Keys) {
    $v = $values[$key]
    $was = if ($base.ContainsKey($key)) { $base[$key] } else { $null }
    $delta = ''
    if ($null -ne $was) {
        if ($was -eq 0) { $delta = if ($v -eq 0) { '0%' } else { 'new>0' } }
        else { $delta = ('{0:+0.0;-0.0;0}%' -f (100.0 * ($v - $was) / $was)) }
    }
    [PSCustomObject]@{ Name = $key; Value = $v; Baseline = $was; Delta = $delta }
}
$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

if (-not $NoSave) {
    New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
    $out = Join-Path $ReportDir ("perf-{0}.csv" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $rows | Select-Object Name, Value | Export-Csv -LiteralPath $out -NoTypeInformation
    Write-Host "saved -- $out" -ForegroundColor DarkGray
}
