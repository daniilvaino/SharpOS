# MeltySynth — SoundFont MIDI synthesizer

**Upstream:** https://github.com/sinshu/meltysynth
**Snapshot:** 2026-11-04, commit `4ba079c9ee1453b03be7e164d254f48426f0aac3`
(package version 2.4.1 — the one ManagedDoom's Silk build references)
**License:** MIT (see [LICENSE.txt](LICENSE.txt)) — © 2021 Nobuaki Tanaka, with the
notices of C# Synth (© 2014 Alex Veltsistas) and TinySoundFont (© 2017, 2018
Bernhard Schelling; SFZero © 2012 Steve Folta) it carries.
**Taken:** `MeltySynth/src/*.cs` (41 files). Two lines changed, marked `SharpOS cut:`
(`ArrayMath.cs`, `SoundFontSampleData.cs`): `.AsSpan()` on an array passed to
`MemoryMarshal.Cast`. C# 14's first-class spans pick the `ReadOnlySpan` overload
for an array argument, and the code writes through the result; our std needs
C# 14 (`field`), so the project cannot drop back to 13. MeltySynth predates the
rule — it would break the same way compiled with C# 14 against .NET 10.
**Not taken:** the tests, the examples, the benchmark workspace, the project file.

Compiled into DOOM.EXE (`apps_native/GPL_AHEAD_WARNING_DOOM_managed/DoomApp.csproj`)
for the music: `SharpMusic` renders MUS/MIDI through it with the TimGM6mb
SoundFont (`vendor/TimGM6mb`). Pure managed code (netstandard2.1); on our std it
needs `Vector<float>`, `ArrayPool`, `Encoding.ASCII` and LINQ, all present.
