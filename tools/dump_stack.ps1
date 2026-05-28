# tools/dump_stack.ps1
# ---------------------------------------------------------------------------
# Dump a guest physical memory range from a HUNG QEMU (e.g. after a triple
# fault with -no-shutdown) via QMP pmemsave, for offline stack walking.
#
# Stack regions are identity-mapped in SharpOS (VA == PA), so the VA range
# printed by [CT] OK id=N stackBase=.. stackTop=.. can be passed directly.
#
# Usage:
#   pwsh tools/dump_stack.ps1 -Base 0xE283000 -Size 0x400000 -Out stack.bin
#   pwsh tools/dump_stack.ps1 -Base 0xEE80000 -Size 0x800000 -QmpPort 4444
#
# Then analyze with tools/walk_stack.ps1 (return-address scan).
# ---------------------------------------------------------------------------
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Base,
    [Parameter(Mandatory=$true)][string]$Size,
    [string]$Out = "stack.bin",
    [int]$QmpPort = 4444
)

$baseVal = [Convert]::ToUInt64($Base, 16 + 0) # accepts 0x.. via ToUInt64 base detection below
if ($Base -match '^0x') { $baseVal = [Convert]::ToUInt64($Base.Substring(2), 16) }
else { $baseVal = [Convert]::ToUInt64($Base, 10) }
if ($Size -match '^0x') { $sizeVal = [Convert]::ToUInt64($Size.Substring(2), 16) }
else { $sizeVal = [Convert]::ToUInt64($Size, 10) }

# pmemsave writes to a path on the HOST relative to QEMU's CWD (the .qemu
# workdir). Use an absolute path so we know where it lands.
$absOut = [System.IO.Path]::GetFullPath($Out)

$client = New-Object System.Net.Sockets.TcpClient
try {
    $client.Connect("127.0.0.1", $QmpPort)
    $stream = $client.GetStream()
    $reader = New-Object System.IO.StreamReader($stream)
    $writer = New-Object System.IO.StreamWriter($stream)
    $writer.AutoFlush = $true

    Start-Sleep -Milliseconds 100
    $reader.ReadLine() | Out-Null              # greeting
    $writer.WriteLine('{"execute":"qmp_capabilities"}')
    $reader.ReadLine() | Out-Null              # caps ack

    # pmemsave: val=base, size=size, filename=absOut
    $cmd = @{
        execute   = "pmemsave"
        arguments = @{ val = [int64]$baseVal; size = [int64]$sizeVal; filename = $absOut }
    } | ConvertTo-Json -Compress
    $writer.WriteLine($cmd)
    $resp = $reader.ReadLine()
    Write-Host "pmemsave resp: $resp"
    Write-Host "Dumped base=0x$($baseVal.ToString('X')) size=0x$($sizeVal.ToString('X')) -> $absOut"
}
catch {
    throw "QMP pmemsave failed on 127.0.0.1:$QmpPort -- is QEMU still running (hung)?  $_"
}
finally {
    $client.Close()
}
