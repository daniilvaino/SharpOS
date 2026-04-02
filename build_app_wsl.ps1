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

$wslProjectDir = Convert-WindowsPathToWsl -WindowsPath $projectDir

$escapedProjectDir = Escape-BashSingleQuote $wslProjectDir
$escapedProjectFile = Escape-BashSingleQuote $projectFileName
$escapedConfiguration = Escape-BashSingleQuote $Configuration
$escapedRid = Escape-BashSingleQuote $RuntimeIdentifier

$publishCommand = "set -euo pipefail; cd '{0}'; dotnet publish '{1}' -c '{2}' -r '{3}' /p:PublishAot=true --output './bin/{2}/out-{3}' /v:minimal" -f $escapedProjectDir, $escapedProjectFile, $escapedConfiguration, $escapedRid

Write-Host "Building app via WSL distro '$Distro'..."
$null = Invoke-Wsl -Distribution $Distro -Command $publishCommand

$binRoot = Join-Path $projectDir "bin"
if (-not (Test-Path -LiteralPath $binRoot)) {
    throw "Build output directory not found: $binRoot"
}

$preferredOutputDir = Join-Path $projectDir ("bin\{0}\out-{1}" -f $Configuration, $RuntimeIdentifier)

$filterArtifact = {
    param([System.IO.FileInfo]$File)

    $name = $File.Name.ToLowerInvariant()
    if (
        $name.EndsWith(".dbg") -or
        $name.EndsWith(".map") -or
        $name.EndsWith(".json") -or
        $name.EndsWith(".pdb") -or
        $name.EndsWith(".dll")
    ) {
        return $false
    }

    return $true
}

$artifactCandidate = $null
if (Test-Path -LiteralPath $preferredOutputDir) {
    $artifactCandidate = Get-ChildItem -LiteralPath $preferredOutputDir -File |
        Where-Object { & $filterArtifact $_ } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

if ($null -eq $artifactCandidate) {
    $artifactCandidate = Get-ChildItem -LiteralPath $binRoot -Recurse -File |
    Where-Object {
        if (-not (& $filterArtifact $_)) {
            return $false
        }

        $full = $_.FullName.Replace('\', '/').ToLowerInvariant()
        return $full.Contains("/publish/") -or ($full -match "/out-[^/]+/")
    } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
}

if ($null -eq $artifactCandidate) {
    throw "Could not locate built ELF artifact under $binRoot"
}

$artifactWindowsPath = $artifactCandidate.FullName

Write-Host "Built ELF artifact: $artifactWindowsPath"

if ($NoCopy) {
    Write-Host "NoCopy set: ESP copy skipped."
    exit 0
}

$espBootDirPath = Join-Path $repoRoot $EspBootDir
New-Item -ItemType Directory -Force -Path $espBootDirPath | Out-Null
$destinationPath = Join-Path $espBootDirPath $ArtifactName

Copy-Item -LiteralPath $artifactWindowsPath -Destination $destinationPath -Force
Write-Host "Copied ELF to ESP: $destinationPath"
