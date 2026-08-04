# tools/run_qemu_gdbtrap.ps1
# ---------------------------------------------------------------------------
# Launch run_build.ps1, tee its output to a log, enable QEMU's gdbstub through
# QMP, and run GDB with a configurable trap.
#
# Default trap:
#   break at CallDescrWorkerInternalReturnAddress (imageBase + 0x58BBB3)
#   only when RBX is zero, then stop in GDB with useful register/stack dumps.
#
# Usage:
#   $env:SHARPOS_GUI=1
#   pwsh tools/run_qemu_gdbtrap.ps1 -ForkConfig Release
#
# Wrapper options are prefixed with -Trap/-Gdb/-Log. Unknown options are passed
# to run_build.ps1, so the common run_build arguments can be used directly:
#   pwsh tools/run_qemu_gdbtrap.ps1 -ForkConfig Release -Configuration Release
#
# Fully custom GDB template:
#   pwsh tools/run_qemu_gdbtrap.ps1 -TrapGdbScript tools/mytrap.gdb -- -ForkConfig Release
#
# Supported template placeholders:
#   {{GDB_PORT}}, {{QMP_PORT}}, {{LOG_PATH}}, {{IMAGE_BASE}},
#   {{BREAK_ADDR}}, {{BREAK_RVA}}, {{TRAP_NAME}}
# ---------------------------------------------------------------------------

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")

$settings = [ordered]@{
    RunScript           = (Join-Path $repoRoot "run_build.ps1")
    LogPath             = (Join-Path $repoRoot "last_build.log")
    QmpPort             = 4444
    GdbPort             = 1234
    GdbExe              = $null
    TimeoutSec          = 120
    TrapName            = "calldescr-rbx-zero"
    TrapBreakRva        = "0x58BBB3"
    TrapBreakAddress    = $null
    TrapCondition       = '$rbx == 0'
    TrapGdbScript       = $null
    TrapOnHitFile       = $null
    TrapOutDir          = (Join-Path $repoRoot ".qemu\gdbtrap")
    ImageBase           = $null
    ImageBaseRegex      = 'imageBase=0x([0-9A-Fa-f]+)'
    DryRun              = $false
    NoStopBeforeGdb     = $false
    NoAutoContinue      = $false
    NoWaitForRunner     = $false
    Gui                 = $false
}

function Show-Usage {
    Write-Host @"
Usage:
  pwsh tools/run_qemu_gdbtrap.ps1 [trap options] [run_build.ps1 options]
  pwsh tools/run_qemu_gdbtrap.ps1 [trap options] -- [run_build.ps1 options]

Common:
  pwsh tools/run_qemu_gdbtrap.ps1 -ForkConfig Release
  pwsh tools/run_qemu_gdbtrap.ps1 -TrapBreakRva 0x58BBB3 -TrapCondition '`$rbx == 0' -ForkConfig Release
  pwsh tools/run_qemu_gdbtrap.ps1 -TrapBreakAddress 0x7D271BB3 -TrapCondition '`$rbx == 0' -- -ForkConfig Release
  pwsh tools/run_qemu_gdbtrap.ps1 -TrapBreakAddress "0x50000D5C5480,0x50000D5C54A4" -- -ForkConfig Release

Trap options:
  -LogPath <path>             Combined run_build/QEMU log. Default: last_build.log
  -QmpPort <port>             QMP port; also passed to run_build.ps1. Default: 4444
  -GdbPort <port>             QEMU gdbserver port. Default: 1234
  -GdbExe <path>              gdb.exe path. Default: auto-resolve
  -TrapBreakRva <hex[,hex]>   Breakpoint RVA(s) relative to kernel imageBase
  -TrapBreakAddress <hex[,hex]>
                              Absolute breakpoint address(es); overrides -TrapBreakRva
  -TrapCondition <gdb expr>   GDB condition for the breakpoint
  -TrapOnHitFile <path>       GDB commands for the breakpoint commands block
  -TrapGdbScript <path>       Full GDB script/template; replaces generated trap
  -ImageBase <hex>            Skip log wait and use this kernel image base
  -TrapDryRun                 Generate scripts and print commands without launching
  -NoStopBeforeGdb            Do not QMP-stop before connecting GDB
  -NoAutoContinue             Leave GDB stopped after setting breakpoints
  -NoWaitForRunner            Do not wait for the run_build process after GDB exits
  -Gui                        Set SHARPOS_GUI=1 for the child run
"@
}

