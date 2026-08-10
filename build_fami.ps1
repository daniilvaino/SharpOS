# build_fami.ps1 -- build Fami (fast NES emulator) as a freestanding win-x64 PE.
# Thin wrapper over build_launcher.ps1 (the generic builder), same as
# build_tricnes.ps1 / build_doom.ps1. Run in vcvars64. run_build.ps1 stages the
# output to the ESP as FAMI.EXE + .abi, so it shows up in the launcher.
#
# The emulator core comes from the Fami submodule (MIT, David Khristepher
# Santos) and is compiled verbatim; everything SharpOS-specific lives beside it
# in apps_native/Fami (Program.cs, GopVideo.cs, Compat\).
#
# The second NES emulator on the system: TriCNES is the accuracy reference
# (141/141 on AccuracyCoin, ~19 ms/frame), this one is the playable emulator
# (~2 ms/frame). Both stage to the ESP; both read the same cartridge.
#
# Needs a cartridge staged as EFI\BOOT\GAME.NES -- run_build.ps1 copies the
# first .nes it finds in work\roms\ if one is there.
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)
& "$PSScriptRoot\build_launcher.ps1" `
    -AppProject "apps_native/Fami/FamiApp.csproj" `
    -Configuration $Configuration `
    -RuntimeIdentifier $RuntimeIdentifier
