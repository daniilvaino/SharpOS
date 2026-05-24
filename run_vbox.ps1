<#
.SYNOPSIS
    Fully-automated VirtualBox run for SharpOS.

.DESCRIPTION
    Mirror of run_build.ps1's QEMU workflow, but for VirtualBox:
      1. Tears down any existing VM (power off, unlock, close media, unregister, purge folder).
      2. Builds the disk images via build_media_xorriso.ps1 (which itself calls run_build.ps1 -NoRun).
      3. Creates a fresh UEFI VM matching the QEMU machine profile (q35/ich9, 512 MB, std VGA).
      4. Attaches the freshly-built VHD and launches the VM.

    The script is idempotent: run it as many times as you like, you always get a clean VM.

    QEMU -> VirtualBox parameter mapping:
      -machine q35            -> --chipset ich9
      -m 512                  -> --memory 512
      -cpu qemu64,+nx         -> NX is on by default for 64-bit guests
      OVMF (if=pflash)        -> --firmware efi64
      -vga std                -> --graphicscontroller vboxvga
      -net none               -> --nic1 none
      -serial mon:stdio       -> --uart1 0x3F8 4 + --uartmode1 file <log>

    NOTE: VirtualBox uses its own built-in EFI firmware; the custom strict-NX OVMF
    used by the QEMU workflow cannot be substituted here.

.EXAMPLE
    Clear; & "C:\work\OS\run_vbox.ps1" 2>&1 | Tee-Object C:\work\OS\last_vbox.log

.EXAMPLE
    .\run_vbox.ps1 -Stop          # power off the running VM
#>

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$VmName = "SharpOS",

    # Skip calling build_images.ps1 entirely and reuse the existing VHD/ISO.
    [switch]$SkipImageBuild,

    # Pass -NoBuild to build_images.ps1: rebuild the images from an
    # already-compiled output without re-running the .NET compile.
    [switch]$NoCompile,

    # Launch without a GUI window (serial log is still written).
    [switch]$Headless,

    # Also attach the bootable ISO as a virtual DVD drive.
    [switch]$AttachIso,

    # Build the ISO too. Off by default: VirtualBox boots from the VHD,
    # the ISO is only useful when -AttachIso is also set.
    [switch]$BuildIso,

    # Just power off the VM and exit (mirror of run_build.ps1 -Stop).
    [switch]$Stop,

    [string]$BuildImagesScript = (Join-Path $PSScriptRoot "build_media_xorriso.ps1"),
    [string]$OutputDir         = (Join-Path $PSScriptRoot "OS\.qemu\media"),
    [string]$VhdPath,
    [string]$IsoPath,
    [string]$VmBaseFolder      = (Join-Path $PSScriptRoot ".vbox"),

    [int]$MemoryMb = 512,
    [int]$CpuCount = 1,
    [int]$VramMb   = 64,

    [ValidateSet("none", "vboxvga", "vmsvga", "vboxsvga")]
    [string]$GraphicsController = "vboxvga",

    [ValidateSet("none", "file", "pipe")]
    [string]$SerialMode = "file",
    [string]$SerialLogPath,

    [string]$OsType = "Other_64",
    [string]$VBoxManagePath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# --------------------------------------------------------------------------
# Path helpers
# --------------------------------------------------------------------------

function Get-FullPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $Path
    }
    try {
        return [System.IO.Path]::GetFullPath($Path)
    }
    catch {
        return $Path
    }
}

# --------------------------------------------------------------------------
# VBoxManage resolution + invocation
# --------------------------------------------------------------------------

function Resolve-VBoxManage {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath)) {
            throw "VBoxManage not found at explicit path: $ExplicitPath"
        }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $fromPath = Get-Command "VBoxManage.exe" -ErrorAction SilentlyContinue
    if ($fromPath) {
        return $fromPath.Source
    }

    $candidates = @()
    if ($env:VBOX_MSI_INSTALL_PATH) {
        $candidates += (Join-Path $env:VBOX_MSI_INSTALL_PATH "VBoxManage.exe")
    }
    if ($env:VBOX_INSTALL_PATH) {
        $candidates += (Join-Path $env:VBOX_INSTALL_PATH "VBoxManage.exe")
    }
    $candidates += "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"
    $candidates += "${env:ProgramFiles}\Oracle\VirtualBox\VBoxManage.exe"

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "VBoxManage.exe could not be found. Install VirtualBox or pass -VBoxManagePath."
}

