# tools/probe_report.ps1
# ----------------------------------------------------------------------------
# Extract probe / launcher statuses from a SharpOS boot log.
#
# Usage:
#   pwsh tools/probe_report.ps1 -Log path\to\boot.log
#   pwsh tools/probe_report.ps1 -Log boot.log -Compact
#
# For every known probe (kernel, GC, EH L1..L17, drivers, threading, CoreCLR,
# ExitBootServices, PAL/OS census, per-app launcher) the script emits one
# line: name | status | detail. Statuses:
#   OK       expected outcome matched
#   FAIL     start present + status present but mismatch
#   VALUE    captured a value but no gold reference (informational)
#   HALT     start seen but no terminating status (probe didn't complete)
#   UNKNOWN  no trace in log
# ----------------------------------------------------------------------------
# ASCII-only by policy: avoids Windows PowerShell 5 codepage decode issues
# when the .ps1 has no UTF-8 BOM (em-dashes / box-drawing chars confuse the
# parser on RU/CP1251 hosts). PS7 (pwsh) handles UTF-8 fine either way.

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Log,
    [switch]$Compact
)

if (-not (Test-Path -LiteralPath $Log)) {
    Write-Error "Log file not found: $Log"
    exit 1
}

$text = Get-Content -Raw -LiteralPath $Log

# --- helpers ---------------------------------------------------------

function Find-First ([string]$pattern) {
    $m = [regex]::Match($text, $pattern, 'Multiline')
    if ($m.Success) { return $m }
    return $null
}

function Find-All ([string]$pattern) {
    return [regex]::Matches($text, $pattern, 'Multiline')
}

function Get-ProbeStatus {
    param(
        [string]$Cat,
        [string]$Name,
        [string]$Detect,
        [string]$Status,
        [string]$Expect,
        [string]$ExpectRe,
        [int]$Group = 1
    )
    $det = if ($Detect) { Find-First $Detect } else { $null }
    $st  = if ($Status) { Find-First $Status } else { $null }
    if (-not $det -and -not $st) {
        return [PSCustomObject]@{ Cat=$Cat; Name=$Name; Status='UNKNOWN'; Detail='' }
    }
    if ($det -and -not $st -and $Status) {
        return [PSCustomObject]@{ Cat=$Cat; Name=$Name; Status='HALT'; Detail='started but no terminator' }
    }
    if ($st) {
        $val = if ($st.Groups.Count -gt $Group) { $st.Groups[$Group].Value } else { '' }
        if ($Expect) {
            if ($val -eq $Expect) {
                return [PSCustomObject]@{ Cat=$Cat; Name=$Name; Status='OK'; Detail="val=$val" }
            } else {
                return [PSCustomObject]@{ Cat=$Cat; Name=$Name; Status='FAIL'; Detail="val=$val expected=$Expect" }
            }
        }
        if ($ExpectRe) {
            if ($val -match $ExpectRe) {
                return [PSCustomObject]@{ Cat=$Cat; Name=$Name; Status='OK'; Detail=$val }
            } else {
                return [PSCustomObject]@{ Cat=$Cat; Name=$Name; Status='FAIL'; Detail=$val }
            }
        }
        return [PSCustomObject]@{ Cat=$Cat; Name=$Name; Status='VALUE'; Detail=$val }
    }
    if ($det -and -not $Status) {
        return [PSCustomObject]@{ Cat=$Cat; Name=$Name; Status='OK'; Detail='present' }
    }
    return [PSCustomObject]@{ Cat=$Cat; Name=$Name; Status='UNKNOWN'; Detail='' }
}

# --- probe map -------------------------------------------------------

$results = @()

# Phase 1 -- kernel heap (no explicit PASS line; we accept presence of
# a successful 16-byte alloc as evidence).
$results += Get-ProbeStatus -Cat 'Phase1' -Name 'KernelHeapSmoke' `
    -Detect 'heap alloc 16 -> 0x[0-9A-Fa-f]+'

# Phase 2 -- GC statics summary (count = entries materialised).
$results += Get-ProbeStatus -Cat 'Phase2' -Name 'GcStaticsSummary' `
    -Detect 'gcstatics-summary: entries=' `
    -Status 'gcstatics-summary: entries=(\d+)'

# Phase 3 -- RTC CMOS snapshot.
$results += Get-ProbeStatus -Cat 'Phase3' -Name 'RtcSnapshot' `
    -Detect 'rtc: ' `
    -Status 'rtc:\s*([^\r\n]+)'

# Phase 4 -- diagnostics + EH gradient.
$results += Get-ProbeStatus -Cat 'Phase4' -Name 'GcHeapSmoke' `
    -Detect 'gc heap test begin' `
    -Status 'gc heap test (end)' `
    -Expect 'end'