function Parse-Num([string] $text) {
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "empty numeric value"
    }

    $t = $text.Trim()
    if ($t.StartsWith("0x") -or $t.StartsWith("0X")) {
        return [UInt64]::Parse($t.Substring(2), [Globalization.NumberStyles]::AllowHexSpecifier)
    }

    return [Convert]::ToUInt64($t, 10)
}

function Parse-NumList([string] $text) {
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "empty numeric list"
    }

    $values = New-Object System.Collections.Generic.List[UInt64]
    foreach ($part in $text.Split(',')) {
        $trimmed = $part.Trim()
        if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
            $values.Add((Parse-Num $trimmed))
        }
    }

    if ($values.Count -eq 0) {
        throw "empty numeric list"
    }

    return [UInt64[]]$values.ToArray()
}

function Format-Hex64([UInt64] $value) {
    return "0x{0:X16}" -f $value
}

function Format-HexList([UInt64[]] $values) {
    return (($values | ForEach-Object { Format-Hex64 $_ }) -join ",")
}

function Quote-PowerShellLiteral([string] $value) {
    return "'" + $value.Replace("'", "''") + "'"
}

function Format-RunArgument([string] $value) {
    # Keep parameter names syntactic so PowerShell binds them to run_build.ps1.
    if ($value -match '^-[A-Za-z?][A-Za-z0-9_:-]*$') {
        return $value
    }

    return Quote-PowerShellLiteral $value
}

function Resolve-GdbExe {
    param([string] $Explicit)

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        if (-not (Test-Path -LiteralPath $Explicit)) {
            throw "GDB not found: $Explicit"
        }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }

    $cmd = Get-Command "gdb.exe" -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    foreach ($candidate in @(
        "C:\msys64\mingw64\bin\gdb.exe",
        "C:\msys64\ucrt64\bin\gdb.exe",
        "C:\Program Files\qemu\gdb.exe"
    )) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "Could not find gdb.exe. Pass -GdbExe <path>."
}

function Test-TcpPort {
    param(
        [string] $HostName,
        [int] $Port
    )

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $iar = $client.BeginConnect($HostName, $Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne(250)) {
            return $false
        }
        $client.EndConnect($iar)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Close()
    }
}

function Wait-TcpPort {
    param(
        [string] $HostName,
        [int] $Port,
        [int] $TimeoutSec
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-TcpPort -HostName $HostName -Port $Port) {
            return
        }
        Start-Sleep -Milliseconds 200
    }

    throw "Timed out waiting for $HostName`:$Port"
}

function Invoke-Qmp {
    param(
        [int] $Port,
        [hashtable] $Command
    )

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $client.Connect("127.0.0.1", $Port)
        $stream = $client.GetStream()
        $stream.ReadTimeout = 5000
        $reader = [System.IO.StreamReader]::new($stream)
        $writer = [System.IO.StreamWriter]::new($stream)
        $writer.AutoFlush = $true

        [void]$reader.ReadLine()
        $writer.WriteLine('{"execute":"qmp_capabilities"}')
        [void]$reader.ReadLine()

        $writer.WriteLine(($Command | ConvertTo-Json -Compress))
        $raw = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($raw)) {
            throw "empty QMP response"
        }

        return ($raw | ConvertFrom-Json)
    }
    finally {
        if ($reader) { $reader.Dispose() }
        if ($writer) { $writer.Dispose() }
        if ($stream) { $stream.Dispose() }
        $client.Close()
    }
}

function Invoke-Hmp {
    param(
        [int] $Port,
        [string] $Line
    )

    return Invoke-Qmp -Port $Port -Command @{
        execute = "human-monitor-command"
        arguments = @{ "command-line" = $Line }
    }
}

