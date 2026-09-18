# build_launcher.ps1 -- build a freestanding win-x64 PE app, one publish.
#
# Generic native-app builder (default: the Terminal.Gui launcher; -AppProject
# to build any other). The freestanding-link recipe (/ENTRY:SharpAppBootstrap,
# /SUBSYSTEM, /BASE, /FIXED, /NODEFAULTLIB) + base std/sdk surface live in the
# shared apps_native/sdk/FreestandingPe.props (win-x64 gated); __security_cookie
# comes from CoffStub.Generator via @(NativeLibrary). So `dotnet publish -r
# win-x64` emits the PE directly -- no cl.exe, no manual link. The link is
# lld-link on every host (SharpOsNativeLink.props); apps need no MSVC or
# Windows SDK libraries. PeLoader maps the PE at ImageBase 0x100000000.
# Per-app wrappers: build_fetch.ps1, build_aottests.ps1, build_doom.ps1, ...
#
# Tools and their versions are checked against toolchain.json before the
# build (tools/Toolchain.ps1); nothing is installed.

param(
    [string]$AppProject = "apps_native/Launcher/Launcher.csproj",
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"

# Paths are relative to the repo root, wherever the script is started from.
Push-Location $PSScriptRoot
. (Join-Path $PSScriptRoot "tools/Toolchain.ps1")
$saved = Enter-SharpOsToolchain -Components lld, dotnet
try {
    $projectFile = (Resolve-Path -LiteralPath $AppProject).Path
    $projectDir = Split-Path -Parent $projectFile
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectFile)
    $outDir = Join-Path $projectDir "bin/$Configuration/out-$RuntimeIdentifier"

    # CoffStub.Generator.dll must exist (the csproj imports its .targets).
    # Built every time: the build is incremental, and a stale DLL left from an
    # older checkout would otherwise be used silently.
    & dotnet build "bootasm/CoffStub.Generator/CoffStub.Generator.csproj" -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "CoffStub.Generator build failed ($LASTEXITCODE)" }

    & dotnet publish $projectFile -c $Configuration -r $RuntimeIdentifier --output $outDir /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

    $exe = Join-Path $outDir "$projectName.exe"
    if (-not (Test-Path -LiteralPath $exe)) { throw "PE not produced: $exe" }
    Write-Host "Built: $exe"
}
finally {
    Exit-SharpOsToolchain $saved
    Pop-Location
}