$results += Get-ProbeStatus -Cat 'Phase4' -Name 'GcStress' `
    -Detect 'gc stress test begin' `
    -Status 'gc stress test (end)' `
    -Expect 'end'

$results += Get-ProbeStatus -Cat 'Phase4' -Name 'NativeAotFeatures' `
    -Detect 'nativeaot probe begin' `
    -Status 'nativeaot probe (end)' `
    -Expect 'end'

$results += Get-ProbeStatus -Cat 'Phase4' -Name 'CreateSpanIntrinsic' `
    -Detect 'nativeaot probe begin' `
    -Status 'RuntimeHelpers\.CreateSpan: (ok|FAIL)' `
    -ExpectRe '^ok$'

$results += Get-ProbeStatus -Cat 'Phase4' -Name 'IcedEncode' `
    -Detect 'nativeaot probe begin' `
    -Status 'iced\.encode\(mov rax,rcx\): (ok|FAIL)' `
    -ExpectRe '^ok$'

$results += Get-ProbeStatus -Cat 'Phase4' -Name 'Cctor' `
    -Detect 'cctor implicit-int-field' `
    -Status 'cctor ref-field repeat: (ok|FAIL)' `
    -ExpectRe '^ok$'

# Enum coverage split into three orthogonal probes so the report shows
# real coverage per surface (см. NativeAotProbe.cs — Probe_Enum). Today:
# cast + bitwise green, toString RED (Enum stub in MinimalRuntime.cs is
# empty). When std/no-runtime ports Enum, toString flips to OK without
# any changes here.
$results += Get-ProbeStatus -Cat 'Phase4' -Name 'EnumCast' `
    -Detect 'nativeaot probe begin' `
    -Status 'enum\.cast: (ok|FAIL)' `
    -ExpectRe '^ok$'

$results += Get-ProbeStatus -Cat 'Phase4' -Name 'EnumBitwise' `
    -Detect 'nativeaot probe begin' `
    -Status 'enum\.bitwise: (ok|FAIL)' `
    -ExpectRe '^ok$'

# toString — honest FAIL today (Enum stub in MinimalRuntime.cs has no
# ToString override, falls through to object.ToString → returns type
# name, not "B"). Gate expects 'ok' like everyone else: it'll show
# straight FAIL now, and flip to green the day std/no-runtime ports
# Enum's member-name resolution. No "expected FAIL" trick — that
# rendered as ambiguous "OK (FAIL)".
$results += Get-ProbeStatus -Cat 'Phase4' -Name 'EnumToString' `
    -Detect 'nativeaot probe begin' `
    -Status 'enum\.toString: (ok|FAIL)' `
    -ExpectRe '^ok$'

# Phase 4 -- EH level gates.
$ehGates = @(
    @{ N='EhTryFinallyNoThrow';    Re='eh L1 try/finally no-throw: val=(\d+)';       Expect='211'  },
    @{ N='EhTryCatchNoThrow';      Re='eh L2 try/catch no-throw: val=(\d+)';         Expect='4'    },
    @{ N='EhExceptionShape';       Re='eh L4 exception shape: val=(\d+)';            Expect='127'  },
    @{ N='EhRootWalk';              Re='eh L5 \.pdata \+ root walk: val=(\d+)';      Expect='7'    },
    @{ N='EhDecode';                Re='eh L6 ehInfo varint decode: val=(\d+)';      Expect='111'  },
    @{ N='EhFrameWalk';              Re='eh L7 frame walk: val=(\d+)';               Expect='3'    },
    @{ N='EhEnumLive';               Re='eh 5\.3 enum-live: val=(\d+)';              Expect='15'   },
    @{ N='EhRealDispatch';           Re='eh L8 typed catch \(real dispatch\): val=(\d+)';  Expect='801'  },
    @{ N='EhRethrowChain';           Re='eh L9 rethrow chain: val=(\d+)';            Expect='901'  },
    @{ N='EhTryCatchFinally';        Re='eh L10 finally \+ catch: val=(\d+)';        Expect='111'  },
    @{ N='EhFilter';                 Re='eh L11 catch-when filter: val=(\d+)';       Expect='1101' },
    @{ N='EhHwFault';                Re='eh L13 hw fault \(null deref\): val=(\d+)'; Expect='3'    },
    @{ N='EhStackTrace';             Re='eh L14 stack trace populated: val=(\d+)';   Expect='1401' },
    @{ N='EhCollidedUnwind';         Re='eh L15 collided unwind: val=(\d+)';         Expect='1501' },
    @{ N='EhMultiFrameFinally';      Re='eh L16 multi-frame finally: val=(\d+)';     Expect='1616' },
    @{ N='EhMultiFrameStackTrace';   Re='eh L17 multi-frame stack trace: val=(\d+)'; ExpectRe='^17\d\d$' }
)
foreach ($g in $ehGates) {
    $splat = @{ Cat='EH'; Name=$g.N; Detect=$g.Re; Status=$g.Re }
    if ($g.Expect)    { $splat.Expect    = $g.Expect }
    if ($g.ExpectRe)  { $splat.ExpectRe  = $g.ExpectRe }
    $results += Get-ProbeStatus @splat
}