function Wait-ImageBase {
    param(
        [string] $LogPath,
        [string] $Regex,
        [int] $TimeoutSec
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $LogPath) {
            $matches = Select-String -LiteralPath $LogPath -Pattern $Regex -AllMatches -ErrorAction SilentlyContinue
            foreach ($matchInfo in $matches) {
                foreach ($match in $matchInfo.Matches) {
                    if ($match.Groups.Count -gt 1) {
                        return Parse-Num ("0x" + $match.Groups[1].Value)
                    }
                }
            }
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for imageBase in $LogPath"
}

function Expand-TrapTemplate {
    param(
        [string] $Template,
        [hashtable] $Values
    )

    $result = $Template
    foreach ($key in $Values.Keys) {
        $result = $result.Replace("{{" + $key + "}}", [string]$Values[$key])
    }
    return $result
}

function New-DefaultGdbScript {
    param(
        [string] $TrapName,
        [UInt64[]] $BreakAddresses,
        [UInt64[]] $BreakRvas,
        [string] $Condition,
        [int] $GdbPort,
        [string[]] $OnHitCommands,
        [bool] $AutoContinue
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("set pagination off")
    $lines.Add("set confirm off")
    $lines.Add("set height 0")
    $lines.Add("set width 0")
    $lines.Add("set disassembly-flavor intel")
    $lines.Add("set remotetimeout 60")
    $lines.Add("target remote 127.0.0.1:$GdbPort")
    $lines.Add("printf `"\n[trap] $TrapName breaks=$(Format-HexList $BreakAddresses) rvas=$(Format-HexList $BreakRvas)\n`"")

    foreach ($breakAddress in $BreakAddresses) {
        if ([string]::IsNullOrWhiteSpace($Condition)) {
            $lines.Add("break *$(Format-Hex64 $breakAddress)")
        }
        else {
            $lines.Add("break *$(Format-Hex64 $breakAddress) if $Condition")
        }

        $lines.Add("commands")
        foreach ($cmd in $OnHitCommands) {
            $lines.Add("  $cmd")
        }
        $lines.Add("end")
    }

    if ($AutoContinue) {
        $lines.Add("continue")
    }

    return ($lines -join [Environment]::NewLine) + [Environment]::NewLine
}

function New-DefaultOnHitCommands {
    $commands = @(
        "silent",
        'printf "\n[trap-hit] CallDescr return with corrupt nonvolatiles\n"',
        'printf "rip=%p rsp=%p rbp=%p rbx=%p rsi=%p rdi=%p r14=%p r15=%p\n", $rip,$rsp,$rbp,$rbx,$rsi,$rdi,$r14,$r15',
        'printf "rax=%p rcx=%p rdx=%p r8=%p r9=%p r10=%p r11=%p\n", $rax,$rcx,$rdx,$r8,$r9,$r10,$r11',
        'printf "\n[stack at rsp]\n"',
        'x/32gx $rsp',
        'printf "\n[CallDescr frame around rbp]\n"',
        'x/24gx $rbp-0x40',
        'printf "\n[innermost SosCallDescrLink candidate rbp+0x40]\n"',
        'x/12gx $rbp+0x40',
        'printf "\n[code]\n"',
        'x/12i $rip-24'
    )

    return $commands
}

$runArgs = New-Object System.Collections.Generic.List[string]
$afterSeparator = $false
for ($i = 0; $i -lt $args.Count; $i++) {
    $arg = [string]$args[$i]
    if ($arg -eq "--") {
        $afterSeparator = $true
        continue
    }

    if ($afterSeparator) {
        $runArgs.Add($arg)
        continue
    }

    switch -Regex ($arg) {
        '^-h$|^-Help$|^--help$' {
            Show-Usage
            exit 0
        }
        '^-RunScript$' {
            $settings.RunScript = $args[++$i]
            continue
        }
        '^-LogPath$' {
            $settings.LogPath = $args[++$i]
            continue
        }
        '^-QmpPort$' {
            $settings.QmpPort = [int]$args[++$i]
            $runArgs.Add($arg)
            $runArgs.Add([string]$settings.QmpPort)
            continue
        }
        '^-GdbPort$' {
            $settings.GdbPort = [int]$args[++$i]
            continue
        }
        '^-GdbExe$' {
            $settings.GdbExe = $args[++$i]
            continue
        }
        '^-TrapTimeoutSec$|^-TimeoutSec$' {
            $settings.TimeoutSec = [int]$args[++$i]
            continue
        }
        '^-TrapName$' {
            $settings.TrapName = $args[++$i]
            continue
        }
        '^-TrapBreakRva$|^-BreakRva$' {
            $settings.TrapBreakRva = $args[++$i]
            continue
        }
        '^-TrapBreakAddress$|^-BreakAddress$' {
            $settings.TrapBreakAddress = $args[++$i]
            continue
        }
        '^-TrapCondition$|^-Condition$' {
            $settings.TrapCondition = $args[++$i]
            continue
        }
        '^-TrapGdbScript$|^-GdbScript$' {
            $settings.TrapGdbScript = $args[++$i]
            continue
        }
        '^-TrapOnHitFile$|^-OnHitFile$' {
            $settings.TrapOnHitFile = $args[++$i]
            continue
        }
        '^-TrapOutDir$' {
            $settings.TrapOutDir = $args[++$i]
            continue
        }
        '^-ImageBase$' {
            $settings.ImageBase = $args[++$i]
            continue
        }
        '^-ImageBaseRegex$' {
            $settings.ImageBaseRegex = $args[++$i]
            continue
        }
        '^-TrapDryRun$|^-DryRun$' {
            $settings.DryRun = $true
            continue
        }
        '^-NoStopBeforeGdb$' {
            $settings.NoStopBeforeGdb = $true
            continue
        }
        '^-NoAutoContinue$' {
            $settings.NoAutoContinue = $true
            continue
        }
        '^-NoWaitForRunner$' {
            $settings.NoWaitForRunner = $true
            continue
        }
        '^-Gui$' {
            $settings.Gui = $true
            continue
        }
        default {
            $runArgs.Add($arg)
            continue
        }
    }
}

$settings.RunScript = (Resolve-Path -LiteralPath $settings.RunScript).Path
$settings.LogPath = [System.IO.Path]::GetFullPath($settings.LogPath)
$settings.TrapOutDir = [System.IO.Path]::GetFullPath($settings.TrapOutDir)
$settings.GdbExe = Resolve-GdbExe -Explicit $settings.GdbExe

New-Item -ItemType Directory -Force -Path $settings.TrapOutDir | Out-Null

if ($settings.Gui) {
    $env:SHARPOS_GUI = "1"
}

try { chcp 65001 > $null } catch { }
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    [Console]::InputEncoding = [System.Text.Encoding]::UTF8
} catch { }
$OutputEncoding = [System.Text.Encoding]::UTF8
$PSDefaultParameterValues['Tee-Object:Encoding'] = 'utf8'
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'

$joinedRunArgs = ($runArgs | ForEach-Object { Format-RunArgument $_ }) -join " "
$runCommand = @"
`$ErrorActionPreference = 'Stop'
try { chcp 65001 > `$null } catch { }
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    [Console]::InputEncoding = [System.Text.Encoding]::UTF8
} catch { }
`$OutputEncoding = [System.Text.Encoding]::UTF8
`$PSDefaultParameterValues['Tee-Object:Encoding'] = 'utf8'
`$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
& $(Quote-PowerShellLiteral $settings.RunScript) $joinedRunArgs 2>&1 | Tee-Object -FilePath $(Quote-PowerShellLiteral $settings.LogPath)
exit `$LASTEXITCODE
"@

Write-Host "[trap] launching: $($settings.RunScript) $joinedRunArgs"
Write-Host "[trap] log: $($settings.LogPath)"

$runner = $null

try {
    if (-not $settings.DryRun) {
        $encodedRunCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($runCommand))
        $runner = Start-Process -FilePath "pwsh" -ArgumentList @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-EncodedCommand", $encodedRunCommand
        ) -NoNewWindow -PassThru
    }

    if ($settings.DryRun) {
        Write-Host "[trap] dry-run: run command follows"
        Write-Host $runCommand
    }

    Write-Host "[trap] waiting for QMP on 127.0.0.1:$($settings.QmpPort)"
    if (-not $settings.DryRun) {
        Wait-TcpPort -HostName "127.0.0.1" -Port $settings.QmpPort -TimeoutSec $settings.TimeoutSec
    }

    $imageBaseValue = [UInt64]0
    if ($settings.TrapBreakAddress) {
        if ($settings.ImageBase) {
            $imageBaseValue = Parse-Num $settings.ImageBase
        }
    }
    else {
        if ($settings.ImageBase) {
            $imageBaseValue = Parse-Num $settings.ImageBase
        }
        else {
            Write-Host "[trap] waiting for imageBase in log"
            if ($settings.DryRun) {
                if (Test-Path -LiteralPath $settings.LogPath) {
                    $imageBaseValue = Wait-ImageBase -LogPath $settings.LogPath -Regex $settings.ImageBaseRegex -TimeoutSec 1
                }
                else {
                    throw "Dry-run needs -ImageBase when $($settings.LogPath) does not exist"
                }
            }
            else {
                $imageBaseValue = Wait-ImageBase -LogPath $settings.LogPath -Regex $settings.ImageBaseRegex -TimeoutSec $settings.TimeoutSec
            }
            Write-Host "[trap] imageBase: $(Format-Hex64 $imageBaseValue)"
        }
    }

    if (-not $settings.NoStopBeforeGdb) {
        Write-Host "[trap] stopping guest before GDB attaches"
        if (-not $settings.DryRun) {
            [void](Invoke-Hmp -Port $settings.QmpPort -Line "stop")
        }
    }

    Write-Host "[trap] enabling QEMU gdbserver on tcp::$($settings.GdbPort)"
    if (-not $settings.DryRun) {
        $gdbServerResp = Invoke-Hmp -Port $settings.QmpPort -Line "gdbserver tcp::$($settings.GdbPort)"
        if ($gdbServerResp.return) {
            Write-Host ($gdbServerResp.return.TrimEnd())
        }
    }

    $breakRvaValues = if ($settings.TrapBreakRva) { Parse-NumList $settings.TrapBreakRva } else { [UInt64[]]@([UInt64]0) }
    $breakAddressValues = if ($settings.TrapBreakAddress) {
        Parse-NumList $settings.TrapBreakAddress
    }
    else {
        [UInt64[]]($breakRvaValues | ForEach-Object { $imageBaseValue + $_ })
    }
    if ($settings.TrapBreakAddress) {
        $breakRvaValues = [UInt64[]]($breakAddressValues | ForEach-Object { [UInt64]0 })
    }
    $breakAddressValue = $breakAddressValues[0]
    $breakRvaValue = $breakRvaValues[0]

    $values = @{
        GDB_PORT = [string]$settings.GdbPort
        QMP_PORT = [string]$settings.QmpPort
        LOG_PATH = $settings.LogPath
        IMAGE_BASE = Format-Hex64 $imageBaseValue
        BREAK_ADDR = Format-Hex64 $breakAddressValue
        BREAK_RVA = Format-Hex64 $breakRvaValue
        TRAP_NAME = $settings.TrapName
    }

    $gdbScriptPath = Join-Path $settings.TrapOutDir "$($settings.TrapName).gdb"
    if ($settings.TrapGdbScript) {
        $templatePath = Resolve-Path -LiteralPath $settings.TrapGdbScript
        $template = Get-Content -LiteralPath $templatePath -Raw
        Set-Content -LiteralPath $gdbScriptPath -Value (Expand-TrapTemplate -Template $template -Values $values) -Encoding utf8
    }
    else {
        $onHitCommands = if ($settings.TrapOnHitFile) {
            Get-Content -LiteralPath (Resolve-Path -LiteralPath $settings.TrapOnHitFile)
        }
        else {
            New-DefaultOnHitCommands
        }

        $scriptText = New-DefaultGdbScript `
            -TrapName $settings.TrapName `
            -BreakAddresses $breakAddressValues `
            -BreakRvas $breakRvaValues `
            -Condition $settings.TrapCondition `
            -GdbPort $settings.GdbPort `
            -OnHitCommands $onHitCommands `
            -AutoContinue (-not $settings.NoAutoContinue)

        Set-Content -LiteralPath $gdbScriptPath -Value $scriptText -Encoding ascii
    }

    Write-Host "[trap] gdb script: $gdbScriptPath"
    Write-Host "[trap] starting GDB; when the trap hits, GDB will stay at the prompt"
    if ($settings.DryRun) {
        Write-Host "[trap] dry-run: GDB command would be:"
        Write-Host "$(Quote-PowerShellLiteral $settings.GdbExe) -q -x $(Quote-PowerShellLiteral $gdbScriptPath)"
    }
    else {
        & $settings.GdbExe -q -x $gdbScriptPath
    }
}
finally {
    if (-not $settings.NoWaitForRunner -and $runner -and -not $runner.HasExited) {
        Write-Host "[trap] waiting for run_build process pid=$($runner.Id)"
        Wait-Process -Id $runner.Id
    }
}
