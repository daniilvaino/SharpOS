# build_launcher_gui.ps1 -- build the Terminal.Gui launcher as a freestanding
# win-x64 PE. Thin wrapper over build_launcher.ps1 (the generic builder, whose
# name is about the build recipe, not this app). Run in vcvars64.
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)
& "$PSScriptRoot\build_launcher.ps1" `
    -AppProject "apps_native/Launcher/Launcher.csproj" `
    -Configuration $Configuration `
    -RuntimeIdentifier $RuntimeIdentifier
