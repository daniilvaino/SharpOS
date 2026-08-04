# tools/symbolize.ps1
# ---------------------------------------------------------------------------
# Turn image-relative addresses from a crash dump into function names, using
# dbghelp against the kernel's own PDB.
#
# Always symbolize against the kernel image (BOOTX64.EFI == OS/bin/.../native/
# OS.exe): CoreCLR is statically linked into it, so a standalone coreclr.dll
# would give phantom names for the same RVAs.
#
# The log prints both an absolute RIP and the image base it belongs to
# ("ib=0x..." on [seh-step] lines, "imageBase=0x..." on the exception dump);
# pass either -Rva directly or -Rip together with -ImageBase.
#
# Usage:
#   pwsh tools/symbolize.ps1 -Rva 0x399AD
#   pwsh tools/symbolize.ps1 -Rip 0x7CD239AD -ImageBase 0x7CCEA000
#   pwsh tools/symbolize.ps1 -Rva 0x399AD,0x386CC,0x446A0
# ---------------------------------------------------------------------------
[CmdletBinding()]
param(
    [string[]] $Rva,
    [string[]] $Rip,
    [string]   $ImageBase,
    [string]   $Image = "$PSScriptRoot\..\OS\bin\Release\net8.0\win-x64\native\OS.exe"
)

$ErrorActionPreference = "Stop"

$image = (Resolve-Path -LiteralPath $Image).Path
$symbolDir = Split-Path -Parent $image

# Collect the RVAs to resolve. Everything arrives as text (pwsh -File hands
# "a,b" over as one string), so split and parse here rather than leaning on
# parameter binding.
$targets = New-Object System.Collections.Generic.List[UInt64]

function Parse-Hex([string] $text) {
    $t = $text.Trim()
    if ($t.StartsWith("0x") -or $t.StartsWith("0X")) { return [Convert]::ToUInt64($t.Substring(2), 16) }
    return [Convert]::ToUInt64($t, 10)
}

if ($Rva) {
    foreach ($chunk in ([string]::Join(",", $Rva) -split ',')) {
        if ($chunk.Trim()) { $targets.Add((Parse-Hex $chunk)) }
    }
}

if ($Rip) {
    if (-not $ImageBase) { throw "-Rip needs -ImageBase (the ib=0x.. value from the log)" }
    $imageBaseValue = Parse-Hex $ImageBase
    foreach ($chunk in ([string]::Join(",", $Rip) -split ',')) {
        if (-not $chunk.Trim()) { continue }
        $absolute = Parse-Hex $chunk
        if ($absolute -lt $imageBaseValue) { throw "RIP $chunk is below the image base" }
        $targets.Add($absolute - $imageBaseValue)
    }
}

if ($targets.Count -eq 0) { throw "nothing to symbolize: pass -Rva or -Rip" }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class DbgHelp
{
    const string Dll = @"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\dbghelp.dll";

    [DllImport(Dll, SetLastError = true)]
    public static extern bool SymInitialize(IntPtr hProcess, string userSearchPath, bool invadeProcess);

    [DllImport(Dll)]
    public static extern uint SymSetOptions(uint options);

    [DllImport(Dll, SetLastError = true)]
    public static extern ulong SymLoadModuleEx(IntPtr hProcess, IntPtr hFile, string imageName,
        string moduleName, ulong baseOfDll, uint dllSize, IntPtr data, uint flags);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool SymFromAddr(IntPtr hProcess, ulong address, out ulong displacement, IntPtr symbol);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool SymGetLineFromAddr64(IntPtr hProcess, ulong address, out uint displacement, IntPtr line);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();
}
'@

$SYMOPT_UNDNAME      = 0x00000002
$SYMOPT_DEFERRED     = 0x00000004
$SYMOPT_LOAD_LINES   = 0x00000010
$SYMOPT_NO_PROMPTS   = 0x00080000

# No deferred loading: with it dbghelp answers "no symbol" for addresses it has
# not touched yet, which reads exactly like a missing PDB.
[void][DbgHelp]::SymSetOptions($SYMOPT_UNDNAME -bor $SYMOPT_LOAD_LINES -bor $SYMOPT_NO_PROMPTS)

$proc = [DbgHelp]::GetCurrentProcess()
if (-not [DbgHelp]::SymInitialize($proc, $symbolDir, $false)) {
    throw "SymInitialize failed: $([ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()).Message)"
}

# Load at a synthetic base; every lookup below is base + rva.
$loadBase = [uint64]0x10000000
$loaded = [DbgHelp]::SymLoadModuleEx($proc, [IntPtr]::Zero, $image, $null, $loadBase, 0, [IntPtr]::Zero, 0)
if ($loaded -eq 0) {
    throw "SymLoadModuleEx failed: $([ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()).Message)"
}

# SYMBOL_INFO (x64): MaxNameLen sits at +80 and the inline Name buffer at +84.
# Getting those four bytes wrong silently chops the first characters off every
# name, which looks like a demangling problem rather than an offset one.
$maxName = 1024
$symSize = 88 + $maxName
$symBuf = [Runtime.InteropServices.Marshal]::AllocHGlobal($symSize)
$lineBuf = [Runtime.InteropServices.Marshal]::AllocHGlobal(24)

try {
    # Not $rva: PowerShell is case-insensitive, so that would reuse the [string[]]
    # -Rva parameter and coerce every assignment back into a string array.
    foreach ($targetRva in $targets) {
        [Runtime.InteropServices.Marshal]::WriteInt32($symBuf, 0, 88)          # SizeOfStruct
        [Runtime.InteropServices.Marshal]::WriteInt32($symBuf, 80, $maxName)   # MaxNameLen

        $displacement = [uint64]0
        $address = [uint64]$loadBase + [uint64]$targetRva
        $line = ""

        if ([DbgHelp]::SymFromAddr($proc, $address, [ref] $displacement, $symBuf)) {
            $name = [Runtime.InteropServices.Marshal]::PtrToStringAnsi([IntPtr]::Add($symBuf, 84))

            [Runtime.InteropServices.Marshal]::WriteInt32($lineBuf, 0, 24)     # SizeOfStruct
            $lineDisp = 0
            if ([DbgHelp]::SymGetLineFromAddr64($proc, $address, [ref] $lineDisp, $lineBuf)) {
                $filePtr = [Runtime.InteropServices.Marshal]::ReadIntPtr($lineBuf, 8)
                $file = [Runtime.InteropServices.Marshal]::PtrToStringAnsi($filePtr)
                $lineNo = [Runtime.InteropServices.Marshal]::ReadInt32($lineBuf, 16)
                $line = "  $file`:$lineNo"
            }

            "{0,-12} {1}+0x{2:x}{3}" -f ("0x" + $targetRva.ToString("x")), $name, $displacement, $line
        }
        else {
            "{0,-12} <no symbol>" -f ("0x" + $targetRva.ToString("x"))
        }
    }
}
finally {
    [Runtime.InteropServices.Marshal]::FreeHGlobal($symBuf)
    [Runtime.InteropServices.Marshal]::FreeHGlobal($lineBuf)
}
