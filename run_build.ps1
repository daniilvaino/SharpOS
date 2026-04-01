param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRun,
    [switch]$Stop,
    [int]$QmpPort = 4444,
    [string]$QemuExe,
    [string]$OvmfCode,
    [string]$OvmfVars
)

$ErrorActionPreference = "Stop"

if ($Stop) {
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $client.Connect("127.0.0.1", $QmpPort)

        $stream = $client.GetStream()
        $writer = [System.IO.StreamWriter]::new($stream)
        $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($stream)

        $stream.ReadTimeout = 3000
        try {
            [void]$reader.ReadLine()
        }
        catch {
        }

        $writer.WriteLine('{"execute":"qmp_capabilities"}')
        $writer.WriteLine('{"execute":"quit"}')
        Start-Sleep -Milliseconds 100

        $reader.Dispose()
        $writer.Dispose()
        $stream.Dispose()
        $client.Dispose()
        Write-Host "QEMU quit command sent to 127.0.0.1:$QmpPort."
        exit 0
    }
    catch {
        throw "Could not connect to QMP on 127.0.0.1:$QmpPort. Is QEMU running from this script?"
    }
}

function Resolve-FirstPath {
    param(
        [string[]]$Candidates,
        [string]$Label
    )

    foreach ($candidate in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $expanded = [Environment]::ExpandEnvironmentVariables($candidate)
        if (Test-Path -LiteralPath $expanded) {
            return (Resolve-Path -LiteralPath $expanded).Path
        }
    }

    throw "Could not find $Label. Pass an explicit path via the script parameter."
}

function Resolve-OptionalPath {
    param([string[]]$Candidates)

    foreach ($candidate in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $expanded = [Environment]::ExpandEnvironmentVariables($candidate)
        if (Test-Path -LiteralPath $expanded) {
            return (Resolve-Path -LiteralPath $expanded).Path
        }
    }

    return $null
}

function Write-U16 {
    param([byte[]]$Buffer, [int]$Offset, [uint16]$Value)
    $Buffer[$Offset + 0] = [byte]($Value -band 0xFF)
    $Buffer[$Offset + 1] = [byte](($Value -shr 8) -band 0xFF)
}

function Write-U32 {
    param([byte[]]$Buffer, [int]$Offset, [uint32]$Value)
    $Buffer[$Offset + 0] = [byte]($Value -band 0xFF)
    $Buffer[$Offset + 1] = [byte](($Value -shr 8) -band 0xFF)
    $Buffer[$Offset + 2] = [byte](($Value -shr 16) -band 0xFF)
    $Buffer[$Offset + 3] = [byte](($Value -shr 24) -band 0xFF)
}

function Write-U64 {
    param([byte[]]$Buffer, [int]$Offset, [uint64]$Value)
    Write-U32 -Buffer $Buffer -Offset $Offset -Value ([uint32]($Value -band 0xFFFFFFFF))
    Write-U32 -Buffer $Buffer -Offset ($Offset + 4) -Value ([uint32](($Value -shr 32) -band 0xFFFFFFFF))
}

function Write-Bytes {
    param([byte[]]$Buffer, [int]$Offset, [byte[]]$Values)
    for ($i = 0; $i -lt $Values.Length; $i++) {
        $Buffer[$Offset + $i] = $Values[$i]
    }
}

