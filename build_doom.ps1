# build_doom.ps1 -- build ManagedDoom (DoomApp) as a freestanding win-x64 PE.
# Thin wrapper over build_launcher.ps1 (the generic builder), same as
# build_aottests.ps1 / build_fetch.ps1. Run in vcvars64. run_build.ps1 stages
# the output to the ESP as DOOM.EXE + .abi, so it shows up in the launcher.
#
# (The P1 compile-gap probe phase is over -- the core compiles on the app std
# since step141; doom_gaps.log is no longer written.)
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)
& "$PSScriptRoot\build_launcher.ps1" `
    -AppProject "apps_native/GPL_AHEAD_WARNING_DOOM_managed/DoomApp.csproj" `
    -Configuration $Configuration `
    -RuntimeIdentifier $RuntimeIdentifier