# Phase E -- TebFacade / Atomics / Ping-pong.
$results += Get-ProbeStatus -Cat 'PhaseE' -Name 'TebFacadeSwap' `
    -Detect 'TEB facade probe start' `
    -Status 'TEB facade probe: swap (Self=ok Limit=ok)' `
    -ExpectRe 'Self=ok Limit=ok'

$results += Get-ProbeStatus -Cat 'PhaseE' -Name 'Atomics' `
    -Detect 'atomics probe start' `
    -Status 'atomics probe: (CmpXchg-hit=ok CmpXchg-miss=ok Xchg=ok MFence=ok)' `
    -ExpectRe 'CmpXchg-hit=ok CmpXchg-miss=ok Xchg=ok MFence=ok'

$results += Get-ProbeStatus -Cat 'PhaseE' -Name 'ThreadPingPong' `
    -Detect 'ping-pong probe start' `
    -Status 'ping-pong probe: T1=(\d+/\d+) T2=\d+/\d+.*?(ok|FAIL)' `
    -ExpectRe '^5/5$' `
    -Group 1

$results += Get-ProbeStatus -Cat 'PhaseE' -Name 'ThreadPingPong.Verdict' `
    -Detect 'ping-pong probe: T1=' `
    -Status 'ping-pong probe:.*?(ok|FAIL)\b' `
    -ExpectRe '^ok$'

$results += Get-ProbeStatus -Cat 'PhaseE' -Name 'ThreadSleep' `
    -Detect 'sleep probe start' `
    -Status 'sleep probe: pass=(\d+)/3 fail=0\s+--\s+(ok|FAIL)' `
    -ExpectRe '^ok$' `
    -Group 2

$results += Get-ProbeStatus -Cat 'PhaseE' -Name 'ThreadEvent' `
    -Detect 'event probe start' `
    -Status 'event probe: latency=(-?\d+) ms.*?(ok|FAIL)' `
    -ExpectRe '^ok$' `
    -Group 2

$results += Get-ProbeStatus -Cat 'PhaseE' -Name 'ThreadSemaphore' `
    -Detect 'semaphore probe start' `
    -Status 'semaphore probe:.*?residualCount=\d+\s+--\s+(ok|FAIL)' `
    -ExpectRe '^ok$'

$results += Get-ProbeStatus -Cat 'PhaseE' -Name 'AllocStress' `
    -Detect 'alloc stress probe start' `
    -Status 'alloc stress probe:[\s\S]{0,500}?corruption=\d+[\s\S]{0,200}?--\s+(ok|FAIL)' `
    -ExpectRe '^ok$'

$results += Get-ProbeStatus -Cat 'PhaseE' -Name 'ProcessSpawn' `
    -Detect 'process probe start' `
    -Status 'process probe:[\s\S]{0,500}?--\s+(ok|FAIL)' `
    -ExpectRe '^ok$'

# Drivers (Phase 4 sub).
$results += Get-ProbeStatus -Cat 'Drivers' -Name 'SerialSmoke' `
    -Detect '\[serial\] direct-UART line via own 16550' `
    -Status '\[serial\] direct-UART line via own 16550 driver - Phase B (OK)' `
    -Expect 'OK'

$results += Get-ProbeStatus -Cat 'Drivers' -Name 'FbRender' `
    -Detect '\[fbtext\] ' `
    -Status '\[fbtext\][^\r\n]*\s(PASS|FAIL)\b' `
    -ExpectRe '^PASS$'