function New-AppElfImage {
    $imageSize = 4096
    $textOffset = 0x100
    $dataOffset = 0x200
    $textVaddr = [uint64]0x0000000000400000
    $dataVaddr = [uint64]0x0000000000401000
    $entry = [uint64]0x0000000000400010

    [byte[]]$bytes = New-Object byte[] $imageSize

    $bytes[0] = 0x7F
    $bytes[1] = [byte][char]'E'
    $bytes[2] = [byte][char]'L'
    $bytes[3] = [byte][char]'F'
    $bytes[4] = 2
    $bytes[5] = 1
    $bytes[6] = 1
    Write-U16 -Buffer $bytes -Offset 16 -Value 2
    Write-U16 -Buffer $bytes -Offset 18 -Value 0x3E
    Write-U32 -Buffer $bytes -Offset 20 -Value 1
    Write-U64 -Buffer $bytes -Offset 24 -Value $entry
    Write-U64 -Buffer $bytes -Offset 32 -Value 64
    Write-U16 -Buffer $bytes -Offset 52 -Value 64
    Write-U16 -Buffer $bytes -Offset 54 -Value 56
    Write-U16 -Buffer $bytes -Offset 56 -Value 3

    # PHDR 0: PT_LOAD RX
    $ph0 = 64
    Write-U32 -Buffer $bytes -Offset ($ph0 + 0) -Value 1
    Write-U32 -Buffer $bytes -Offset ($ph0 + 4) -Value 5
    Write-U64 -Buffer $bytes -Offset ($ph0 + 8) -Value $textOffset
    Write-U64 -Buffer $bytes -Offset ($ph0 + 16) -Value $textVaddr
    Write-U64 -Buffer $bytes -Offset ($ph0 + 32) -Value 0x80
    Write-U64 -Buffer $bytes -Offset ($ph0 + 40) -Value 0x80
    Write-U64 -Buffer $bytes -Offset ($ph0 + 48) -Value 0x1000

    # PHDR 1: PT_LOAD RW
    $ph1 = $ph0 + 56
    Write-U32 -Buffer $bytes -Offset ($ph1 + 0) -Value 1
    Write-U32 -Buffer $bytes -Offset ($ph1 + 4) -Value 6
    Write-U64 -Buffer $bytes -Offset ($ph1 + 8) -Value $dataOffset
    Write-U64 -Buffer $bytes -Offset ($ph1 + 16) -Value $dataVaddr
    Write-U64 -Buffer $bytes -Offset ($ph1 + 32) -Value 0x40
    Write-U64 -Buffer $bytes -Offset ($ph1 + 40) -Value 0x100
    Write-U64 -Buffer $bytes -Offset ($ph1 + 48) -Value 0x1000

    # PHDR 2: PT_NOTE
    $ph2 = $ph1 + 56
    Write-U32 -Buffer $bytes -Offset ($ph2 + 0) -Value 4
    Write-U32 -Buffer $bytes -Offset ($ph2 + 4) -Value 4
    Write-U64 -Buffer $bytes -Offset ($ph2 + 8) -Value 0x300
    Write-U64 -Buffer $bytes -Offset ($ph2 + 32) -Value 0x20
    Write-U64 -Buffer $bytes -Offset ($ph2 + 40) -Value 0x20
    Write-U64 -Buffer $bytes -Offset ($ph2 + 48) -Value 8

    # Fill segment contents
    for ($i = 0; $i -lt 0x80; $i++) {
        $bytes[$textOffset + $i] = 0x90
    }
    for ($i = 0; $i -lt 0x40; $i++) {
        $bytes[$dataOffset + $i] = [byte](0x22 + ($i -band 0x0F))
    }

    # entry code:
    # rdi = ProcessStartupBlock*
    # mov r10, rdi
    # mov r11, [r10+0x38]      ; startup->ServiceTableAddress
    # mov rax, [r11+0x08]      ; service->WriteStringAddress
    # lea rcx, [rip+msg]       ; arg1 = null-terminated string
    # sub rsp, 0x38            ; shadow + save slot + alignment for call
    # mov [rsp+0x20], r10
    # call rax                 ; WriteString("hello from app\n")
    # mov r10, [rsp+0x20]
    # add rsp, 0x38
    # mov rdx, [r10+0x30]      ; startup->MarkerAddress
    # mov eax, 0x12345678
    # mov [rdx], eax
    # mov eax, 42
    # ret
    Write-Bytes -Buffer $bytes -Offset ($textOffset + 0x10) -Values @(
        0x49,0x89,0xFA,
        0x4D,0x8B,0x5A,0x38,
        0x49,0x8B,0x43,0x08,
        0x48,0x8D,0x0D,0x3E,0x00,0x00,0x00,
        0x48,0x83,0xEC,0x38,
        0x4C,0x89,0x54,0x24,0x20,
        0xFF,0xD0,
        0x4C,0x8B,0x54,0x24,0x20,
        0x48,0x83,0xC4,0x38,
        0x49,0x8B,0x52,0x30,
        0xB8,0x78,0x56,0x34,0x12,
        0x89,0x02,
        0xB8,0x2A,0x00,0x00,0x00,
        0xC3
    )

    # message at text+0x60
    Write-Bytes -Buffer $bytes -Offset ($textOffset + 0x60) -Values @(
        0x68,0x65,0x6C,0x6C,0x6F,0x20,0x66,0x72,0x6F,0x6D,0x20,0x61,0x70,0x70,0x0A,0x00
    )

    # marker slot in data segment at 0x401020 starts as zero
    Write-U32 -Buffer $bytes -Offset ($dataOffset + 0x20) -Value 0
    return $bytes
}

