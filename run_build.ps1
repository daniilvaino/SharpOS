param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    # step113-followup: which CoreCLR fork build the kernel links + ships.
    # Debug = _DEBUG asserts + unoptimized (what we stabilized on).
    # Release = no asserts, optimized, closer to shipping .NET. Must have
    # been built first: .\dotnet-runtime-sharpos\build_clr_sharpos.ps1 -Configuration Release
    [ValidateSet("Debug", "Release")]
    [string]$ForkConfig = "Debug",
    # Kernel-only build: skip CoreCLR linking + CoreClrProbe compilation.
    # Resulting BOOTX64.EFI hosts no managed app — used for measuring the
    # bare kernel image size.
    [switch]$SkipCoreClr,
    [switch]$NoRun,
    [switch]$Stop,
    # Trace faults instead of letting the machine die quietly. When output
    # stops mid-line and nothing follows, a triple fault and a hang look
    # exactly alike from the serial port; this tells them apart in one run.
    # Writes .qemu\qemu-debug.log (interrupts, CPU resets, guest errors) and
    # keeps QEMU alive after a reset so the log survives to be read.
    [switch]$TraceFaults,
    [int]$QmpPort = 4444,
    # Reproduce the hardware the kernel actually meets: no PS/2 controller,
    # a USB keyboard instead. Firmware still provides input pre-EBS; our own
    # post-EBS driver is PS/2-only, so the launcher goes deaf exactly as it
    # does on the test machines.
    [switch]$NoPs2,
    # Attach an xHCI controller with a keyboard and mouse, keeping PS/2 intact.
    # Target to develop the USB stack against: recent machines are usually
    # xHCI-only, while QEMU's q35 does not expose one unless asked.
    [switch]$Usb,
    # Boot with the ESP on a USB stick and no SATA disk at all — the shape of
    # the test machines, where there is no AHCI controller to fall back on.
    [switch]$UsbOnly,
    # Framebuffer mode to ask the firmware for, e.g. "3840x2160".
    [string]$Resolution,
    # ESP staged as a real FAT32 image; 253 MB of payload today, so 512 leaves headroom.
    [int]$EspImageSizeMb = 512,
    [string]$QemuExe,
    [string]$OvmfCode,
    [string]$OvmfVars
)

$ErrorActionPreference = "Stop"

# Force MSVC toolchain (cl.exe, link.exe) to emit messages in English
# (VSLANG=1033 = en-US). Effective only if the English MSVC language pack
# is installed; on a Russian-only install the tools fall back to localized
# (CP866) text, so the UTF-8 console setup below is what actually keeps the
# log readable.
$env:VSLANG = "1033"

# Encoding hygiene. On a localized (RU) Windows the MSVC tools write their
# output in the OEM code page (CP866); captured through the default console
# encoding and then teed to a UTF-16LE file, link/compiler errors come out
# as "каракули". Switch the whole pipeline to UTF-8: chcp 65001 makes the
# MSVC tools (which honour the console code page) emit UTF-8, PowerShell
# captures it as UTF-8, and every file write below defaults to UTF-8 too.
# Result: last_build.log is portable UTF-8 that reads cleanly everywhere.
$env:DOTNET_CLI_UI_LANGUAGE = "en"
$env:PYTHONIOENCODING = "utf-8"
try { chcp 65001 > $null } catch { }
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    [Console]::InputEncoding  = [System.Text.Encoding]::UTF8
} catch { }   # no interactive console (redirected / CI) — chcp already covers native tools
$OutputEncoding = [System.Text.Encoding]::UTF8
$PSDefaultParameterValues['Out-File:Encoding']    = 'utf8'
$PSDefaultParameterValues['Tee-Object:Encoding']  = 'utf8'
$PSDefaultParameterValues['Set-Content:Encoding'] = 'utf8'
$PSDefaultParameterValues['Add-Content:Encoding'] = 'utf8'

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
$efiProjectDir = Join-Path $repoRoot "OS"
$projectFile = Join-Path $efiProjectDir "OS.csproj"
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

# Compose BuildId: <git-short-sha>[-<tag-from-build-tag.txt>]
# Tag is a free-form label the user can put in build-tag.txt (in repo root);
# leave the file empty to show only the SHA. If git is unavailable, fall back
# to "local". The resulting value is passed into dotnet publish as
# /p:BuildId=..., which OS.csproj turns into a generated BuildInfo.g.cs.
$buildId = "local"
$gitSha = (& git -C $repoRoot rev-parse --short HEAD 2>$null)
if ($LASTEXITCODE -eq 0 -and $gitSha) {
    $buildId = $gitSha.Trim()
}
$tagFile = Join-Path $repoRoot "build-tag.txt"
if (Test-Path -LiteralPath $tagFile) {
    $tag = (Get-Content -LiteralPath $tagFile -Raw).Trim()
    if ($tag) {
        $buildId = "$buildId-$tag"
    }
}

