# tools/watch_vbox_stop.ps1
# ---------------------------------------------------------------------------
# Watch a VirtualBox serial log and pause the VM before a known crash point.
# After pausing, optionally capture registers and a VirtualBox VM core dump.
#
# Default trigger is intentionally one probe before the current VBox-only
# WorkingSet64 crash, so the VM is paused before Guru Meditation/poweroff.
#
# Usage:
#   pwsh tools/watch_vbox_stop.ps1
#   pwsh tools/watch_vbox_stop.ps1 -TriggerRegex 'Process\.\.\.WorkingSet64'
#   pwsh tools/watch_vbox_stop.ps1 -TriggerRegex 'managed throw deep stack.*\[DEG\]' -NoDumpCore
#   pwsh tools/watch_vbox_stop.ps1 -WatchMxcsr -TriggerRegex 'Process\.\.\.WorkingSet64'
#   pwsh tools/watch_vbox_stop.ps1 -ScanExisting -TimeoutSec 120
# ---------------------------------------------------------------------------

[CmdletBinding()]
param(
    [string]$Vm = "SharpOS",
    [string]$SerialLog,
    [string[]]$TriggerRegex = @('Process\.\.\.ProcessName\s+\[OK\]'),
    [string]$VBoxManage,
    [string]$OutDir,
    [int]$PollMs = 25,
    [int]$TimeoutSec = 0,
    [int]$ActionDelayMs = 0,
    [switch]$WatchMxcsr,
    [string]$MxcsrRequiredMask = "0x1F80",
    [int]$MxcsrPollMs = 100,
    [switch]$ScanExisting,
    [switch]$StartVm,
    [switch]$NoDumpCore,
    [switch]$ResumeAfterDump,
    [string[]]$Registers = @(
        "mxcsr", "rip", "rsp", "rbp",
        "rax", "rbx", "rcx", "rdx", "rsi", "rdi",
        "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15",
        "cr2", "cr3", "cr4"
    )
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($SerialLog)) {
    $SerialLog = Join-Path $repoRoot "OS\.qemu\media\vbox-serial.log"
}
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $repoRoot ".vbox\stops"
}

function Resolve-VBoxManage {
    param([string]$Explicit)

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        $full = [System.IO.Path]::GetFullPath($Explicit)
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw "VBoxManage not found: $full"
        }
        return $full
    }

    $default = "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"
    if (Test-Path -LiteralPath $default -PathType Leaf) {
        return $default
    }

    $cmd = Get-Command VBoxManage.exe -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) {
        return $cmd.Source
    }

    throw "VBoxManage.exe not found. Pass -VBoxManage <path>."
}

function Invoke-VBox {
    param(
        [Parameter(Mandatory=$true)][string]$Exe,
        [Parameter(Mandatory=$true)][string[]]$Args,
        [string]$OutFile
    )

    Write-Host ("+ {0} {1}" -f $Exe, ($Args -join " "))
    $output = & $Exe @Args 2>&1
    $exit = $LASTEXITCODE
    if (-not [string]::IsNullOrWhiteSpace($OutFile)) {
        $output | Out-File -LiteralPath $OutFile -Encoding utf8
    }
    if ($output) {
        $output | ForEach-Object { Write-Host $_ }
    }
    if ($exit -ne 0) {
        throw "VBoxManage failed with exit code $exit"
    }
    return $output
}

function Get-FileLengthOrZero {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [Int64]0
    }
    return (Get-Item -LiteralPath $Path).Length
}

function Read-NewText {
    param(
        [string]$Path,
        [ref]$Offset
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return ""
    }

    $info = Get-Item -LiteralPath $Path
    if ($info.Length -lt $Offset.Value) {
        $Offset.Value = 0
    }
    if ($info.Length -eq $Offset.Value) {
        return ""
    }

    $fs = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
    try {
        [void]$fs.Seek($Offset.Value, [System.IO.SeekOrigin]::Begin)
        $count = [int]($info.Length - $Offset.Value)
        $buffer = New-Object byte[] $count
        $read = $fs.Read($buffer, 0, $count)
        $Offset.Value += $read
        if ($read -le 0) {
            return ""
        }
        return [System.Text.Encoding]::UTF8.GetString($buffer, 0, $read)
    }
    finally {
        $fs.Dispose()
    }
}

function Test-LineMatch {
    param(
        [string]$Line,
        [string[]]$Patterns
    )

    foreach ($pattern in $Patterns) {
        if ($Line -match $pattern) {
            return $pattern
        }
    }
    return $null
}

function Parse-Num {
    param([string]$Text)

    $trimmed = $Text.Trim()
    if ($trimmed.StartsWith("0x") -or $trimmed.StartsWith("0X")) {
        return [UInt64]::Parse($trimmed.Substring(2), [Globalization.NumberStyles]::AllowHexSpecifier)
    }
    return [Convert]::ToUInt64($trimmed, 10)
}

function Try-ReadMxcsr {
    param(
        [string]$Exe,
        [string]$VmName
    )

    $output = & $Exe debugvm $VmName getregisters --cpu=0 mxcsr 2>&1
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    $text = [string]::Join("`n", $output)
    if ($text -match 'mxcsr\s*=\s*(0x[0-9A-Fa-f]+|\d+)') {
        return [pscustomobject]@{
            Value = [UInt64](Parse-Num $Matches[1])
            Text = $text
        }
    }

    return $null
}

