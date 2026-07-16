# build_launcher_win.ps1 -- freestanding win-x64 PE launcher, one publish.
#
# The whole freestanding-link recipe (/ENTRY:SharpAppBootstrap, /SUBSYSTEM,
# /BASE, /FIXED, /NODEFAULTLIB) lives in HelloSharpFs.csproj (win-x64 gated),
# and __security_cookie comes from CoffStub.Generator via @(NativeLibrary). So
# `dotnet publish -r win-x64` emits the PE directly -- no manual link.exe, no
# cl.exe, no collect-obj step. Run inside a VS dev environment (vcvars64) so the
# SDK's link.exe resolves. PeLoader (step137) maps the PE at ImageBase 0x400000.

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
Write-Host "Built freestanding PE launcher: $exe"
