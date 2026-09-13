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
    # The same Bench.dll run natively on the host (`dotnet Bench.dll`), for an
    # "x host" column. QEMU's software CPU is ~20x slower on plain code, so a
    # benchmark far above that factor points at SharpOS, not at emulation.
    [string]$Reference = (Join-Path $PSScriptRoot 'bench-host-reference.log'),
    # The same Bench.dll on stock Linux inside the same QEMU (run_linux_ref.ps1),
    # for an "x linux" column: emulation is in both, so what remains is SharpOS.
    [string]$LinuxReference = (Join-Path $PSScriptRoot 'bench-qemu-linux-reference.log'),
    # Do not write this run's CSV (for looking at an old log).
    [switch]$NoSave,
    # Record this run in the progress history under this name, and rebuild
    # the progress table. Re-using a label replaces that entry.
    [string]$Label = '',
    # Date shown for the entry (default: today). For entries recorded later.
    [string]$When = '',
    [string]$History = (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs') 'perf-history.csv'),
    [string]$Progress = (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs') 'perf-progress.md')
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

# What the numbers were taken on. Two runs are only comparable on the same
# fork build, CPU model, accelerator and disk path; the report says when they
# are not instead of letting a config change pass for a code change.
$config = [ordered]@{}
$mFork = [regex]::Match($text, '\[info\] fork: (\S+)')
$config['fork'] = if ($mFork.Success) { $mFork.Groups[1].Value } else { 'unknown' }
# Logs from before the line existed had tiering on.
$mTier = [regex]::Match($text, '\[info\] hosted tiering: (\S+)')
$config['tiering'] = if ($mTier.Success) { $mTier.Groups[1].Value } else { 'on' }
$mQemu = [regex]::Match($text, 'QEMU: [^\r\n]*')
if ($mQemu.Success) {
    $q = $mQemu.Value
    $mCpu = [regex]::Match($q, '-cpu (\S+)')
    $mAccel = [regex]::Match($q, 'accel=(\w+)')
    $config['cpu'] = if ($mCpu.Success) { $mCpu.Groups[1].Value } else { 'default' }
    $config['accel'] = if ($mAccel.Success) { $mAccel.Groups[1].Value } else { 'default' }
    $config['disk'] = if ($q -match 'usb-storage') { 'usb' } else { 'sata' }
} else {
    $config['vm'] = 'not-qemu'
}

if (-not $Baseline -and (Test-Path -LiteralPath $ReportDir)) {
    $prev = Get-ChildItem -LiteralPath $ReportDir -Filter 'perf-*.csv' |
        Sort-Object Name | Select-Object -Last 1
    if ($prev) { $Baseline = $prev.FullName }
}

$base = @{}
$baseConfig = @{}
if ($Baseline -and (Test-Path -LiteralPath $Baseline)) {
    foreach ($row in Import-Csv -LiteralPath $Baseline) {
        [long]$n = 0
        if ($row.Name -like 'config.*') { $baseConfig[$row.Name.Substring(7)] = $row.Value }
        elseif ([long]::TryParse($row.Value, [ref]$n)) { $base[$row.Name] = $n }
    }
}

Write-Host ""
Write-Host "=== SharpOS perf -- $Log ===" -ForegroundColor White
Write-Host ("    config -- " + (($config.Keys | ForEach-Object { "$_=$($config[$_])" }) -join ' ')) -ForegroundColor White
if ($base.Count -gt 0) {
    Write-Host "    baseline -- $Baseline" -ForegroundColor White
    foreach ($k in $config.Keys) {
        if ($baseConfig.ContainsKey($k) -and $baseConfig[$k] -ne $config[$k]) {
            Write-Host "    WARNING: baseline $k=$($baseConfig[$k]), this run $k=$($config[$k]) -- not comparable" -ForegroundColor Yellow
        }
    }
}
Write-Host ""

function Read-Reference([string]$path, [string]$what) {
    $table = @{}
    if ($path -and (Test-Path -LiteralPath $path)) {
        foreach ($m in [regex]::Matches((Get-Content -Raw -LiteralPath $path), '\[perf\] ([A-Za-z0-9_.:-]+)=(-?\d+)')) {
            $table[$m.Groups[1].Value] = [long]$m.Groups[2].Value
        }
        Write-Host "    $what reference -- $path" -ForegroundColor White
    }
    return $table
}
$host_ = Read-Reference $Reference 'host'
$linux_ = Read-Reference $LinuxReference 'linux-in-qemu'
Write-Host ""

# The references are Bench.dll runs; the NativeAOT benchmark does the same
# work under aot.*, so it is compared with the bench.* line of the same name.
function Get-RefName([string]$metric) { return (($metric -replace '^aot\.', 'bench.') -replace '\.first\.', '.') }

function Get-Ratio($value, [hashtable]$ref, [string]$metric) {
    $name = Get-RefName $metric
    if ($ref.ContainsKey($name) -and $ref[$name] -gt 0 -and $metric -like '*ns_per_op' -and $null -ne $value -and $value -ne '') {
        return ('x{0:0.#}' -f ([double]$value / $ref[$name]))
    }
    return ''
}

$rows = foreach ($key in $values.Keys) {
    $v = $values[$key]
    $was = if ($base.ContainsKey($key)) { $base[$key] } else { $null }
    $delta = ''
    if ($null -ne $was) {
        if ($was -eq 0) { $delta = if ($v -eq 0) { '0%' } else { 'new>0' } }
        else { $delta = ('{0:+0.0;-0.0;0}%' -f (100.0 * ($v - $was) / $was)) }
    }
    # "name#2" is the same benchmark run again; it compares with the same reference line.
    $plain = $key -replace '#\d+$', ''
    [PSCustomObject]@{ Name = $key; Value = $v; Baseline = $was; Delta = $delta
                       Host = (Get-Ratio $v $host_ $plain); Linux = (Get-Ratio $v $linux_ $plain) }
}
$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

if (-not $NoSave) {
    New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
    $out = Join-Path $ReportDir ("perf-{0}.csv" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $configRows = foreach ($k in $config.Keys) { [PSCustomObject]@{ Name = "config.$k"; Value = $config[$k] } }
    @($configRows) + @($rows | Select-Object Name, Value) | Export-Csv -LiteralPath $out -NoTypeInformation
    Write-Host "saved -- $out" -ForegroundColor DarkGray
}

# --- progress history ------------------------------------------------------
#
# One row per (entry, metric) in docs/perf-history.csv; docs/perf-progress.md
# holds a table rebuilt from it, one column per entry. Only the metrics below
# go in: the table is for watching the suspects move, not for every counter.
#
# A benchmark run several times in one boot is recorded as the median of the
# warm runs -- the first one pays for compiling the benchmark itself.

$tracked = [ordered]@{
    'bench.alloc.short.ns_per_op'    = 'alloc.short, ns/op'
    'bench.alloc.short.gc0'          = 'alloc.short, gen0 collections'
    'bench.alloc.retained.ns_per_op' = 'alloc.retained, ns/op'
    'bench.strings.ns_per_op'        = 'strings, ns/op'
    'bench.collections.ns_per_op'    = 'collections, ns/op'
    'bench.exceptions.ns_per_op'     = 'exceptions, ns/op'
    'bench.jit.ns_per_op'            = 'jit, ns/op'
    'bench.tasks.ns_per_op'          = 'tasks, ns/op'
    'bench.output.ns_per_op'         = 'output, ns/line'
    'run.Bench.clock.avg_ns'         = 'clock read, ns (kernel)'
    'bench.clock.utcnow.backsteps'   = 'clock backsteps (UtcNow)'
    'bench.clock.self_timed_ms'      = 'clock test by Stopwatch, ms'
    'run.Bench.wall_ms'              = 'whole Bench run by HPET, ms'
    'run.Bench.screen.total_ms'      = 'screen repaints, ms/run'
    'run.Bench.disklog.total_ms'     = 'disk log, ms/run'
    'run.Bench.write.total_ms'       = 'writes served by kernel, ms/run'
    'census.wall_ms'                 = 'census, ms'
    # The NativeAOT app tier (apps_native/BenchAot, BENCHAOT.EXE): the same
    # work, compared with the same host and Linux lines as the hosted rows.
    'aot.alloc.short.ns_per_op'      = 'AOT alloc.short, ns/op'
    'aot.alloc.short.gc'             = 'AOT alloc.short, collections'
    'aot.alloc.retained.ns_per_op'   = 'AOT alloc.retained, ns/op'
    'aot.strings.ns_per_op'          = 'AOT strings, ns/op'
    'aot.collections.ns_per_op'      = 'AOT collections, ns/op'
    'aot.exceptions.ns_per_op'       = 'AOT exceptions, ns/op'
    'aot.tasks.ns_per_op'            = 'AOT tasks, ns/op'
    'aot.output.ns_per_op'           = 'AOT output, ns/line'
    'aot.output.first.ns_per_op'     = 'AOT output before tasks, ns/line'
    'run.BENCHAOT.screen.total_ms'   = 'AOT screen repaints, ms/run'
    'run.BENCHAOT.write.total_ms'    = 'AOT writes served by kernel, ms/run'
}

function Get-Warm([long[]]$list) {
    $l = @($list)
    if ($l.Count -ge 2) { $l = $l[1..($l.Count - 1)] }
    $s = @($l | Sort-Object)
    $mid = [int][math]::Floor($s.Count / 2)
    if ($s.Count % 2 -eq 1) { return $s[$mid] }
    return [long][math]::Round(($s[$mid - 1] + $s[$mid]) / 2.0)
}

function Format-Metric([string]$metric, $value) {
    if ($null -eq $value -or $value -eq '') { return '' }
    $v = [double]$value
    if ($metric -like '*ns_per_op' -or $metric -like '*avg_ns') {
        if ($v -lt 1000) { return ('{0:0} ns' -f $v) }
        if ($v -lt 1000000) { return ('{0:0.0} us' -f ($v / 1000)) }
        return ('{0:0.00} ms' -f ($v / 1000000))
    }
    return ('{0:0}' -f $v)
}

if ($Label) {
    $groups = [ordered]@{}
    foreach ($key in $values.Keys) {
        $plain = $key -replace '#\d+$', ''
        if (-not $tracked.Contains($plain)) { continue }
        if (-not $groups.Contains($plain)) { $groups[$plain] = New-Object System.Collections.Generic.List[long] }
        $groups[$plain].Add($values[$key])
    }

    $date = if ($When) { $When } else { Get-Date -Format 'yyyy-MM-dd' }
    $configText = ($config.Keys | ForEach-Object { "$_=$($config[$_])" }) -join ' '

    $kept = @()
    if (Test-Path -LiteralPath $History) {
        $kept = @(Import-Csv -LiteralPath $History | Where-Object { $_.Label -ne $Label })
    }
    $added = foreach ($metric in $groups.Keys) {
        [PSCustomObject]@{ Label = $Label; When = $date; Config = $configText; Metric = $metric; Value = (Get-Warm $groups[$metric].ToArray()) }
    }
    @($kept) + @($added) | Export-Csv -LiteralPath $History -NoTypeInformation -Encoding utf8

    # Rebuild the table from the whole history.
    $all = @(Import-Csv -LiteralPath $History)
    $labels = New-Object System.Collections.Generic.List[string]
    foreach ($r in $all) { if (-not $labels.Contains($r.Label)) { $labels.Add($r.Label) } }
    $cell = @{}
    $meta = @{}
    foreach ($r in $all) {
        $cell["$($r.Label)|$($r.Metric)"] = $r.Value
        $meta[$r.Label] = $r
    }
    # The ratio columns compare the newest entry taken on the usual setup: an
    # experiment on another CPU model is not what the references were taken on.
    $usual = @('fork=Release', 'tiering=on', 'cpu=qemu64,+nx', 'accel=tcg', 'disk=usb', 'fork=unknown')
    $last = $labels[$labels.Count - 1]
    for ($k = $labels.Count - 1; $k -ge 0; $k--) {
        $odd = @($meta[$labels[$k]].Config -split ' ' | Where-Object { $_ -and $_ -notin $usual })
        if ($odd.Count -eq 0) { $last = $labels[$k]; break }
    }

    $lines = New-Object System.Collections.Generic.List[string]
    # host: the PC, natively. linux qemu: Debian in the same QEMU. The two
    # ratios on the right are for the newest usual-setup entry; "x linux" is
    # the one that is SharpOS's alone.
    $lines.Add('| | host | linux qemu | ' + ($labels -join ' | ') + ' | x host | x linux |')
    $lines.Add('|---|---:|---:|' + (($labels | ForEach-Object { '---:' }) -join '|') + '|---:|---:|')
    $lines.Add('| date | | | ' + (($labels | ForEach-Object { $meta[$_].When }) -join ' | ') + ' | | |')
    # Only what differs from the usual QEMU setup is spelled out.
    $lines.Add('| config | | | ' + (($labels | ForEach-Object {
        $c = $meta[$_].Config
        $parts = @($c -split ' ' | Where-Object { $_ -notin $usual })
        $parts -join ' '
    }) -join ' | ') + ' | | |')
    foreach ($metric in $tracked.Keys) {
        if (-not ($labels | Where-Object { $cell.ContainsKey("$_|$metric") })) { continue }
        $refName = Get-RefName $metric
        $hostValue = if ($metric -like '*ns_per_op' -and $host_.ContainsKey($refName)) { $host_[$refName] } elseif ($host_.ContainsKey($metric)) { $host_[$metric] } else { $null }
        $linuxValue = if ($metric -like '*ns_per_op' -and $linux_.ContainsKey($refName)) { $linux_[$refName] } elseif ($linux_.ContainsKey($metric)) { $linux_[$metric] } else { $null }
        $cells = foreach ($l in $labels) { Format-Metric $metric $cell["$l|$metric"] }
        $newest = $cell["$last|$metric"]
        $lines.Add("| $($tracked[$metric]) | $(Format-Metric $metric $hostValue) | $(Format-Metric $metric $linuxValue) | " +
                   ($cells -join ' | ') + " | $(Get-Ratio $newest $host_ $metric) | $(Get-Ratio $newest $linux_ $metric) |")
    }

    $begin = '<!-- perf-table:begin -->'
    $end = '<!-- perf-table:end -->'
    $doc = if (Test-Path -LiteralPath $Progress) { Get-Content -Raw -LiteralPath $Progress } else { '' }
    $table = $begin + "`n" + ($lines -join "`n") + "`n" + $end
    $i = $doc.IndexOf($begin)
    $j = $doc.IndexOf($end)
    if ($i -ge 0 -and $j -gt $i) {
        $doc = $doc.Substring(0, $i) + $table + $doc.Substring($j + $end.Length)
    } else {
        $doc = $doc.TrimEnd() + "`n`n" + $table + "`n"
    }
    [System.IO.File]::WriteAllText($Progress, $doc, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "progress -- $Label recorded; table in $Progress" -ForegroundColor DarkGray
}