function Stop-And-Capture {
    param(
        [string]$Exe,
        [string]$VmName,
        [string]$Reason,
        [string]$MatchedLine
    )

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $prefix = Join-Path $OutDir ("vbox-stop-{0}" -f $stamp)
    $reasonFile = "$prefix.reason.txt"
    $showFile = "$prefix.showvminfo.txt"
    $regsFile = "$prefix.registers.txt"
    $coreFile = "$prefix.elf"

    @(
        "time=$(Get-Date -Format o)"
        "vm=$VmName"
        "serial_log=$SerialLog"
        "matched_regex=$Reason"
        "matched_line=$MatchedLine"
    ) | Out-File -LiteralPath $reasonFile -Encoding utf8

    if ($ActionDelayMs -gt 0) {
        Start-Sleep -Milliseconds $ActionDelayMs
    }

    Invoke-VBox -Exe $Exe -Args @("controlvm", $VmName, "pause") | Out-Null

    try {
        Invoke-VBox -Exe $Exe -Args @("showvminfo", $VmName, "--machinereadable") -OutFile $showFile | Out-Null
    }
    catch {
        Write-Warning "showvminfo failed after pause: $_"
    }

    try {
        $regArgs = @("debugvm", $VmName, "getregisters", "--cpu=0") + $Registers
        Invoke-VBox -Exe $Exe -Args $regArgs -OutFile $regsFile | Out-Null
    }
    catch {
        Write-Warning "getregisters failed after pause: $_"
    }

    if (-not $NoDumpCore) {
        try {
            Invoke-VBox -Exe $Exe -Args @("debugvm", $VmName, "dumpvmcore", "--filename=$coreFile") | Out-Null
            Write-Host "VM core: $coreFile"
        }
        catch {
            Write-Warning "dumpvmcore failed after pause: $_"
        }
    }

    if ($ResumeAfterDump) {
        Invoke-VBox -Exe $Exe -Args @("controlvm", $VmName, "resume") | Out-Null
    }

    Write-Host "Reason: $reasonFile"
    Write-Host "Registers: $regsFile"
    Write-Host "VM left paused: $(-not $ResumeAfterDump)"
}

$VBoxManage = Resolve-VBoxManage $VBoxManage
$SerialLog = [System.IO.Path]::GetFullPath($SerialLog)
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$mxcsrMask = [UInt64](Parse-Num $MxcsrRequiredMask)

if ($StartVm) {
    Invoke-VBox -Exe $VBoxManage -Args @("startvm", $Vm) | Out-Null
}

$offset = [Int64]0
if (-not $ScanExisting) {
    $offset = Get-FileLengthOrZero $SerialLog
}

Write-Host "VM: $Vm"
Write-Host "Serial log: $SerialLog"
Write-Host "Start offset: $offset"
Write-Host "Trigger regex:"
$TriggerRegex | ForEach-Object { Write-Host "  $_" }
if ($WatchMxcsr) {
    Write-Host ("MXCSR watch: required mask 0x{0:X}; poll {1} ms" -f $mxcsrMask, $MxcsrPollMs)
}
Write-Host "Output dir: $OutDir"
Write-Host "Waiting..."

$start = Get-Date
$pending = ""
$lastMxcsrPoll = [DateTime]::MinValue

while ($true) {
    if ($TimeoutSec -gt 0 -and ((Get-Date) - $start).TotalSeconds -ge $TimeoutSec) {
        throw "Timed out after $TimeoutSec seconds without trigger."
    }

    $newText = Read-NewText -Path $SerialLog -Offset ([ref]$offset)
    if ($newText.Length -gt 0) {
        $pending += $newText
        $normalized = $pending.Replace("`r`n", "`n").Replace("`r", "`n")
        $parts = $normalized.Split([char]10)
        if ($normalized.EndsWith("`n")) {
            $pending = ""
            if ($parts.Count -gt 1) {
                $lines = $parts[0..($parts.Count - 2)]
            }
            else {
                $lines = @()
            }
        }
        else {
            $pending = $parts[$parts.Count - 1]
            if ($parts.Count -gt 1) {
                $lines = $parts[0..($parts.Count - 2)]
            }
            else {
                $lines = @()
            }
        }

        foreach ($line in $lines) {
            $pattern = Test-LineMatch -Line $line -Patterns $TriggerRegex
            if ($pattern) {
                Write-Host "Matched: $line"
                Stop-And-Capture -Exe $VBoxManage -VmName $Vm -Reason $pattern -MatchedLine $line
                exit 0
            }
        }
    }

    if ($pending.Length -gt 0) {
        $pattern = Test-LineMatch -Line $pending -Patterns $TriggerRegex
        if ($pattern) {
            Write-Host "Matched partial line: $pending"
            Stop-And-Capture -Exe $VBoxManage -VmName $Vm -Reason $pattern -MatchedLine $pending
            exit 0
        }
    }

    if ($WatchMxcsr -and ((Get-Date) - $lastMxcsrPoll).TotalMilliseconds -ge $MxcsrPollMs) {
        $lastMxcsrPoll = Get-Date
        $mx = Try-ReadMxcsr -Exe $VBoxManage -VmName $Vm
        if ($mx -ne $null -and (($mx.Value -band $mxcsrMask) -ne $mxcsrMask)) {
            $line = ("mxcsr = 0x{0:X8}; required mask = 0x{1:X8}" -f $mx.Value, $mxcsrMask)
            Write-Host "Matched MXCSR: $line"
            Stop-And-Capture -Exe $VBoxManage -VmName $Vm -Reason "mxcsr-mask" -MatchedLine $line
            exit 0
        }
    }

    Start-Sleep -Milliseconds $PollMs
}
