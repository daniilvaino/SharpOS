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

Write-Host "Prepared EFI image: $bootx64"
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
}