function Invoke-VBox {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$AllowFail,
        [switch]$Quiet
    )

    # Native stderr (merged via 2>&1) must not trip ErrorActionPreference=Stop.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $raw = & $script:VBoxManage @Arguments 2>&1
    }
    finally {
        $ErrorActionPreference = $prevEap
    }
    $exitCode = $LASTEXITCODE

    $lines = @()
    foreach ($item in $raw) {
        if ($item -is [System.Management.Automation.ErrorRecord]) {
            $lines += $item.ToString()
        }
        else {
            $lines += [string]$item
        }
    }

    if (-not $Quiet) {
        foreach ($line in $lines) {
            Write-Host "    $line"
        }
    }

    if (($exitCode -ne 0) -and (-not $AllowFail)) {
        throw ("VBoxManage " + ($Arguments -join " ") + " failed (exit $exitCode):`n" + ($lines -join "`n"))
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output   = $lines
    }
}

# --------------------------------------------------------------------------
# VM introspection
# --------------------------------------------------------------------------

function Get-VBoxVersion {
    $res = Invoke-VBox -Arguments @("--version") -Quiet
    $raw = ""
    if ($res.Output.Count -gt 0) {
        $raw = ([string]$res.Output[0]).Trim()
    }
    $major = 0
    $minor = 0
    if ($raw -match '^(\d+)\.(\d+)') {
        $major = [int]$matches[1]
        $minor = [int]$matches[2]
    }
    return [pscustomobject]@{ Major = $major; Minor = $minor; Raw = $raw }
}

function Test-VmExists {
    param([string]$Name)

    $res = Invoke-VBox -Arguments @("list", "vms") -Quiet
    foreach ($line in $res.Output) {
        if ([string]$line -match '^"(.+)"\s+\{[0-9a-fA-F-]+\}\s*$') {
            if ($matches[1] -eq $Name) {
                return $true
            }
        }
    }
    return $false
}

function Get-VmInfo {
    param([string]$Name)

    $res = Invoke-VBox -Arguments @("showvminfo", $Name, "--machinereadable") -Quiet -AllowFail
    if ($res.ExitCode -ne 0) {
        return $null
    }

    $info = @{}
    foreach ($line in $res.Output) {
        $s = [string]$line
        $idx = $s.IndexOf('=')
        if ($idx -lt 1) {
            continue
        }
        $key = $s.Substring(0, $idx)
        $val = $s.Substring($idx + 1)
        if ($val.Length -ge 2 -and $val.StartsWith('"') -and $val.EndsWith('"')) {
            $val = $val.Substring(1, $val.Length - 2)
        }
        $info[$key] = $val
    }
    return $info
}

function Wait-VmUnlocked {
    param(
        [string]$Name,
        [int]$TimeoutSec = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $info = Get-VmInfo -Name $Name
        if ($null -eq $info) {
            return  # VM is gone
        }

        $state = ""
        if ($info.ContainsKey('VMState')) {
            $state = $info['VMState']
        }
        $session = ""
        if ($info.ContainsKey('SessionName') -and $info['SessionName']) {
            $session = $info['SessionName']
        }
        elseif ($info.ContainsKey('SessionType') -and $info['SessionType']) {
            $session = $info['SessionType']
        }

        $settled = ($state -eq 'poweroff' -or $state -eq 'aborted' -or $state -eq 'aborted-saved')
        if ($settled -and [string]::IsNullOrEmpty($session)) {
            return
        }
        Start-Sleep -Milliseconds 500
    }

    throw "Timed out waiting for VM '$Name' to power off and release its session lock."
}

# --------------------------------------------------------------------------
# Teardown
# --------------------------------------------------------------------------

