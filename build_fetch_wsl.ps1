param(
    [string]$AppProject = "apps/FetchApp/FetchApp.csproj",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "linux-x64",
    [string]$Distro = "Ubuntu",
    [string]$ArtifactName = "FETCH.ELF",
    [string]$EspBootDir = "OS_0.1/.qemu/esp/EFI/BOOT",
    [string]$DefineConstants = "",
    [ValidateRange(1, 2)]
    [int]$AppAbiVersion = 2,
    [ValidateSet(0, 1)]
    [int]$ServiceAbi = 1,
    [switch]$NoCopy
)

$ErrorActionPreference = "Stop"

function Escape-BashSingleQuote {
    param([string]$Value)
    if ($null -eq $Value) { return "" }
    return $Value.Replace("'", "'""'""'")
}

function Convert-WindowsPathToWsl {
    param([string]$WindowsPath)

    if ([string]::IsNullOrWhiteSpace($WindowsPath)) {
        throw "Cannot convert empty Windows path to WSL path."
    }

    $fullPath = [System.IO.Path]::GetFullPath($WindowsPath)
    if ($fullPath.Length -lt 3 -or $fullPath[1] -ne ':') {
        throw "Path is not a drive-qualified Windows path: $fullPath"
    }

    $drive = [char]::ToLowerInvariant($fullPath[0])
    $tail = $fullPath.Substring(2).Replace('\', '/')
    return "/mnt/$drive$tail"
}

function Invoke-Wsl {
    param(
        [string]$Distribution,
        [string]$Command
    )

    $output = & wsl.exe -d $Distribution -- bash -lc $Command
    if ($LASTEXITCODE -ne 0) {
        $outputText = ""
        if ($null -ne $output) {
            $outputText = ($output | ForEach-Object { "$_" }) -join [Environment]::NewLine
        }

        if ([string]::IsNullOrWhiteSpace($outputText)) {
            throw "WSL command failed with exit code $LASTEXITCODE"
        }

        throw "WSL command failed with exit code $LASTEXITCODE`n$outputText"
    }

    return $output
}

function New-AppAbiManifestBytes {
    param(
        [int]$AppAbiVersionValue,
        [int]$ServiceAbiValue
    )

    [byte[]]$bytes = New-Object byte[] 16
    $bytes[0] = [byte][char]'S'
    $bytes[1] = [byte][char]'A'
    $bytes[2] = [byte][char]'B'
    $bytes[3] = [byte][char]'I'

    $bytes[4] = 1
    $bytes[5] = 0
    $bytes[6] = [byte]($AppAbiVersionValue -band 0xFF)
    $bytes[7] = [byte](($AppAbiVersionValue -shr 8) -band 0xFF)
    $bytes[8] = [byte]($ServiceAbiValue -band 0xFF)
    $bytes[9] = [byte](($ServiceAbiValue -shr 8) -band 0xFF)
    $bytes[10] = 0
    $bytes[11] = 0
    $bytes[12] = 0
    $bytes[13] = 0
    $bytes[14] = 0
    $bytes[15] = 0
    return $bytes
}

$repoRoot = Split-Path -Parent $PSCommandPath
$projectPath = Join-Path $repoRoot $AppProject
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "App project not found: $projectPath"
}

$projectDir = Split-Path -Parent $projectPath
$projectFileName = Split-Path -Leaf $projectPath
$projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectFileName)
$outputDirRelative = "./bin/$Configuration/out-$RuntimeIdentifier"

$wslProjectDir = Convert-WindowsPathToWsl -WindowsPath $projectDir

$escapedProjectDir = Escape-BashSingleQuote $wslProjectDir
$escapedProjectFile = Escape-BashSingleQuote $projectFileName
$escapedConfiguration = Escape-BashSingleQuote $Configuration
$escapedRid = Escape-BashSingleQuote $RuntimeIdentifier
$escapedProjectName = Escape-BashSingleQuote $projectName
$escapedOutputDir = Escape-BashSingleQuote $outputDirRelative
$escapedArtifactName = Escape-BashSingleQuote $ArtifactName
$publishDefineArg = ""
if (-not [string]::IsNullOrWhiteSpace($DefineConstants)) {
    $escapedDefines = Escape-BashSingleQuote $DefineConstants
    $publishDefineArg = " /p:DefineConstants='{0}'" -f $escapedDefines
}

$publishCommand = "set -euo pipefail; cd '{0}'; dotnet publish '{1}' -c '{2}' -r '{3}' /p:PublishAot=true /p:OutputType=Library /p:NativeLib=Static{4} --output '{5}' /v:minimal" -f $escapedProjectDir, $escapedProjectFile, $escapedConfiguration, $escapedRid, $publishDefineArg, $escapedOutputDir

