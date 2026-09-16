# build_shell.ps1 -- build the shell as a freestanding win-x64 PE.
# Thin wrapper over build_launcher.ps1 (the generic builder). Run in vcvars64.
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)
& "$PSScriptRoot\build_launcher.ps1" `
    -AppProject "apps_native/Shell/Shell.csproj" `
    -Configuration $Configuration `
    -RuntimeIdentifier $RuntimeIdentifier