function Close-RegisteredMedium {
    param(
        [ValidateSet("disk", "dvd")][string]$Kind,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $listArg = if ($Kind -eq 'disk') { 'hdds' } else { 'dvds' }
    $res = Invoke-VBox -Arguments @("list", $listArg) -Quiet -AllowFail
    if ($res.ExitCode -ne 0) {
        return
    }

    $targetFull = (Get-FullPath -Path $Path)
    $currentUuid = $null

    foreach ($line in $res.Output) {
        $s = [string]$line
        if ($s -match '^UUID:\s+(.+)$') {
            # Note: '^UUID:' deliberately does not match 'Parent UUID:'.
            $currentUuid = $matches[1].Trim()
        }
        elseif ($s -match '^Location:\s+(.+)$') {
            $locFull = (Get-FullPath -Path ($matches[1].Trim()))
            if ($currentUuid -and ($locFull -ieq $targetFull)) {
                Write-Host "  Closing $Kind medium $currentUuid"
                Invoke-VBox -Arguments @("closemedium", $Kind, $currentUuid) -Quiet -AllowFail | Out-Null
            }
        }
    }
}

function Remove-SharpOsVm {
    param(
        [string]$Name,
        [string]$VhdFullPath,
        [string]$IsoFullPath,
        [string]$BaseFolder
    )

    if (Test-VmExists -Name $Name) {
        Write-Host "Existing VM '$Name' found - tearing down..."

        $info = Get-VmInfo -Name $Name
        $state = "unknown"
        if ($info -and $info.ContainsKey('VMState')) {
            $state = $info['VMState']
        }
        Write-Host "  Current state: $state"

        if ($state -eq 'running' -or $state -eq 'paused' -or $state -eq 'stuck' -or $state -eq 'live snapshotting') {
            Write-Host "  Powering off..."
            Invoke-VBox -Arguments @("controlvm", $Name, "poweroff") -Quiet -AllowFail | Out-Null
        }
        elseif ($state -eq 'saved' -or $state -eq 'aborted-saved') {
            Write-Host "  Discarding saved state..."
            Invoke-VBox -Arguments @("discardstate", $Name) -Quiet -AllowFail | Out-Null
        }

        Write-Host "  Waiting for session lock to release..."
        Wait-VmUnlocked -Name $Name -TimeoutSec 30

        Write-Host "  Unregistering and deleting VM..."
        # --delete (NOT --delete-all): removes the VM's own files only,
        # leaving the build-output VHD/ISO untouched.
        $unreg = Invoke-VBox -Arguments @("unregistervm", $Name, "--delete") -Quiet -AllowFail
        if ($unreg.ExitCode -ne 0) {
            Write-Warning "unregistervm reported a problem; will still clean up the folder."
        }
    }
    else {
        Write-Host "No registered VM named '$Name'."
    }

    # The build script re-creates the VHD with a fresh UUID every time, so any
    # previously-registered medium MUST be closed (by UUID) or VirtualBox will
    # refuse the next attach. Done whether or not the VM existed.
    Close-RegisteredMedium -Kind "disk" -Path $VhdFullPath
    Close-RegisteredMedium -Kind "dvd"  -Path $IsoFullPath

    # An interrupted previous run can leave the VM folder behind without a
    # registry entry; that breaks the next createvm. Purge it.
    $vmFolder = Join-Path $BaseFolder $Name
    if (Test-Path -LiteralPath $vmFolder) {
        Write-Host "  Removing leftover VM folder: $vmFolder"
        Remove-Item -LiteralPath $vmFolder -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --------------------------------------------------------------------------
# Resolve VBoxManage early (needed for every path, including -Stop)
# --------------------------------------------------------------------------

$script:VBoxManage = Resolve-VBoxManage -ExplicitPath $VBoxManagePath
Write-Host "VBoxManage: $script:VBoxManage"

# --------------------------------------------------------------------------
# -Stop fast path
# --------------------------------------------------------------------------

if ($Stop) {
    if (Test-VmExists -Name $VmName) {
        Write-Host "Powering off VM '$VmName'..."
        Invoke-VBox -Arguments @("controlvm", $VmName, "poweroff") -Quiet -AllowFail | Out-Null
        Write-Host "Done."
    }
    else {
        Write-Host "VM '$VmName' is not registered - nothing to stop."
    }
    exit 0
}

# --------------------------------------------------------------------------
# Resolve all paths
# --------------------------------------------------------------------------

$OutputDir = Get-FullPath -Path $OutputDir
if ([string]::IsNullOrWhiteSpace($VhdPath)) {
    $VhdPath = Join-Path $OutputDir "sharpos-esp.vhd"
}
if ([string]::IsNullOrWhiteSpace($IsoPath)) {
    $IsoPath = Join-Path $OutputDir "sharpos-boot.iso"
}
if ([string]::IsNullOrWhiteSpace($SerialLogPath)) {
    $SerialLogPath = Join-Path $OutputDir "vbox-serial.log"
}

$VhdPath       = Get-FullPath -Path $VhdPath
$IsoPath       = Get-FullPath -Path $IsoPath
$SerialLogPath = Get-FullPath -Path $SerialLogPath
$VmBaseFolder  = Get-FullPath -Path $VmBaseFolder

$vbox = Get-VBoxVersion
Write-Host "VirtualBox version: $($vbox.Raw)"
Write-Host "VM name           : $VmName"
Write-Host "VHD               : $VhdPath"
if ($AttachIso) {
    Write-Host "ISO               : $IsoPath"
}
Write-Host "VM base folder    : $VmBaseFolder"
Write-Host ""

# --------------------------------------------------------------------------
# Step 1 - Teardown (must run BEFORE the build, so nothing holds the VHD)
# --------------------------------------------------------------------------

Write-Host "=== Teardown ==="
Remove-SharpOsVm -Name $VmName -VhdFullPath $VhdPath -IsoFullPath $IsoPath -BaseFolder $VmBaseFolder

if (($SerialMode -eq 'file') -and (Test-Path -LiteralPath $SerialLogPath)) {
    Write-Host "  Removing stale serial log: $SerialLogPath"
    Remove-Item -LiteralPath $SerialLogPath -Force -ErrorAction SilentlyContinue
}
Write-Host ""

# --------------------------------------------------------------------------
# Step 2 - Build disk images
# --------------------------------------------------------------------------

if ($SkipImageBuild) {
    Write-Host "=== Image build skipped (-SkipImageBuild) ==="
}
else {
    Write-Host "=== Building disk images ==="
    if (-not (Test-Path -LiteralPath $BuildImagesScript)) {
        throw "build_images script not found: $BuildImagesScript (pass -BuildImagesScript)."
    }

    $buildArgs = @{
        Configuration = $Configuration
        OutputDir     = $OutputDir
        VhdPath       = $VhdPath
        IsoPath       = $IsoPath
    }
    if ($NoCompile) {
        $buildArgs.NoBuild = $true
    }
    if (-not ($BuildIso -or $AttachIso)) {
        $buildArgs.NoIso = $true
    }

    & $BuildImagesScript @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "build_images.ps1 failed with exit code $LASTEXITCODE"
    }
}
Write-Host ""

# --------------------------------------------------------------------------
# Step 3 - Verify artifacts
# --------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $VhdPath)) {
    throw "VHD not found after build: $VhdPath`nIf the build outputs elsewhere, pass -VhdPath explicitly."
}
if ($AttachIso -and -not (Test-Path -LiteralPath $IsoPath)) {
    throw "ISO not found after build: $IsoPath`nIf the build outputs elsewhere, pass -IsoPath explicitly."
}

# Serial log needs an existing parent directory before the VM starts.
$serialDir = Split-Path -Parent $SerialLogPath
if ($serialDir -and -not (Test-Path -LiteralPath $serialDir)) {
    New-Item -ItemType Directory -Path $serialDir -Force | Out-Null
}

# --------------------------------------------------------------------------
# Step 4 - Create VM
# --------------------------------------------------------------------------

Write-Host "=== Creating VM ==="

$createArgs = @(
    "createvm",
    "--name",       $VmName,
    "--ostype",     $OsType,
    "--basefolder", $VmBaseFolder,
    "--register"
)
# --platform-architecture exists from VirtualBox 7.1 onwards.
if (($vbox.Major -gt 7) -or ($vbox.Major -eq 7 -and $vbox.Minor -ge 1)) {
    $createArgs += @("--platform-architecture", "x86")
}
Invoke-VBox -Arguments $createArgs | Out-Null

# --------------------------------------------------------------------------
# Step 5 - Configure VM (mirror of the QEMU machine profile)
# --------------------------------------------------------------------------

Write-Host "=== Configuring VM ==="

$modifyArgs = @(
    "modifyvm", $VmName,
    "--firmware",            "efi64",      # OVMF in QEMU -> built-in EFI here
    "--chipset",             "ich9",       # QEMU -machine q35
    "--memory",              "$MemoryMb",  # QEMU -m 512
    "--cpus",                "$CpuCount",
    "--ioapic",              "on",         # required for EFI / >1 CPU
    "--pae",                 "on",
    "--nestedpaging",        "on",
    "--graphicscontroller",  $GraphicsController,
    "--vram",                "$VramMb",
    "--nic1",                "none",       # QEMU -net none
    "--mouse",               "ps2",
    "--keyboard",            "ps2",
    "--boot1",               "disk",
    "--boot2",               "dvd",
    "--boot3",               "none",
    "--boot4",               "none"
)
Invoke-VBox -Arguments $modifyArgs | Out-Null

# Serial port - the SharpOS console is mirrored to COM1 (0x3F8, IRQ 4),
# exactly like QEMU's -serial. 'file' captures it to a log next to the images.
switch ($SerialMode) {
    'file' {
        Write-Host "  Serial COM1 -> file: $SerialLogPath"
        Invoke-VBox -Arguments @(
            "modifyvm", $VmName,
            "--uart1", "0x3F8", "4",
            "--uartmode1", "file", $SerialLogPath
        ) | Out-Null
    }
    'pipe' {
        $pipeName = "\\.\pipe\$VmName"
        Write-Host "  Serial COM1 -> named pipe: $pipeName"
        Invoke-VBox -Arguments @(
            "modifyvm", $VmName,
            "--uart1", "0x3F8", "4",
            "--uartmode1", "server", $pipeName
        ) | Out-Null
    }
    'none' {
        Invoke-VBox -Arguments @("modifyvm", $VmName, "--uart1", "off") | Out-Null
    }
}

# --------------------------------------------------------------------------
# Step 6 - Storage
# --------------------------------------------------------------------------

Write-Host "=== Attaching storage ==="

Invoke-VBox -Arguments @(
    "storagectl", $VmName,
    "--name",       "SATA",
    "--add",        "sata",
    "--controller", "IntelAhci",
    "--portcount",  "2",
    "--bootable",   "on"
) | Out-Null

Write-Host "  Port 0: VHD  -> $VhdPath"
Invoke-VBox -Arguments @(
    "storageattach", $VmName,
    "--storagectl", "SATA",
    "--port",       "0",
    "--device",     "0",
    "--type",       "hdd",
    "--medium",     $VhdPath
) | Out-Null

if ($AttachIso) {
    Write-Host "  Port 1: ISO  -> $IsoPath"
    Invoke-VBox -Arguments @(
        "storageattach", $VmName,
        "--storagectl", "SATA",
        "--port",       "1",
        "--device",     "0",
        "--type",       "dvddrive",
        "--medium",     $IsoPath
    ) | Out-Null
}

# --------------------------------------------------------------------------
# Step 7 - Launch
# --------------------------------------------------------------------------

$startType = if ($Headless) { "headless" } else { "gui" }
Write-Host ""
Write-Host "=== Launching VM ($startType) ==="
Invoke-VBox -Arguments @("startvm", $VmName, "--type", $startType) | Out-Null

Write-Host ""
Write-Host "Done. VM '$VmName' is running."
if ($SerialMode -eq 'file') {
    Write-Host "Serial log: $SerialLogPath"
}
elseif ($SerialMode -eq 'pipe') {
    Write-Host "Serial pipe: \\.\pipe\$VmName  (connect a pipe client to read COM1)"
}
Write-Host "Stop it with:  .\$(Split-Path -Leaf $PSCommandPath) -Stop"
