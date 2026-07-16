# build_launcher.ps1 -- build a freestanding win-x64 PE app, one publish.
#
# Generic native-app builder (default: the HelloSharpFs launcher; -AppProject to
# build any other). The freestanding-link recipe (/ENTRY:SharpAppBootstrap,
# /SUBSYSTEM, /BASE, /FIXED, /NODEFAULTLIB) + base std/sdk surface live in the
# shared apps_native/sdk/FreestandingPe.props (win-x64 gated); __security_cookie
# comes from CoffStub.Generator via @(NativeLibrary). So `dotnet publish -r
# win-x64` emits the PE directly -- no WSL, no cl.exe, no manual link. Run inside
# a VS dev environment (vcvars64). PeLoader maps the PE at ImageBase 0x400000.
# Per-app wrappers: build_fetch.ps1, build_aottests.ps1.

param(
    [string]$AppProject = "apps_native/HelloSharpFs/HelloSharpFs.csproj",
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"

$projectFile = (Resolve-Path -LiteralPath $AppProject).Path
$projectDir = Split-Path -Parent $projectFile
$projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectFile)
$outDir = Join-Path $projectDir "bin/$Configuration/out-$RuntimeIdentifier"

# CoffStub.Generator.dll must exist (the csproj imports its .targets).
$genDll = "bootasm/CoffStub.Generator/bin/Release/netstandard2.0/CoffStub.Generator.dll"
if (-not (Test-Path -LiteralPath $genDll)) {
    & dotnet build "bootasm/CoffStub.Generator/CoffStub.Generator.csproj" -c Release /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "CoffStub.Generator build failed ($LASTEXITCODE)" }
}

& dotnet publish $projectFile -c $Configuration -r $RuntimeIdentifier --output $outDir /v:minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $outDir "$projectName.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "PE not produced: $exe" }
Write-Host "Built: $exe"
