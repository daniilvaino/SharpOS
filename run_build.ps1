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

function New-DefaultDataSegment {
    [byte[]]$data = New-Object byte[] 0x40
    for ($i = 0; $i -lt 0x40; $i++) {
        $data[$i] = [byte](0x22 + ($i -band 0x0F))
    }
    $data[0x20] = 0
    $data[0x21] = 0
    $data[0x22] = 0
    $data[0x23] = 0
    return $data
}

function New-BaseElfImage {
    param([byte[]]$TextSegment, [byte[]]$DataSegment)

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
    Write-U64 -Buffer $bytes -Offset ($ph0 + 32) -Value $TextSegment.Length
    Write-U64 -Buffer $bytes -Offset ($ph0 + 40) -Value $TextSegment.Length
    Write-U64 -Buffer $bytes -Offset ($ph0 + 48) -Value 0x1000

    # PHDR 1: PT_LOAD RW
    $ph1 = $ph0 + 56
    Write-U32 -Buffer $bytes -Offset ($ph1 + 0) -Value 1
    Write-U32 -Buffer $bytes -Offset ($ph1 + 4) -Value 6
    Write-U64 -Buffer $bytes -Offset ($ph1 + 8) -Value $dataOffset
    Write-U64 -Buffer $bytes -Offset ($ph1 + 16) -Value $dataVaddr
    Write-U64 -Buffer $bytes -Offset ($ph1 + 32) -Value $DataSegment.Length
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

    for ($i = 0; $i -lt $TextSegment.Length; $i++) {
        $bytes[$textOffset + $i] = $TextSegment[$i]
    }

    for ($i = 0; $i -lt $DataSegment.Length; $i++) {
        $bytes[$dataOffset + $i] = $DataSegment[$i]
    }

    return $bytes
}

function New-HelloElfImage {
    [byte[]]$text = New-Object byte[] 0x100
    for ($i = 0; $i -lt $text.Length; $i++) {
        $text[$i] = 0x90
    }

    Write-Bytes -Buffer $text -Offset 0x10 -Values @(
        0x4C,0x8B,0x5F,0x38,
        0x49,0x8B,0x43,0x08,
        0x48,0x8D,0x0D,0x61,0x00,0x00,0x00,
        0x48,0x83,0xEC,0x28,
        0xFF,0xD0,
        0x48,0x83,0xC4,0x28,
        0x4C,0x8B,0x5F,0x38,
        0x49,0x8B,0x43,0x28,
        0xB9,0x0A,0x00,0x00,0x00,
        0x48,0x83,0xEC,0x28,
        0xFF,0xD0,
        0x48,0x83,0xC4,0x28,
        0xB8,0x0A,0x00,0x00,0x00,
        0xC3
    )

    Write-Bytes -Buffer $text -Offset 0x80 -Values @(
        0x68,0x65,0x6C,0x6C,0x6F,0x20,0x66,0x72,0x6F,0x6D,0x20,0x68,0x65,0x6C,0x6C,0x6F,0x20,0x61,0x70,0x70,0x0A,0x00
    )

    return (New-BaseElfImage -TextSegment $text -DataSegment (New-DefaultDataSegment))
}

function New-AbiInfoElfImage {
    [byte[]]$text = New-Object byte[] 0x100
    for ($i = 0; $i -lt $text.Length; $i++) {
        $text[$i] = 0x90
    }

    Write-Bytes -Buffer $text -Offset 0x10 -Values @(
        0x4C,0x8B,0x5F,0x38,
        0x49,0x8B,0x43,0x08,
        0x48,0x8D,0x0D,0x81,0x00,0x00,0x00,
        0x48,0x83,0xEC,0x28,
        0xFF,0xD0,
        0x48,0x83,0xC4,0x28,
        0x4C,0x8B,0x5F,0x38,
        0x49,0x8B,0x43,0x08,
        0x48,0x8D,0x0D,0x80,0x00,0x00,0x00,
        0x48,0x83,0xEC,0x28,
        0xFF,0xD0,
        0x48,0x83,0xC4,0x28,
        0x4C,0x8B,0x5F,0x38,
        0x49,0x8B,0x43,0x20,
        0x48,0x83,0xEC,0x28,
        0xFF,0xD0,
        0x48,0x83,0xC4,0x28,
        0x89,0xC1,
        0x4C,0x8B,0x5F,0x38,
        0x49,0x8B,0x43,0x10,
        0x48,0x83,0xEC,0x28,
        0xFF,0xD0,
        0x48,0x83,0xC4,0x28,
        0x4C,0x8B,0x5F,0x38,
        0x49,0x8B,0x43,0x08,
        0x48,0x8D,0x0D,0x49,0x00,0x00,0x00,
        0x48,0x83,0xEC,0x28,
        0xFF,0xD0,
        0x48,0x83,0xC4,0x28,
        0x4C,0x8B,0x5F,0x38,
        0x49,0x8B,0x43,0x28,
        0xB9,0x0B,0x00,0x00,0x00,
        0x48,0x83,0xEC,0x28,
        0xFF,0xD0,
        0x48,0x83,0xC4,0x28,
        0xB8,0x0B,0x00,0x00,0x00,
        0xC3
    )

    Write-Bytes -Buffer $text -Offset 0xA0 -Values @(
        0x61,0x62,0x69,0x20,0x69,0x6E,0x66,0x6F,0x20,0x61,0x70,0x70,0x0A,0x00
    )
    Write-Bytes -Buffer $text -Offset 0xB8 -Values @(
        0x61,0x62,0x69,0x3D,0x00
    )
    Write-Bytes -Buffer $text -Offset 0xC0 -Values @(
        0x0A,0x00
    )

    return (New-BaseElfImage -TextSegment $text -DataSegment (New-DefaultDataSegment))
}

