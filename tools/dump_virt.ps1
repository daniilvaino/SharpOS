# tools/dump_virt.ps1
# ---------------------------------------------------------------------------
# Dump a guest VIRTUAL memory range from a running/hung QEMU via QMP memsave.
#
# pmemsave (tools/dump_stack.ps1) takes physical addresses, which only works
# for identity-mapped regions such as the stacks. JIT code lives high in the
# 0x5000_0000_0000 range and needs the CPU's page tables, hence memsave.
#
# Usage:
#   pwsh tools/dump_virt.ps1 -Base 0x500009081C50 -Size 0x100 -Out code.bin
# ---------------------------------------------------------------------------
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Base,
    [Parameter(Mandatory=$true)][string]$Size,
    [string]$Out = "virt.bin",
    [int]$Cpu = 0,
    [int]$QmpPort = 4444
)

function Parse-Num([string] $t) {
    if ($t -match '^0x') { return [Convert]::ToUInt64($t.Substring(2), 16) }
    return [Convert]::ToUInt64($t, 10)
}

$baseVal = Parse-Num $Base
$sizeVal = Parse-Num $Size
$absOut  = [System.IO.Path]::GetFullPath($Out)

$client = New-Object System.Net.Sockets.TcpClient
try {
    $client.Connect("127.0.0.1", $QmpPort)
    $stream = $client.GetStream()
    $reader = New-Object System.IO.StreamReader($stream)
    $writer = New-Object System.IO.StreamWriter($stream)
    $writer.AutoFlush = $true

    Start-Sleep -Milliseconds 100
    $reader.ReadLine() | Out-Null
    $writer.WriteLine('{"execute":"qmp_capabilities"}')
    $reader.ReadLine() | Out-Null

    $cmd = @{
        execute   = "memsave"
        arguments = @{ val = [int64]$baseVal; size = [int64]$sizeVal; filename = $absOut; "cpu-index" = $Cpu }
    } | ConvertTo-Json -Compress
    $writer.WriteLine($cmd)
    Write-Host "memsave resp: $($reader.ReadLine())"
    Write-Host "Dumped VA=0x$($baseVal.ToString('X')) size=0x$($sizeVal.ToString('X')) -> $absOut"
}
catch { throw "QMP memsave failed on 127.0.0.1:$QmpPort -- is QEMU still running?  $_" }
finally { $client.Close() }