# CoffStub.Generator hosts an MSBuild task that OS.csproj imports through a
# .targets file rather than a ProjectReference, so — unlike BootAsm.Generator —
# nothing builds it implicitly. On a fresh clone the publish below fails with
# MSB4062 ("BootAsm.EmitCoffStubsTask could not be loaded"). Build it first;
# the step is incremental and costs nothing once it is up to date.
$coffStubProj = Join-Path $repoRoot "bootasm\CoffStub.Generator\CoffStub.Generator.csproj"
if (Test-Path -LiteralPath $coffStubProj) {
    Write-Host "Building CoffStub.Generator (MSBuild task host)..."
    # Output captured, not discarded: a bare exit code says nothing, and the
    # usual cause here is a file lock (a VM still holding the DLL, a parallel
    # build) whose message names the file. Shown only on failure so a good
    # build stays quiet.
    $coffStubLog = & dotnet build $coffStubProj -c Release --nologo -v quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        $coffStubLog | ForEach-Object { Write-Output $_ }
        throw "CoffStub.Generator build failed with exit code $LASTEXITCODE"
    }
}

Write-Host "Building OS ($Configuration, BuildId=$buildId)..."
Push-Location $efiProjectDir
try {
    $skipArg = if ($SkipCoreClr) { "/p:SkipCoreClr=true" } else { "" }
    & dotnet publish $projectFile -c $Configuration -r win-x64 "/p:BuildId=$buildId" "/p:CoreClrForkConfig=$ForkConfig" $skipArg
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$publishDir = Join-Path $efiProjectDir "bin\$Configuration\$targetFramework\win-x64\publish"
$builtEfi = Join-Path $publishDir "OS.exe"
if (-not (Test-Path -LiteralPath $builtEfi)) {
    $builtEfi = Get-ChildItem -LiteralPath $publishDir -Filter *.exe -File -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $builtEfi -or -not (Test-Path -LiteralPath $builtEfi)) {
    throw "Built EFI binary not found in: $publishDir"
}

$espBootDir = Join-Path $qemuWorkDir "esp\EFI\BOOT"
New-Item -ItemType Directory -Force -Path $espBootDir | Out-Null

# Applications and the data they open live in \apps, not beside the
# firmware entry point. \EFI\BOOT is where UEFI looks for BOOTX64.EFI, and
# nothing else belongs there — a launcher listing that folder was offering
# the bootloader as if it were something to run.
$espAppsDir = Join-Path $qemuWorkDir "esp\apps"
New-Item -ItemType Directory -Force -Path $espAppsDir | Out-Null
$bootx64 = Join-Path $espBootDir "BOOTX64.EFI"
Copy-Item -LiteralPath $builtEfi -Destination $bootx64 -Force

# CoreCLR expects System.Private.CoreLib.dll at path \sharpos\System.Private.CoreLib.dll
# (our Path.GetFullPath prepends "\sharpos\" to relative paths). Place the DLL there
# on the EFI partition so CreateFileW -> SharpOSHost_FileOpen -> Platform.TryReadFile
# via UEFI SimpleFileSystem can find it.
$spcDll = Join-Path (Join-Path $repoRoot "dotnet-runtime-sharpos\artifacts\bin\coreclr\windows.x64.$ForkConfig") "System.Private.CoreLib.dll"
$espSharpOSDir = Join-Path $qemuWorkDir "esp\sharpos"
if (Test-Path -LiteralPath $spcDll) {
    New-Item -ItemType Directory -Force -Path $espSharpOSDir | Out-Null
    Copy-Item -LiteralPath $spcDll -Destination (Join-Path $espSharpOSDir "System.Private.CoreLib.dll") -Force
    Write-Host "Prepared CoreCLR BCL: \sharpos\System.Private.CoreLib.dll"
}
else {
    Write-Warning "System.Private.CoreLib.dll not found at $spcDll - CoreCLR init will fail with FILE_NOT_FOUND"
}
# Create directories that SharpOSHost_GetSystemString reports as existing.
# GetTempPath → "C:\sharpos\tmp\", GetSystemDirectory → "C:\sharpos\system32".
# PS FileSystemProvider validates these at PSDrive auto-mount; if a path is
# reported but missing on disk, the entire PSDrive init throws and C: never
# registers → all subsequent module discovery dies (no PSDrive to resolve
# $PSHome\Modules against).
New-Item -ItemType Directory -Force -Path (Join-Path $espSharpOSDir "tmp")      | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $espSharpOSDir "system32") | Out-Null
Write-Host "Prepared empty dirs: \sharpos\tmp\ \sharpos\system32\"