function New-MarkerElfImage {
    [byte[]]$text = New-Object byte[] 0x100
    for ($i = 0; $i -lt $text.Length; $i++) {
        $text[$i] = 0x90
    }

    Write-Bytes -Buffer $text -Offset 0x10 -Values @(
        0x4C,0x8B,0x5F,0x38,
        0x49,0x8B,0x43,0x08,
        0x48,0x8D,0x0D,0x81,0x00,0x00,0x00,
        0x48,0x83,0xEC,0x28,
        0xFF,0xD0,
        0x48,0x83,0xC4,0x28,
        0x48,0x8B,0x57,0x30,
        0xB8,0x78,0x56,0x34,0x12,
        0x89,0x02,
        0x4C,0x8B,0x5F,0x38,
        0x49,0x8B,0x43,0x08,
        0x48,0x8D,0x0D,0x6D,0x00,0x00,0x00,
        0x48,0x83,0xEC,0x28,
        0xFF,0xD0,
        0x48,0x83,0xC4,0x28,
        0x4C,0x8B,0x5F,0x38,
        0x49,0x8B,0x43,0x18,
        0x48,0x8B,0x4F,0x30,
        0x48,0x83,0xEC,0x28,
        0xFF,0xD0,
        0x48,0x83,0xC4,0x28,
        0x4C,0x8B,0x5F,0x38,
        0x49,0x8B,0x43,0x08,
        0x48,0x8D,0x0D,0x46,0x00,0x00,0x00,
        0x48,0x83,0xEC,0x28,
        0xFF,0xD0,
        0x48,0x83,0xC4,0x28,
        0x4C,0x8B,0x5F,0x38,
        0x49,0x8B,0x43,0x28,
        0xB9,0x0C,0x00,0x00,0x00,
        0x48,0x83,0xEC,0x28,
        0xFF,0xD0,
        0x48,0x83,0xC4,0x28,
        0xB8,0x0C,0x00,0x00,0x00,
        0xC3
    )

    Write-Bytes -Buffer $text -Offset 0xA0 -Values @(
        0x6D,0x61,0x72,0x6B,0x65,0x72,0x20,0x61,0x70,0x70,0x0A,0x00
    )
    Write-Bytes -Buffer $text -Offset 0xB0 -Values @(
        0x6D,0x61,0x72,0x6B,0x65,0x72,0x3D,0x00
    )
    Write-Bytes -Buffer $text -Offset 0xB8 -Values @(
        0x0A,0x00
    )

    return (New-BaseElfImage -TextSegment $text -DataSegment (New-DefaultDataSegment))
}

