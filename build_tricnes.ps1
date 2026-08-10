# build_tricnes.ps1 -- build TriCNES (NES emulator) as a freestanding win-x64 PE.
# Thin wrapper over build_launcher.ps1 (the generic builder), same as
# build_doom.ps1 / build_aottests.ps1. Run in vcvars64. run_build.ps1 stages the
# output to the ESP as TRICNES.EXE + .abi, so it shows up in the launcher.
#
# The emulator core comes from the TriCNES submodule (MIT, Chris Siebert) and is
# compiled verbatim; everything SharpOS-specific lives beside it in
# apps_native/TriCNES (Program.cs, GopVideo.cs, Compat\).
#
# Needs a cartridge staged as EFI\BOOT\GAME.NES -- run_build.ps1 copies the
# first .nes it finds in work\roms\ if one is there.
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)
& "$PSScriptRoot\build_launcher.ps1" `
    -AppProject "apps_native/TriCNES/TriCNESApp.csproj" `
    -Configuration $Configuration `
    -RuntimeIdentifier $RuntimeIdentifier