$linkCommandTemplate = @'
set -euo pipefail; cd '{0}'; mkdir -p './obj/{4}/freestanding'; if [ ! -f '{1}/{2}.a' ]; then echo '__ERR_NO_STATIC_LIB__' >&2; exit 2; fi;
printf 'unsigned long long __security_cookie = 0x2B992DDFA232ULL;\n' > './obj/{4}/freestanding/security_cookie.c';
printf '#include <stdint.h>\nstatic unsigned char g_sharp_heap[1024 * 1024];\nstatic uint64_t g_sharp_heap_offset = 0;\nstatic void* sharp_alloc(uint64_t size){{ uint64_t aligned = (size + 15ULL) & ~15ULL; if (g_sharp_heap_offset + aligned > (uint64_t)sizeof(g_sharp_heap)) return (void*)0; void* p = (void*)(g_sharp_heap + g_sharp_heap_offset); g_sharp_heap_offset += aligned; return p; }}\nvoid* RhNewString(void* pEEType, int length){{ if (length < 0) return (void*)0; uint64_t chars = ((uint64_t)length + 1ULL) * 2ULL; uint64_t bytes = 8ULL + 4ULL + chars; unsigned char* obj = (unsigned char*)sharp_alloc(bytes); if (!obj) return (void*)0; *((void**)obj) = pEEType; *((int*)(obj + 8)) = length; unsigned short* data = (unsigned short*)(obj + 12); for (int i = 0; i <= length; i++) data[i] = 0; return (void*)obj; }}\n' > './obj/{4}/freestanding/runtime_stubs.c';
gcc -c './obj/{4}/freestanding/security_cookie.c' -o './obj/{4}/freestanding/security_cookie.o';
gcc -c './obj/{4}/freestanding/runtime_stubs.c' -o './obj/{4}/freestanding/runtime_stubs.o';
ld -o '{1}/{3}' -e SharpAppBootstrap -u SharpAppBootstrap '{1}/{2}.a' './obj/{4}/freestanding/runtime_stubs.o' './obj/{4}/freestanding/security_cookie.o';
readelf -h '{1}/{3}' | grep -q 'Type:.*EXEC' || (echo '__ERR_NOT_ET_EXEC__' >&2; exit 3);
if readelf -l '{1}/{3}' | grep -Eq 'INTERP|DYNAMIC'; then echo '__ERR_DYNAMIC_HEADERS__' >&2; exit 4; fi;
realpath '{1}/{3}'
'@
$linkCommand = $linkCommandTemplate -f $escapedProjectDir, $escapedOutputDir, $escapedProjectName, $escapedArtifactName, $escapedConfiguration

Write-Host "Building freestanding app via WSL distro '$Distro'..."
$null = Invoke-Wsl -Distribution $Distro -Command $publishCommand
$linkOutput = Invoke-Wsl -Distribution $Distro -Command $linkCommand
$outputLines = @($linkOutput)
if ($outputLines.Count -eq 0) {
    throw "WSL link step returned no artifact path."
}

$artifactWslPath = $outputLines[$outputLines.Count - 1].Trim()
if ([string]::IsNullOrWhiteSpace($artifactWslPath)) {
    throw "Could not resolve freestanding ELF path from WSL output."
}

$artifactWindowsPath = (& wsl.exe -d $Distro -- wslpath -w $artifactWslPath).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($artifactWindowsPath)) {
    throw "Could not convert ELF path from WSL to Windows path: $artifactWslPath"
}

if (-not (Test-Path -LiteralPath $artifactWindowsPath)) {
    throw "Built freestanding ELF artifact not found: $artifactWindowsPath"
}

Write-Host "Built freestanding ELF artifact: $artifactWindowsPath"

$manifestBytes = New-AppAbiManifestBytes -AppAbiVersionValue $AppAbiVersion -ServiceAbiValue $ServiceAbi
$artifactManifestPath = "$artifactWindowsPath.abi"
[System.IO.File]::WriteAllBytes($artifactManifestPath, $manifestBytes)
Write-Host "Built ABI manifest: $artifactManifestPath"

if ($NoCopy) {
    Write-Host "NoCopy set: ESP copy skipped."
    exit 0
}

$espBootDirPath = Join-Path $repoRoot $EspBootDir
New-Item -ItemType Directory -Force -Path $espBootDirPath | Out-Null
$destinationPath = Join-Path $espBootDirPath $ArtifactName
$destinationManifestPath = "$destinationPath.abi"

Copy-Item -LiteralPath $artifactWindowsPath -Destination $destinationPath -Force
Copy-Item -LiteralPath $artifactManifestPath -Destination $destinationManifestPath -Force
Write-Host "Copied freestanding ELF to ESP: $destinationPath"
Write-Host "Copied ABI manifest to ESP: $destinationManifestPath"