$repoRoot = Split-Path -Parent $PSCommandPath
$efiProjectDir = Join-Path $repoRoot "OS_0.1"
$projectFile = Join-Path $efiProjectDir "OS_0.1.csproj"
if (-not (Test-Path -LiteralPath $projectFile)) {
    throw "Project file not found: $projectFile"
}

[xml]$projectXml = Get-Content -LiteralPath $projectFile
$targetFramework = $null
if ($projectXml -and $projectXml.Project -and $projectXml.Project.PropertyGroup) {
    foreach ($propertyGroup in $projectXml.Project.PropertyGroup) {
        if ($propertyGroup.TargetFramework) {
            $targetFramework = $propertyGroup.TargetFramework.Trim()
            if (-not [string]::IsNullOrWhiteSpace($targetFramework)) {
                break
            }
        }
    }
}

if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw "Could not resolve TargetFramework from $projectFile"
}

if (-not $QemuExe) {
    $cmd = Get-Command "qemu-system-x86_64.exe" -ErrorAction SilentlyContinue
    if ($cmd) {
        $QemuExe = $cmd.Source
    }
}

$QemuExe = Resolve-FirstPath -Candidates @(
    $QemuExe,
    "C:\msys64\mingw64\bin\qemu-system-x86_64.exe",
    "C:\Program Files\qemu\qemu-system-x86_64.exe",
    "C:\Program Files\QEMU\qemu-system-x86_64.exe"
) -Label "qemu-system-x86_64.exe"

$OvmfCode = Resolve-FirstPath -Candidates @(
    $OvmfCode,
    "C:\msys64\mingw64\share\qemu\edk2-x86_64-code.fd",
    "C:\Program Files\qemu\share\edk2-x86_64-code.fd",
    "C:\Program Files\QEMU\share\edk2-x86_64-code.fd",
    "C:\Program Files\qemu\share\ovmf\OVMF_CODE.fd",
    "C:\Program Files\QEMU\share\ovmf\OVMF_CODE.fd"
) -Label "OVMF firmware code file"

$OvmfVars = Resolve-OptionalPath -Candidates @(
    $OvmfVars,
    "C:\msys64\mingw64\share\qemu\edk2-x86_64-vars.fd",
    "C:\msys64\mingw64\share\qemu\edk2-i386-vars.fd",
    "C:\Program Files\qemu\share\edk2-x86_64-vars.fd",
    "C:\Program Files\QEMU\share\edk2-x86_64-vars.fd",
    "C:\Program Files\qemu\share\ovmf\OVMF_VARS.fd",
    "C:\Program Files\QEMU\share\ovmf\OVMF_VARS.fd"
)