# PSReadLine reads its saved history on the first Up-arrow. The file is created
# on demand by a normal host, but we have no writable filesystem yet, so stage an
# empty one: a missing file makes every history navigation re-probe the path.
$psrlHistoryDir  = Join-Path $espSharpOSDir "Microsoft\Windows\PowerShell\PSReadLine"
$psrlHistoryFile = Join-Path $psrlHistoryDir "ConsoleHost_history.txt"
New-Item -ItemType Directory -Force -Path $psrlHistoryDir | Out-Null
if (-not (Test-Path -LiteralPath $psrlHistoryFile)) {
    New-Item -ItemType File -Path $psrlHistoryFile | Out-Null
}
# Boot log target: the kernel mirrors every console line into this file, one
# sector per line. Pre-staged at a fixed size because writing in place needs
# no cluster allocation - 16 MiB is ~32k lines, far more than a boot produces.
$bootLog = Join-Path $espSharpOSDir "bootlog.txt"
# Written as 256 copies of a 64 KiB blank chunk. A per-byte loop over 16M
# elements takes minutes, and [Array]::Fill would be one call but does not
# exist on .NET Framework, so it breaks under Windows PowerShell 5.1.
$blankChunk = New-Object byte[] (64 * 1024)
for ($i = 0; $i -lt $blankChunk.Length; $i++) { $blankChunk[$i] = 0x20 }
$bootLogStream = [System.IO.File]::Create($bootLog)
try {
    for ($i = 0; $i -lt 256; $i++) { $bootLogStream.Write($blankChunk, 0, $blankChunk.Length) }
} finally {
    $bootLogStream.Dispose()
}
Write-Host "Prepared boot log: bootlog.txt (16 MiB)"

# Target for the filesystem write probe: a file of known size and content
# that the kernel overwrites in place. Staged fresh every run so a previous
# run's pattern cannot be mistaken for a successful write.
$fsWriteProbe = Join-Path $espSharpOSDir "fswrite.bin"
[System.IO.File]::WriteAllBytes($fsWriteProbe, ([byte[]]@(65) * 512))
Write-Host "Prepared FS write probe: fswrite.bin (512 x 'A')"

Write-Host "Prepared PSReadLine history: \sharpos\Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt"

# ...and tell PSReadLine not to write it back. The media is read-only, so every
# accepted command otherwise ends in "Access to the path ... is denied" printed
# in red. $PSHome\profile.ps1 is the all-hosts profile; it is already probed at
# startup. Guarded, because a throwing profile would break the prompt itself.
$espProfile = Join-Path $espSharpOSDir "pwsh\profile.ps1"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $espProfile) | Out-Null
@'
# SharpOS: the boot media is read-only, so history cannot be persisted.
# Keep in-session history (Up-arrow still works) but never touch the file.
try { Set-PSReadLineOption -HistorySaveStyle SaveNothing -ErrorAction Stop } catch { }
'@ | Set-Content -LiteralPath $espProfile -Encoding UTF8
Write-Host "Prepared PS profile: \sharpos\pwsh\profile.ps1 (history SaveNothing)"

# Stock PowerShell module manifests: required for built-in cmdlet registration.
# Microsoft.PowerShell.Utility (Write-Output etc.), Microsoft.PowerShell.Management
# (Get-ChildItem etc.), Microsoft.PowerShell.Security/Diagnostics — all import via
# Modules/<Name>/<Name>.psd1 at PS startup. Without these manifests every cmdlet
# lookup ends in "is not recognized" — even though the .dll is in TPA.
$stockPwshModules = "C:\Program Files\PowerShell\7\Modules"
$espPwshModules   = Join-Path $espSharpOSDir "pwsh\Modules"
if (Test-Path -LiteralPath $stockPwshModules) {
    New-Item -ItemType Directory -Force -Path $espPwshModules | Out-Null
    Copy-Item -LiteralPath $stockPwshModules -Destination (Join-Path $espSharpOSDir "pwsh") -Recurse -Force
    $modCount = (Get-ChildItem -LiteralPath $espPwshModules -Directory).Count
    Write-Host "Prepared pwsh modules: \sharpos\pwsh\Modules\ ($modCount modules)"
}
else {
    Write-Warning "Stock PowerShell Modules dir not found at $stockPwshModules - cmdlets will not register"
}

