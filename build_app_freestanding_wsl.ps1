param(
    [string]$AppProject = "apps/HelloSharpFs/HelloSharpFs.csproj",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "linux-x64",
    [string]$Distro = "Ubuntu",
    [string]$ArtifactName = "HELLOCS.ELF",
    [string]$EspBootDir = "OS_0.1/.qemu/esp/EFI/BOOT",
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
        throw "WSL command failed with exit code $LASTEXITCODE"
    }

    return $output
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

$publishCommand = "set -euo pipefail; cd '{0}'; dotnet publish '{1}' -c '{2}' -r '{3}' /p:PublishAot=true /p:OutputType=Library /p:NativeLib=Static --output '{4}' /v:minimal" -f $escapedProjectDir, $escapedProjectFile, $escapedConfiguration, $escapedRid, $escapedOutputDir

$linkCommand = "set -euo pipefail; cd '{0}'; mkdir -p './obj/{4}/freestanding'; if [ ! -f '{1}/{2}.a' ]; then echo '__ERR_NO_STATIC_LIB__' >&2; exit 2; fi; printf 'unsigned long long __security_cookie = 0x2B992DDFA232ULL;\n' > './obj/{4}/freestanding/security_cookie.c'; gcc -c './obj/{4}/freestanding/security_cookie.c' -o './obj/{4}/freestanding/security_cookie.o'; ld -o '{1}/{3}' -e SharpAppEntry '{1}/{2}.a' './obj/{4}/freestanding/security_cookie.o'; readelf -h '{1}/{3}' | grep -q 'Type:.*EXEC' || (echo '__ERR_NOT_ET_EXEC__' >&2; exit 3); if readelf -l '{1}/{3}' | grep -Eq 'INTERP|DYNAMIC'; then echo '__ERR_DYNAMIC_HEADERS__' >&2; exit 4; fi; realpath '{1}/{3}'" -f $escapedProjectDir, $escapedOutputDir, $escapedProjectName, $escapedArtifactName, $escapedConfiguration

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

if ($NoCopy) {
    Write-Host "NoCopy set: ESP copy skipped."
    exit 0
}

$espBootDirPath = Join-Path $repoRoot $EspBootDir
New-Item -ItemType Directory -Force -Path $espBootDirPath | Out-Null
$destinationPath = Join-Path $espBootDirPath $ArtifactName

Copy-Item -LiteralPath $artifactWindowsPath -Destination $destinationPath -Force
Write-Host "Copied freestanding ELF to ESP: $destinationPath"