$results += Get-ProbeStatus -Cat 'Drivers' -Name 'Ps2' `
    -Detect '\[ps2\] status=' `
    -Status '\[ps2\][^\r\n]*\s(PASS|FAIL)' `
    -ExpectRe '^PASS$'

$results += Get-ProbeStatus -Cat 'Drivers' -Name 'LineEdit' `
    -Detect '\[lined\] ' `
    -Status '\[lined\][^\r\n]*\s(PASS|FAIL)' `
    -ExpectRe '^PASS$'

$results += Get-ProbeStatus -Cat 'Drivers' -Name 'ShellEngine' `
    -Detect '\[shell\] ' `
    -Status '\[shell\][^\r\n]*\s(PASS|FAIL)' `
    -ExpectRe '^PASS$'

$results += Get-ProbeStatus -Cat 'Drivers' -Name 'PciScan' `
    -Detect '\[pci\] devs=' `
    -Status '\[pci\][^\r\n]*\s(PASS|FAIL)' `
    -ExpectRe '^PASS$'

# CoreCLR.
$results += Get-ProbeStatus -Cat 'CoreCLR' -Name 'coreclr_initialize' `
    -Detect 'coreclr_initialize hr=' `
    -Status 'coreclr_initialize hr=(0x[0-9A-Fa-f]+)' `
    -Expect '0x0'

$results += Get-ProbeStatus -Cat 'CoreCLR' -Name 'execute_assembly' `
    -Detect 'execute_assembly hr=' `
    -Status 'execute_assembly hr=(0x[0-9A-Fa-f]+) exitCode=(\d+)' `
    -Expect '0x0' `
    -Group 1

$results += Get-ProbeStatus -Cat 'CoreCLR' -Name 'execute_assembly.exitCode' `
    -Detect 'execute_assembly hr=' `
    -Status 'execute_assembly hr=0x[0-9A-Fa-f]+ exitCode=(\d+)' `
    -ExpectRe '^\d+$'

# ExitBootServices -- Phase C survival.
$results += Get-ProbeStatus -Cat 'EBS' -Name 'ExitBootServicesProbe' `
    -Detect '\[ebs\] ExitBootServices OK' `
    -Status '\[ebs\] (ExitBootServices OK -- POST-EBS substrate LIVE)' `
    -ExpectRe '^ExitBootServices OK'

$results += Get-ProbeStatus -Cat 'EBS' -Name 'PostEbsConsoleReroute' `
    -Detect '\[ebs\] console rerouted to own UART\+FbTty'

# PAL/OS census aggregate -- show all three counters as one detail.
$results += Get-ProbeStatus -Cat 'CoreCLR' -Name 'PAL/OS census' `
    -Detect 'PAL/OS census end:' `
    -Status 'PAL/OS census end:\s*(OK=\d+\s+DEG=\d+\s+FAIL=\d+)' `
    -ExpectRe '.'

# Phase E1 -- pager root activation + XCR0 lock.
$results += Get-ProbeStatus -Cat 'Boot' -Name 'pagerRootActivated' `
    -Detect 'pager root activated' `
    -Status 'pager root activated \((clone CR3 live)\)' `
    -ExpectRe 'clone CR3 live'

$results += Get-ProbeStatus -Cat 'Boot' -Name 'xcr0Lock' `
    -Detect 'XCR0 ' `
    -Status 'XCR0 (?:= (x87\|SSE locked)|lock skipped \((.+)\))' `
    -Group 1

# Boot phase markers.
$results += Get-ProbeStatus -Cat 'Boot' -Name 'idtInstalled' `
    -Detect 'idt installed'
$results += Get-ProbeStatus -Cat 'Boot' -Name 'heapInit' `
    -Detect 'heap init ok'
$results += Get-ProbeStatus -Cat 'Boot' -Name 'pagerInit' `
    -Detect 'pager init ok'
$results += Get-ProbeStatus -Cat 'Boot' -Name 'pagerValidation' `
    -Detect 'pager validation done' `
    -Status 'pager validation (done)' `
    -ExpectRe '^done$'
$results += Get-ProbeStatus -Cat 'Boot' -Name 'vmSelfTest' `
    -Detect 'VM manager self-test' `
    -Status 'VM manager self-test (ok|failed)' `
    -Expect 'ok'