# Stage A — byte-for-byte NORMAL dotnet program hosting.
#
# A genuinely-normal `dotnet build` console app (no -nostdlib, no -r:forkSPC)
# references System.Runtime + System.Console v10.0.0.0. The fork's full
# Microsoft.NETCore.App (coreclr-pack/Debug/net10.0) is also v10.0.0.0 — exact
# version match, so the SAME binary that runs via `dotnet`/`corerun` on Windows
# binds & runs in SharpOS. We are our own hostfxr/hostpolicy: ship the fx set,
# generate the TPA list, host via coreclr_execute_assembly.
#
#   \sharpos\System.Private.CoreLib.dll  — proven SPC (windows.x64.Debug)
#   \sharpos\fx\*.dll                    — rest of framework (171, ex-SPC)
#   \sharpos\NormalHello.dll             — stock dotnet build artifact
#   \sharpos\tpa.txt                     — newline/semicolon TPA list (host reads)
# step125: Use the Windows-built BCL assemblies from the fork's
# crossgen2_publish directory. These are the same BCL DLLs as in
# coreclr-pack/linux-x64 BUT compiled `-os windows`, so Win32-targeted
# types (Microsoft.Win32.Registry, System.Net.Sockets, etc.) contain
# real implementations that talk to our advapi32/ws2_32/etc. stubs
# instead of PNSE throw bodies.
# Filter set comes from coreclr-pack (linux-x64) — same 171 BCL names —
# so we don't pull tooling assemblies (ILCompiler.*, crossgen2.*) that
# happen to live next to BCL in crossgen2_publish.
$forkFxNames  = Join-Path $repoRoot "dotnet-runtime-sharpos\artifacts\bin\coreclr-pack\Debug\net10.0\linux-x64"
$forkFxWinSrc = Join-Path $repoRoot "dotnet-runtime-sharpos\artifacts\bin\crossgen2_publish\x64\Release"
$fxDest   = Join-Path $espSharpOSDir "fx"
$normalProj = Join-Path $repoRoot "apps_managed\normal-hello"
$normalDllSrc = Join-Path $normalProj "bin\Release\net10.0\NormalHello.dll"
# step128 — PowerShell bootstrap shim. A managed wrapper that reflection-
# sets SystemPolicy.s_systemLockdownPolicy = None before invoking
# ManagedPSEntry.Main(). Lets PS 7.5 run in FullLanguage mode on bare
# metal (CLM detection in PS 7.5 has no env-var override). See
# apps_managed/PowerShellBootstrap/Program.cs for the override logic.
$psBootstrapProj   = Join-Path $repoRoot "apps_managed\PowerShellBootstrap"
$psBootstrapDllSrc = Join-Path $psBootstrapProj "bin\Release\net10.0\PowerShellBootstrap.dll"
if (Test-Path -LiteralPath $forkFxNames) {
    New-Item -ItemType Directory -Force -Path $fxDest | Out-Null
    $copiedFromWin = 0
    $copiedFromLinux = 0
    Get-ChildItem -LiteralPath $forkFxNames -Filter *.dll |
        Where-Object { $_.Name -ne "System.Private.CoreLib.dll" } |
        ForEach-Object {
            $name = $_.Name
            $winSrc = Join-Path $forkFxWinSrc $name
            if (Test-Path -LiteralPath $winSrc) {
                Copy-Item -LiteralPath $winSrc -Destination (Join-Path $fxDest $name) -Force
                $copiedFromWin++
            } else {
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $fxDest $name) -Force
                $copiedFromLinux++
            }
        }
    $fxCount = (Get-ChildItem -LiteralPath $fxDest -Filter *.dll).Count
    Write-Host "Prepared framework: \sharpos\fx\ ($fxCount dll, Win-impl=$copiedFromWin, Linux-fallback=$copiedFromLinux)"

    # Build the normal app (stock SDK, normal references). If project missing,
    # create a vanilla `dotnet new console`.
    if (-not (Test-Path -LiteralPath (Join-Path $normalProj "NormalHello.csproj"))) {
        New-Item -ItemType Directory -Force -Path $normalProj | Out-Null
        Push-Location $normalProj; & dotnet new console -n NormalHello -o . | Out-Null; Pop-Location
    }
    Push-Location $normalProj; & dotnet build -c Release | Out-Null; Pop-Location
    if (Test-Path -LiteralPath $normalDllSrc) {
        Copy-Item -LiteralPath $normalDllSrc -Destination (Join-Path $espSharpOSDir "NormalHello.dll") -Force
        $h = (Get-FileHash -LiteralPath $normalDllSrc -Algorithm SHA256).Hash
        Write-Host "Prepared NormalHello.dll (stock dotnet build) sha256=$h"
    } else {
        Write-Warning "NormalHello.dll not found at $normalDllSrc"
    }

    # Build PowerShellBootstrap shim — managed wrapper that forces
    # SystemPolicy → None via reflection, then forwards to ManagedPSEntry.
    if (Test-Path -LiteralPath (Join-Path $psBootstrapProj "PowerShellBootstrap.csproj")) {
        Push-Location $psBootstrapProj; & dotnet build -c Release | Out-Null; Pop-Location
        if (Test-Path -LiteralPath $psBootstrapDllSrc) {
            Copy-Item -LiteralPath $psBootstrapDllSrc -Destination (Join-Path $espSharpOSDir "PowerShellBootstrap.dll") -Force
            $bh = (Get-FileHash -LiteralPath $psBootstrapDllSrc -Algorithm SHA256).Hash
            Write-Host "Prepared PowerShellBootstrap.dll sha256=$bh"
        } else {
            Write-Warning "PowerShellBootstrap.dll not found at $psBootstrapDllSrc"
        }
    } else {
        Write-Warning "PowerShellBootstrap project not found at $psBootstrapProj"
    }

    # Benchmarks (step 168): a stock console app, run from the launcher when a
    # number is wanted. Its [perf] lines and the kernel's are gathered by
    # tools/perf_report.ps1.
    $benchProj   = Join-Path $repoRoot "apps_managed\Bench"
    $benchDllSrc = Join-Path $benchProj "bin\Release\net10.0\Bench.dll"
    if (Test-Path -LiteralPath (Join-Path $benchProj "Bench.csproj")) {
        Push-Location $benchProj; & dotnet build -c Release | Out-Null; Pop-Location
        if (Test-Path -LiteralPath $benchDllSrc) {
            Copy-Item -LiteralPath $benchDllSrc -Destination (Join-Path $espSharpOSDir "Bench.dll") -Force
            $bh = (Get-FileHash -LiteralPath $benchDllSrc -Algorithm SHA256).Hash
            Write-Host "Prepared Bench.dll sha256=$bh"
        } else {
            Write-Warning "Bench.dll not found at $benchDllSrc"
        }
    }

    # Generate TPA list: SPC (root) + every fx dll + every pwsh/* dll + the
    # app. Semicolon-sep, virtual-drive C:\sharpos\ paths so BCL's
    # Path.IsPathFullyQualified accepts them. SharpOSHost_FileOpen strips the
    # C:\ prefix transparently.
    #
    # All pwsh/*.dll are added so PowerShell-internal assembly resolution
    # finds them by NAME via TPABinder (CoreCLR picks the path from TPA).
    # Without pwsh/* in TPA, PowerShell falls back to constructing paths
    # itself ($PSHome + filename) and hits a Path.Join bug that doubles the
    # prefix into "C:\sharpos\C:\sharpos\pwsh\X.dll".
    $tpa = New-Object System.Text.StringBuilder
    [void]$tpa.Append('C:\sharpos\System.Private.CoreLib.dll')
    $fxNames = @{}
    Get-ChildItem -LiteralPath $fxDest -Filter *.dll | ForEach-Object {
        [void]$tpa.Append(';C:\sharpos\fx\' + $_.Name)
        $fxNames[$_.Name] = $true
    }
    # Add pwsh/*.dll skipping (a) the duplicate SPC and (b) any dll already
    # provided by fx/ (169 of 300 pwsh dlls overlap with fx — those keep
    # the fx variant; CoreCLR would honor the first TPA entry anyway).
    $pwshDest = Join-Path $espSharpOSDir "pwsh"
    if (Test-Path -LiteralPath $pwshDest) {
        Get-ChildItem -LiteralPath $pwshDest -Filter *.dll | ForEach-Object {
            if ($_.Name -eq 'System.Private.CoreLib.dll') { return }
            if ($fxNames.ContainsKey($_.Name)) { return }
            [void]$tpa.Append(';C:\sharpos\pwsh\' + $_.Name)
        }
    }
    [void]$tpa.Append(';C:\sharpos\NormalHello.dll')
    [void]$tpa.Append(';C:\sharpos\PowerShellBootstrap.dll')
    [void]$tpa.Append(';C:\sharpos\Bench.dll')
    [System.IO.File]::WriteAllText((Join-Path $espSharpOSDir "tpa.txt"), $tpa.ToString())
    Write-Host "Prepared \sharpos\tpa.txt (length=$($tpa.Length))"
}
else {
    Write-Warning "fork fx not found at $forkFx - Stage A normal hosting unavailable"
}
# step137: ELF apps removed. No ELF images are generated or staged anymore;
# actively delete any stale ELF images + .abi sidecars from a prior ESP so the
# launcher only ever sees PE apps. (Fetch is dormant until its PE migration.)
foreach ($staleElf in @("HELLO.ELF", "ABIINFO.ELF", "MARKER.ELF", "HELLOCS.ELF", "FETCH.ELF", "APP.ELF")) {
    $stalePath = Join-Path $espBootDir $staleElf
    if (Test-Path -LiteralPath $stalePath) { Remove-Item -LiteralPath $stalePath -Force }
    if (Test-Path -LiteralPath "$stalePath.abi") { Remove-Item -LiteralPath "$stalePath.abi" -Force }
}

# step137/138: freestanding win-x64 PE apps (built by build_launcher.ps1 /
# build_fetch.ps1 / build_aottests.ps1). Stage each to ESP as <NAME>.EXE; the
# kernel dispatches on the MZ magic to PeLoader. Absent build output just skips
# (that app won't appear in the launcher).
#
# The .abi sidecar is gone: an app's ABI record now travels inside the file, in
# its own manifest resource (apps_native/sdk/SharpAppManifest.props). Two files
# could be separated by a copy, and a missing one meant a silent fall back to
# V1 — the failure looked like the app misbehaving rather than like a file left
# behind. Stale sidecars from earlier builds are deleted below.
$peApps = @(
    @{ Src = "apps_native\FetchApp\bin\Release\out-win-x64\FetchApp.exe";         Dest = "FETCH.EXE" },
    @{ Src = "apps_native\AotTests\bin\Release\out-win-x64\AotTests.exe";         Dest = "AOTTESTS.EXE" },
    @{ Src = "apps_native\GPL_AHEAD_WARNING_DOOM_managed\bin\Release\out-win-x64\DoomApp.exe"; Dest = "DOOM.EXE" },
    @{ Src = "apps_native\TriCNES\bin\Release\out-win-x64\TriCNESApp.exe";        Dest = "TRICNES.EXE" },
    @{ Src = "apps_native\Fami\bin\Release\out-win-x64\FamiApp.exe";              Dest = "FAMI.EXE" },
    @{ Src = "apps_native\Launcher\bin\Release\out-win-x64\Launcher.exe";         Dest = "LAUNCHER.EXE" }
)
foreach ($peApp in $peApps) {
    $peSrc = Join-Path $repoRoot $peApp.Src
    $peDst = Join-Path $espAppsDir $peApp.Dest

    # Anything left where apps used to be staged: remove it, or the launcher
    # would list two copies and run whichever it found first.
    $peStale = Join-Path $espBootDir $peApp.Dest
    if (Test-Path -LiteralPath $peStale) { Remove-Item -LiteralPath $peStale -Force }
    if (Test-Path -LiteralPath $peSrc) {
        Copy-Item -LiteralPath $peSrc -Destination $peDst -Force
        Write-Host "Prepared app PE: $peDst"
    }
    if (Test-Path -LiteralPath "$peDst.abi") {
        Remove-Item -LiteralPath "$peDst.abi" -Force
    }
}

# Payload staging. Game data — IWADs, cartridges — lives in payloads\ at the
# repo root, one folder for everything a build needs to hand to the apps. The
# tree is gitignored: none of it is repo material, and some of it is not ours
# to distribute. See payloads\README.md.
#
# Routed to the ESP by extension, because the extension already says what the
# file is and asking anyone to remember a second rule is how folders get put in
# the wrong place:
#   *.wad -> \apps\<NAME>.WAD      (ManagedDoom probes there for known IWADs)
#   *.nes -> \apps\GAME.NES        (the fixed name both emulators open)
#
# One cartridge is staged, the first by name, because the apps have no way to
# be told which to load yet. That is the argument-passing work; when it lands,
# all of them get staged under their own names and this collapses to a copy.
$payloadDir = Join-Path $repoRoot "payloads"
if (Test-Path -LiteralPath $payloadDir) {
    foreach ($wad in Get-ChildItem -LiteralPath $payloadDir -Filter "*.wad") {
        $wadDst = Join-Path $espAppsDir $wad.Name.ToUpperInvariant()
        Copy-Item -LiteralPath $wad.FullName -Destination $wadDst -Force
        Write-Host "Prepared IWAD: $wadDst"
    }

    $roms = @(Get-ChildItem -LiteralPath $payloadDir -Filter "*.nes" | Sort-Object Name)
    if ($roms.Count -gt 0) {
        Copy-Item -LiteralPath $roms[0].FullName -Destination (Join-Path $espAppsDir "GAME.NES") -Force
        Write-Host "Prepared cartridge: GAME.NES ($($roms[0].Name))"
        # Say what was left behind rather than staging it silently: "I dropped
        # the ROM in and got the other game" is otherwise a mystery.
        if ($roms.Count -gt 1) {
            Write-Host "  ($($roms.Count - 1) more .nes in payloads\ not staged - one cartridge slot)"
        }
    }
}

# Game data from earlier builds, back when applications were staged beside the
# firmware entry point. Left there it would be dead weight on the image, and the
# kind that reads as "the WAD is on the disk" while the app looks elsewhere.
foreach ($stalePayload in @(Get-ChildItem -LiteralPath $espBootDir -Filter "*.WAD" -ErrorAction SilentlyContinue)) {
    Remove-Item -LiteralPath $stalePayload.FullName -Force
}
$staleRom = Join-Path $espBootDir "GAME.NES"
if (Test-Path -LiteralPath $staleRom) { Remove-Item -LiteralPath $staleRom -Force }

# PowerShell distribution. Staged from payloads\pwsh\<dist>\ rather than
# copied by hand once, because which build is on the image decides whether its
# precompiled code is used at all: assemblies carry a ReadyToRun format version,
# and the runtime loads only its own. A 7.5 distribution (format 10) against
# this runtime (format 16, .NET 10) has every one of its assemblies rejected,
# and System.Management.Automation alone is 19 MB that then gets compiled from
# scratch on every start — the long startup and the pause on a first-time
# command both came from exactly that.
#
# System.Private.CoreLib is deliberately NOT taken from the distribution: that
# assembly and the runtime binary are one unit, built together and agreeing on
# internal layout. Ours stays.
$pwshDist = Get-ChildItem -LiteralPath (Join-Path $repoRoot "payloads\pwsh") -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending | Select-Object -First 1
if ($pwshDist) {
    $pwshEsp = Join-Path $espSharpOSDir "pwsh"
    New-Item -ItemType Directory -Force -Path $pwshEsp | Out-Null
    $staged = 0
    foreach ($f in Get-ChildItem -LiteralPath $pwshDist.FullName -File) {
        if ($f.Name -eq "System.Private.CoreLib.dll") { continue }
        Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $pwshEsp $f.Name) -Force
        $staged++
    }
    Write-Host "Prepared PowerShell: $($pwshDist.Name) -> \sharpos\pwsh\ ($staged files, CoreLib kept ours)"
}