$qemuWorkDir = Join-Path $efiProjectDir ".qemu"
$firmwareDir = Join-Path $qemuWorkDir "firmware"
New-Item -ItemType Directory -Force -Path $firmwareDir | Out-Null
$localOvmfCode = Join-Path $firmwareDir "OVMF_CODE.fd"
Copy-Item -LiteralPath $OvmfCode -Destination $localOvmfCode -Force
$localOvmfVars = $null
if ($OvmfVars) {
    $localOvmfVars = Join-Path $firmwareDir "OVMF_VARS.fd"
    Copy-Item -LiteralPath $OvmfVars -Destination $localOvmfVars -Force
}

$env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

Write-Host "Building OS_0.1 ($Configuration)..."
Push-Location $efiProjectDir
try {
    & dotnet publish $projectFile -c $Configuration -r win-x64
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$publishDir = Join-Path $efiProjectDir "bin\$Configuration\$targetFramework\win-x64\publish"
$builtEfi = Join-Path $publishDir "OS_0.1.exe"
if (-not (Test-Path -LiteralPath $builtEfi)) {
    $builtEfi = Get-ChildItem -LiteralPath $publishDir -Filter *.exe -File -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $builtEfi -or -not (Test-Path -LiteralPath $builtEfi)) {
    throw "Built EFI binary not found in: $publishDir"
}

$espBootDir = Join-Path $qemuWorkDir "esp\EFI\BOOT"
New-Item -ItemType Directory -Force -Path $espBootDir | Out-Null
$bootx64 = Join-Path $espBootDir "BOOTX64.EFI"
Copy-Item -LiteralPath $builtEfi -Destination $bootx64 -Force
$appElf = Join-Path $espBootDir "APP.ELF"
[System.IO.File]::WriteAllBytes($appElf, (New-AppElfImage))

Write-Host "Prepared EFI image: $bootx64"
Write-Host "Prepared app ELF: $appElf"
if ($NoRun) {
    Write-Host "NoRun set: build finished, QEMU launch skipped."
    exit 0
}

Write-Host "Launching QEMU..."
Write-Host "COM1 is attached to this terminal (-serial mon:stdio)."
Write-Host "Exit QEMU: Ctrl+], then X; if hotkeys are blocked, run .\run_build.ps1 -Stop in another terminal."
if (-not $localOvmfVars) {
    Write-Host "OVMF_VARS file was not found; booting without persistent UEFI variable store."
}

$originalWindowTitle = $null
$windowTitleSet = $false
if ($Host -and $Host.UI -and $Host.UI.RawUI) {
    $sharpOsTitle = [string]::Concat([char]0x0428, [char]0x0430, [char]0x0440, [char]0x043F, [char]0x043E, [char]0x0441)
    $originalWindowTitle = $Host.UI.RawUI.WindowTitle
    $Host.UI.RawUI.WindowTitle = $sharpOsTitle
    $windowTitleSet = $true
}

Push-Location $qemuWorkDir
try {
    $qemuArgs = @(
        "-machine", "q35,accel=tcg",
        "-cpu", "qemu64",
        "-m", "256",
        "-nographic",
        "-serial", "mon:stdio",
        "-echr", "0x1d",
        "-net", "none",
        "-no-reboot",
        "-qmp", "tcp:127.0.0.1:$QmpPort,server,nowait",
        "-drive", "if=pflash,format=raw,readonly=on,file=firmware/OVMF_CODE.fd"
    )

    if ($localOvmfVars) {
        $qemuArgs += @("-drive", "if=pflash,format=raw,file=firmware/OVMF_VARS.fd")
    }

    $qemuArgs += @("-drive", "format=raw,file=fat:rw:esp")

    & $QemuExe @qemuArgs

    if ($LASTEXITCODE -ne 0) {
        throw "QEMU exited with code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
    if ($windowTitleSet) {
        try {
			Start-Sleep -Milliseconds 1000
            $Host.UI.RawUI.WindowTitle = $originalWindowTitle
        }
        catch {
        }
    }
}