# --- launcher / per-app ---------------------------------------------
# "app run start: <path>" begins; "process exit code = N" + "exit source = X"
# defines ok; "app failed: <path> reason=X" is FAIL.
$launchStarts = Find-All 'app run start:\s+([^\r\n]+)'
$appResults = @()
foreach ($m in $launchStarts) {
    $path = $m.Groups[1].Value.Trim()
    $after = $text.Substring($m.Index)
    $exit = [regex]::Match($after, 'app run start:[^\n]*\n.*?process exit code = (-?\d+)', 'Singleline')
    $failM = [regex]::Match($after, 'app run start:[^\n]*\n.*?app failed:[^\n]*reason=(\w+)', 'Singleline')
    $isExit = $exit.Success
    $isFail = $failM.Success
    if ($isExit -and $isFail) {
        if ($exit.Index -lt $failM.Index) { $isFail = $false } else { $isExit = $false }
    }
    if ($isExit) {
        $appResults += [PSCustomObject]@{ Cat='Launcher'; Name=$path; Status='OK';   Detail="exitCode=$($exit.Groups[1].Value)" }
    } elseif ($isFail) {
        $appResults += [PSCustomObject]@{ Cat='Launcher'; Name=$path; Status='FAIL'; Detail="reason=$($failM.Groups[1].Value)" }
    } else {
        $appResults += [PSCustomObject]@{ Cat='Launcher'; Name=$path; Status='HALT'; Detail='started but no exit/fail trailer' }
    }
}
$results += $appResults

# --- fault detector -------------------------------------------------
$hw = Find-All '\bHW fault:\s+vec=0x([0-9A-Fa-f]+)'
$unhandled = Find-All '\*\*\* unhandled exception'
$panics = Find-All '\*\*\* halting'
if ($hw.Count -gt 0 -or $unhandled.Count -gt 0 -or $panics.Count -gt 0) {
    $hwStatus = if ($hw.Count -eq 0) { 'none' } else { "$($hw.Count) seen" }
    $hwDetail = ($hw | ForEach-Object { "vec=0x$($_.Groups[1].Value)" } | Select-Object -First 5) -join ', '
    $results += [PSCustomObject]@{ Cat='Faults'; Name='HW faults'; Status=$hwStatus; Detail=$hwDetail }

    $unhStatus = if ($unhandled.Count -eq 0) { 'none' } else { "$($unhandled.Count) seen" }
    $results += [PSCustomObject]@{ Cat='Faults'; Name='unhandled exceptions'; Status=$unhStatus; Detail='' }

    $haltStatus = if ($panics.Count -eq 0) { 'none' } else { "$($panics.Count) seen" }
    $results += [PSCustomObject]@{ Cat='Faults'; Name='halts'; Status=$haltStatus; Detail='' }
}

# --- render ---------------------------------------------------------
$counts = @{ OK=0; FAIL=0; VALUE=0; HALT=0; UNKNOWN=0; Other=0 }

$colors = @{
    OK      = 'Green'
    FAIL    = 'Red'
    HALT    = 'Yellow'
    UNKNOWN = 'DarkGray'
    VALUE   = 'Cyan'
}

$catsOrder = @('Boot','Phase1','Phase2','Phase3','Phase4','EH','PhaseE','Drivers','CoreCLR','EBS','Launcher','Faults')

Write-Host ""
Write-Host "=== SharpOS probe report -- $Log ===" -ForegroundColor White
Write-Host ""

foreach ($cat in $catsOrder) {
    $rows = $results | Where-Object { $_.Cat -eq $cat }
    if (-not $rows) { continue }
    Write-Host "[$cat]" -ForegroundColor White
    foreach ($r in $rows) {
        if ($counts.ContainsKey($r.Status)) { $counts[$r.Status]++ } else { $counts.Other++ }
        $statusToken = $r.Status
        $color = if ($colors.ContainsKey($statusToken)) { $colors[$statusToken] } else { 'Gray' }
        $padName = $r.Name.PadRight(34, '.')
        if ($Compact -and $statusToken -eq 'UNKNOWN') { continue }
        $line = '  ' + $padName + ' ' + $statusToken
        if ($r.Detail) { $line += '  (' + $r.Detail + ')' }
        Write-Host $line -ForegroundColor $color
    }
    Write-Host ""
}

Write-Host "--- totals ---" -ForegroundColor White
foreach ($k in 'OK','VALUE','FAIL','HALT','UNKNOWN','Other') {
    $cnt = $counts[$k]
    if ($cnt -gt 0) {
        $color = if ($colors.ContainsKey($k)) { $colors[$k] } else { 'Gray' }
        $totalLine = '  ' + $k.PadRight(8) + ' ' + $cnt
        Write-Host $totalLine -ForegroundColor $color
    }
}
Write-Host ""

# Exit code: 0 if no FAIL/HALT, 1 otherwise. Friendly for CI gating.
if ($counts.FAIL -gt 0 -or $counts.HALT -gt 0) { exit 1 } else { exit 0 }