function New-AppAbiManifest {
    param(
        [uint16]$AppAbiVersion,
        [uint16]$ServiceAbi,
        [uint32]$Flags = 0
    )

    [byte[]]$bytes = New-Object byte[] 16
    $bytes[0] = [byte][char]'S'
    $bytes[1] = [byte][char]'A'
    $bytes[2] = [byte][char]'B'
    $bytes[3] = [byte][char]'I'
    Write-U16 -Buffer $bytes -Offset 4 -Value 1
    Write-U16 -Buffer $bytes -Offset 6 -Value $AppAbiVersion
    Write-U16 -Buffer $bytes -Offset 8 -Value $ServiceAbi
    Write-U16 -Buffer $bytes -Offset 10 -Value 0
    Write-U32 -Buffer $bytes -Offset 12 -Value $Flags
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

# Strict-NX OVMF (built via .\ovmf\build.ps1) is preferred.
# Falls back to the system QEMU OVMF if not yet built.
$OvmfCode = Resolve-FirstPath -Candidates @(
    $OvmfCode,
    (Join-Path $repoRoot "ovmf\OVMF_CODE.strict-nx.fd"),
    "C:\msys64\mingw64\share\qemu\edk2-x86_64-code.fd",
    "C:\Program Files\qemu\share\edk2-x86_64-code.fd",
    "C:\Program Files\QEMU\share\edk2-x86_64-code.fd",
    "C:\Program Files\qemu\share\ovmf\OVMF_CODE.fd",
    "C:\Program Files\QEMU\share\ovmf\OVMF_CODE.fd"
) -Label "OVMF firmware code file"

$OvmfVars = Resolve-OptionalPath -Candidates @(
    $OvmfVars,
    (Join-Path $repoRoot "ovmf\OVMF_VARS.strict-nx.fd"),
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
$helloElf = Join-Path $espBootDir "HELLO.ELF"
$abiInfoElf = Join-Path $espBootDir "ABIINFO.ELF"
$markerElf = Join-Path $espBootDir "MARKER.ELF"
[string]$helloCsElf = Join-Path $espBootDir "HELLOCS.ELF"
[string]$fetchElf = Join-Path $espBootDir "FETCH.ELF"
[string]$legacyAppElf = Join-Path $espBootDir "APP.ELF"
[System.IO.File]::WriteAllBytes($helloElf, (New-HelloElfImage))
[System.IO.File]::WriteAllBytes($abiInfoElf, (New-AbiInfoElfImage))
[System.IO.File]::WriteAllBytes($markerElf, (New-MarkerElfImage))
[System.IO.File]::WriteAllBytes("$helloElf.abi", (New-AppAbiManifest -AppAbiVersion 1 -ServiceAbi 0))
[System.IO.File]::WriteAllBytes("$abiInfoElf.abi", (New-AppAbiManifest -AppAbiVersion 1 -ServiceAbi 0))
[System.IO.File]::WriteAllBytes("$markerElf.abi", (New-AppAbiManifest -AppAbiVersion 1 -ServiceAbi 0))

if (Test-Path -LiteralPath $helloCsElf) {
    [System.IO.File]::WriteAllBytes("$helloCsElf.abi", (New-AppAbiManifest -AppAbiVersion 2 -ServiceAbi 1))
}
elseif (Test-Path -LiteralPath "$helloCsElf.abi") {
    Remove-Item -LiteralPath "$helloCsElf.abi" -Force
}

if (Test-Path -LiteralPath $fetchElf) {
    [System.IO.File]::WriteAllBytes("$fetchElf.abi", (New-AppAbiManifest -AppAbiVersion 2 -ServiceAbi 1))
}
elseif (Test-Path -LiteralPath "$fetchElf.abi") {
    Remove-Item -LiteralPath "$fetchElf.abi" -Force
}

if (Test-Path -LiteralPath $legacyAppElf) {
    Remove-Item -LiteralPath $legacyAppElf -Force
}

Write-Host "Prepared EFI image: $bootx64"
Write-Host "Prepared app ELF: $helloElf"
Write-Host "Prepared app ELF: $abiInfoElf"
Write-Host "Prepared app ELF: $markerElf"
if (Test-Path -LiteralPath $helloCsElf) {
    Write-Host "Prepared app ELF: $helloCsElf"
}
if (Test-Path -LiteralPath $fetchElf) {
    Write-Host "Prepared app ELF: $fetchElf"
}
if ($NoRun) {
    Write-Host "NoRun set: build finished, QEMU launch skipped."
    exit 0
}

Write-Host "Launching QEMU..."
Write-Host "Firmware: $OvmfCode"
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

# OVMF SerialDxe mirrors ConOut to COM1 as UTF-8.
# Switch the terminal to UTF-8 so box-drawing chars render correctly.
$savedOutputEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$savedInputEncoding = [Console]::InputEncoding
[Console]::InputEncoding = [System.Text.Encoding]::UTF8

Push-Location $qemuWorkDir
try {
    $machineArgs = @("-machine", "q35,accel=tcg")
    # +nx: expose the NX/XD bit to firmware and OS (required for NX memory protection policy)
    $cpuArgs = @("-cpu", "qemu64,+nx")

    $qemuArgs = $machineArgs + $cpuArgs + @(
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
    [Console]::OutputEncoding = $savedOutputEncoding
    [Console]::InputEncoding = $savedInputEncoding
    if ($windowTitleSet) {
        try {
			Start-Sleep -Milliseconds 1000
            $Host.UI.RawUI.WindowTitle = $originalWindowTitle
        }
        catch {
        }
    }
}
