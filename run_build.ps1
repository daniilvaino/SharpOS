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
    [int]$QmpPort = 4444,
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
$bootx64 = Join-Path $espBootDir "BOOTX64.EFI"
Copy-Item -LiteralPath $builtEfi -Destination $bootx64 -Force

# CoreCLR expects System.Private.CoreLib.dll at path \sharpos\System.Private.CoreLib.dll
# (our Path.GetFullPath prepends "\sharpos\" to relative paths). Place the DLL there
# on the EFI partition so CreateFileW -> SharpOSHost_FileOpen -> Platform.TryReadFile
# via UEFI SimpleFileSystem can find it.
$spcDll = Join-Path "C:\work\OS\dotnet-runtime-sharpos\artifacts\bin\coreclr\windows.x64.$ForkConfig" "System.Private.CoreLib.dll"
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
$forkFxNames  = "C:\work\OS\dotnet-runtime-sharpos\artifacts\bin\coreclr-pack\Debug\net10.0\linux-x64"
$forkFxWinSrc = "C:\work\OS\dotnet-runtime-sharpos\artifacts\bin\crossgen2_publish\x64\Release"
$fxDest   = Join-Path $espSharpOSDir "fx"
$normalProj = "C:\work\OS\apps_managed\normal-hello"
$normalDllSrc = Join-Path $normalProj "bin\Release\net10.0\NormalHello.dll"
# step128 — PowerShell bootstrap shim. A managed wrapper that reflection-
# sets SystemPolicy.s_systemLockdownPolicy = None before invoking
# ManagedPSEntry.Main(). Lets PS 7.5 run in FullLanguage mode on bare
# metal (CLM detection in PS 7.5 has no env-var override). See
# apps_managed/PowerShellBootstrap/Program.cs for the override logic.
$psBootstrapProj   = "C:\work\OS\apps_managed\PowerShellBootstrap"
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
# build_fetch.ps1 / build_aottests.ps1). Stage each to ESP as <NAME>.EXE + .abi (AbiV2,
# ServiceAbi 0 = WindowsX64); the kernel dispatches on the MZ magic to PeLoader.
# Absent build output just skips (that app won't appear in the launcher).
$peApps = @(
    @{ Src = "apps_native\HelloSharpFs\bin\Release\out-win-x64\HelloSharpFs.exe"; Dest = "HELLO.EXE" },
    @{ Src = "apps_native\FetchApp\bin\Release\out-win-x64\FetchApp.exe";         Dest = "FETCH.EXE" },
    @{ Src = "apps_native\AotTests\bin\Release\out-win-x64\AotTests.exe";         Dest = "AOTTESTS.EXE" },
    @{ Src = "apps_native\GPL_AHEAD_WARNING_DOOM_managed\bin\Release\out-win-x64\DoomApp.exe"; Dest = "DOOM.EXE" }
)
foreach ($peApp in $peApps) {
    $peSrc = Join-Path $repoRoot $peApp.Src
    $peDst = Join-Path $espBootDir $peApp.Dest
    if (Test-Path -LiteralPath $peSrc) {
        Copy-Item -LiteralPath $peSrc -Destination $peDst -Force
        [System.IO.File]::WriteAllBytes("$peDst.abi", (New-AppAbiManifest -AppAbiVersion 2 -ServiceAbi 0))
        Write-Host "Prepared app PE: $peDst"
    }
    elseif (Test-Path -LiteralPath "$peDst.abi") {
        Remove-Item -LiteralPath "$peDst.abi" -Force
    }
}

Write-Host "Prepared EFI image: $bootx64"
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

    # GUI-режим по $env:SHARPOS_GUI=1: окно (GOP framebuffer) + serial в
    # ЭТОТ терминал (PowerShell) одновременно. OVMF ConSplitter веером
    # шлёт ConOut и в GOP (окно), и в COM1 (-serial stdio → PowerShell);
    # наш 16550-драйвер пишет COM1 напрямую → тоже сюда. Tee-Object на
    # serial-лог продолжает работать. Headless-дефолт (CI/лог-воркфлоу)
    # без изменений — те же -nographic/mon:stdio.
    # -vga std в ОБЕИХ ветках: framebuffer-адаптер должен существовать
    # всегда (OVMF поднимает GOP на нём). Видимость = отдельный выбор:
    # headless (-nographic) — GOP реален, но не отображается; GUI — окно.
    if ($env:SHARPOS_GUI -eq '1') {
        $displayArgs = @("-vga", "std", "-serial", "stdio")
    } else {
        $displayArgs = @("-vga", "std", "-nographic", "-serial", "mon:stdio", "-echr", "0x1d")
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
