# build_benchaot.ps1 -- build the NativeAOT benchmark app as a freestanding win-x64 PE.
# Thin wrapper over build_launcher.ps1 (the generic builder). Run in vcvars64.
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)
& "$PSScriptRoot\build_launcher.ps1" `
    -AppProject "apps_native/BenchAot/BenchAot.csproj" `
    -Configuration $Configuration `
    -RuntimeIdentifier $RuntimeIdentifier