Write-Host "Prepared EFI image: $bootx64"
if ($NoRun) {
    Write-Host "NoRun set: build finished, QEMU launch skipped."
    exit 0
}

Write-Host "Launching QEMU..."
Write-Host "Firmware: $OvmfCode"
Write-Host "COM1 is attached to this terminal (-serial mon:stdio)."
# COM3 carries what programs print (the launcher, its children, PowerShell,
# the census); COM1 keeps the kernel log. Kept in a file next to last_build.log
# rather than on this terminal: two streams on one console are the mix this
# split removes.
$appLog = Join-Path $repoRoot "last_app.log"
$errLog = Join-Path $repoRoot "last_err.log"
Write-Host "COM3 (program output) -> $appLog"
Write-Host "COM4 (program errors) -> $errLog"
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
    $machineArgs = if ($NoPs2) {
        @("-machine", "q35,accel=tcg,i8042=off")
    } else {
        @("-machine", "q35,accel=tcg")
    }
    # +nx: expose the NX/XD bit to firmware and OS (required for NX memory protection policy)
    $cpuArgs = @("-cpu", "qemu64,+nx")

    # GUI-режим по $env:SHARPOS_GUI=1: окно (GOP framebuffer) + serial в
    # ЭТОТ терминал (PowerShell) одновременно. OVMF ConSplitter веером
    # шлёт ConOut и в GOP (окно), и в COM1 (-serial stdio → PowerShell);
    # наш 16550-драйвер пишет COM1 напрямую → тоже сюда. Tee-Object на
    # serial-лог продолжает работать. Headless-дефолт (CI/лог-воркфлоу)
    # без изменений — те же -nographic/mon:stdio.
    # -vga std в ОБЕИХ ветках: framebuffer-адаптер должен существовать
    # всегда (OVMF поднимает GOP на нём). Видимость = отдельный выбор:
    # headless (-nographic) — GOP реален, но не отображается; GUI — окно.
    # -Resolution reproduces a big panel locally instead of debugging one
    # through photographs of somebody else's screen. The mode is offered to
    # the firmware as EDID; OVMF then picks it and hands us that framebuffer.
    # Video memory has to grow with it — 3840x2160x4 is 33 MB, and the 16 MB
    # default silently leaves the mode unavailable.
    $vgaArgs = @("-vga", "std")
    if ($Resolution) {
        $parts = $Resolution -split 'x'
        if ($parts.Count -ne 2) { throw "Resolution must look like 3840x2160" }
        $xres = [int]$parts[0]
        $yres = [int]$parts[1]
        # bochs-display, NOT virtio-vga. virtio does honour the requested
        # resolution, but it has no linear framebuffer: the screen only
        # updates when the guest sends a transfer command. The firmware does
        # that for us, we do not — post-EBS the result was visible tearing and
        # a fraction of the frame rate. bochs-display scans out of memory
        # directly, which is what every drawing path here assumes.
        #
        # The mode itself is chosen by the kernel through GOP (UefiGop), so
        # xres/yres here only decide what the firmware offers.
        $vgamemMb = [math]::Max(16, [math]::Ceiling($xres * $yres * 4 / 1MB) * 2)
        $vgaArgs = @(
            "-vga", "none",
            "-device", "bochs-display,edid=on,xres=$xres,yres=$yres,vgamem=$($vgamemMb * 1MB)")
        # Write-Output, not Write-Host: the host stream does not reach the
        # transcript, so this was invisible in the log exactly when it was
        # needed.
        Write-Output "Display: offering ${xres}x${yres} (bochs-display, vgamem ${vgamemMb} MB)"
    }

    if ($env:SHARPOS_GUI -eq '1') {
        $displayArgs = $vgaArgs + @("-serial", "stdio")
    } else {
        $displayArgs = $vgaArgs + @("-nographic", "-serial", "mon:stdio", "-echr", "0x1d")
    }
    # The program port is COM3 (index 2 = 0x3E8, IRQ 4), not a second -serial:
    # that would be COM2, and the firmware mirrors its console onto COM2 —
    # the program log would open with a copy of the whole boot.
    # COM4 (index 3 = 0x2E8) is the programs' error stream, also off the
    # firmware's list.
    $displayArgs += @(
        "-chardev", "file,id=progout,path=$appLog",
        "-device", "isa-serial,chardev=progout,index=2",
        "-chardev", "file,id=progerr,path=$errLog",
        "-device", "isa-serial,chardev=progerr,index=3")

    # With the i8042 gone the guest has no keyboard at all unless one is
    # attached over USB — which is the point: firmware can drive it, we cannot.
    if ($NoPs2 -or $Usb -or $UsbOnly) {
        $displayArgs += @("-device", "qemu-xhci", "-device", "usb-kbd", "-device", "usb-mouse")

        # -UsbOnly puts the real ESP on the stick and leaves no SATA disk, so
        # the firmware boots from USB and the kernel must mount it through its
        # own stack — the shape of the test machines. Plain -Usb attaches a
        # throwaway copy instead, exercising the storage path without putting
        # the disk we boot from behind two drivers at once.
        $stickImage = if ($UsbOnly) { "esp.img" } else { "usbstick.img" }
        $displayArgs += @(
            "-drive", "if=none,id=usbstick,format=raw,file=$stickImage",
            "-device", "usb-storage,drive=usbstick")
    }

    $qemuArgs = $machineArgs + $cpuArgs + @("-m", "2048") + $displayArgs + @(
        "-net", "none",
        "-no-reboot",
        "-qmp", "tcp:127.0.0.1:$QmpPort,server,nowait",
        "-drive", "if=pflash,format=raw,readonly=on,file=firmware/OVMF_CODE.fd"
    )

    if ($localOvmfVars) {
        $qemuArgs += @("-drive", "if=pflash,format=raw,file=firmware/OVMF_VARS.fd")
    }

    # A real FAT32 image rather than VVFAT: QEMU's fat:rw: does not store a
    # filesystem, it synthesises one and maps guest writes back onto host files,
    # which makes it worthless as a target for our own FAT writer. Falls back to
    # VVFAT when mtools are missing so a clone without MSYS2 still boots.
    . (Join-Path $repoRoot "tools\New-EspImage.ps1")
    $espImage = Join-Path $qemuWorkDir "esp.img"
    if (New-EspImage -SourceDir (Join-Path $qemuWorkDir "esp") -RawPath $espImage -SizeMb $EspImageSizeMb) {
        Write-Host "ESP image: $espImage ($EspImageSizeMb MB)"

        # Under -UsbOnly the image is already attached over USB above; adding
        # it here too would present the same medium twice and let a fallback
        # to SATA hide the very thing being tested.
        if (-not $UsbOnly) {
            $qemuArgs += @("-drive", "format=raw,file=esp.img")
        }

        if ($NoPs2 -or $Usb) {
            Copy-Item -LiteralPath $espImage `
                      -Destination (Join-Path $qemuWorkDir "usbstick.img") -Force
        }
    }
    else {
        $qemuArgs += @("-drive", "format=raw,file=fat:rw:esp")
    }

    if ($TraceFaults) {
        # -no-shutdown together with -no-reboot: a triple fault then parks the
        # machine instead of vanishing, so the log is complete rather than cut
        # off wherever the reset happened.
        $qemuArgs += @(
            "-no-shutdown",
            "-d", "int,cpu_reset,guest_errors",
            "-D", "qemu-debug.log")
        Write-Host "Fault tracing on: $qemuWorkDir\qemu-debug.log"
    }

    # The exact command line, in the log. Reconstructing it from the switches
    # is guesswork, and guessing about what QEMU actually received is what
    # made a display option look applied when it was not.
    Write-Output "QEMU: $QemuExe $($qemuArgs -join ' ')"

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
