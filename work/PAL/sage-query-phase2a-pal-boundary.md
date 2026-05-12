# Sage query — Phase 2A PAL boundary tension (Windows-hosted spike)

**To**: Sage 2 (you did Round 4 analysis + finalized D1-D20 + Phase 2 Redesign).

**Status**: Phase 2A Step 4-5 attempted. Open risk #1 from INDEX.md materialized concretely. Need direction before continuing.

---

## What we did

Applied 7 patches per finalized plan + Round 7 audit corrections:

1. `clrfeatures.cmake` — added `CLR_CMAKE_TARGET_SHARPOS` to FEATURE_STATICALLY_LINKED condition
2. `vm/ceemain.cpp:934` — TARGET_SHARPOS finalizer thread skip (per D5)
3. `pal/src/exception/seh-unwind.cpp` — TARGET_SHARPOS narrow placeholder в `PAL_VirtualUnwind` body (per D13)
4. `pal/CMakeLists.txt` — conditional `add_subdirectory(sharpos vs src)`
5. New `pal/sharpos/CMakeLists.txt` + `_placeholder.cpp` (minimal skeleton)
6. `eng/native/configurecompiler.cmake` — propagate `CLR_CMAKE_TARGET_SHARPOS` → preprocessor `TARGET_SHARPOS`
7. `eng/native/configureplatform.cmake` — Variant A: skip `CLR_CMAKE_TARGET_WIN32=1` when `CLR_CMAKE_TARGET_SHARPOS` (reverted in Variant B)

Environment: Windows 11 + VS 2022 Build Tools + CMake 3.28 + Python 3.14 + .NET SDK 10.0.203. Vanilla CoreCLR Windows build worked (`build.cmd -subset clr -configuration Debug` succeeded в 11 min).

---

## A/B experiment results

### Variant A: TARGET_UNIX preprocessor + skip CLR_CMAKE_TARGET_WIN32

Reasoning: PAL pattern в CoreCLR is Unix-shape (vm/ calls Win32-shape API через pal/), so define `TARGET_UNIX` preprocessor without `TARGET_WINDOWS` to keep vm/ calling PAL. Suppress `CLR_CMAKE_TARGET_WIN32` cmake variable to avoid auto-Windows-libs.

**Result**: Failed at **CMake configure stage** в ~10 seconds:
```
CMake Error at .../System.Net.Security.Native/extra_libs.cmake:18 (message):
  Cannot find libgssapi_krb5 and System.Net.Security.Native cannot build
  without it. Try installing libkrb5-dev (or the appropriate package for
  your platform)
```

Кейс: с `CLR_CMAKE_TARGET_WIN32=false`, CoreCLR build начал искать Unix-only system libraries (Kerberos, OpenSSL etc.) which не существуют на Windows host. Cascade: `corehost/apphost/static/CMakeLists.txt:201` calls `append_extra_security_libs` which requires libgssapi_krb5.

Не дошли даже до compile stage. Configuration cascade убил bring-up.

### Variant B: TARGET_WINDOWS preprocessor + CLR_CMAKE_TARGET_WIN32=true (vanilla)

Reasoning: maybe accept Windows-shape mental model — vm/ goes Win32 directly, our pal/sharpos/ becomes inert placeholder. Less invasive.

**Result**: Build succeeded в 5:42!
- ~1698 native components compiled
- `coreclr_static.lib` produced (319 MB)
- `coreclr.dll` produced (38 MB)
- Full corehost subsystem built on top
- 0 warnings, 0 errors

**But verification revealed**: `pal/sharpos/` directory **never compiled** (no obj files в `artifacts/obj/coreclr/windows.x64.Debug/pal/sharpos/`). Investigated cause:

`src/coreclr/CMakeLists.txt:95-112`:
```cmake
if(CLR_CMAKE_HOST_UNIX)
    ...
    add_subdirectory(pal)
    ...
else()
    # Windows host path — pal/ NEVER added
endif()
```

`add_subdirectory(pal)` is gated on `CLR_CMAKE_HOST_UNIX`. On Windows host (our case), pal/ NEVER added to build. Our `pal/CMakeLists.txt` Patch was never parsed, our `pal/sharpos/` never built.

Variant B "succeeded" because **the build path was vanilla Windows CoreCLR** — vm/ calls Win32 directly, no PAL needed, our pal/sharpos/ stub silently ignored.

---

## The fundamental tension

CoreCLR source assumes binary OS targets: Unix XOR Windows. PAL pattern only exists on Unix builds.

Our spike goal was to validate **PAL boundary** — that vm/ → pal/sharpos/ → SharpOSHost_* C-ABI works. This requires vm/ to **call** pal/ functions, which only happens on Unix-shape builds.

But Unix-shape on Windows host requires either:
- Installing Unix system libraries (libkrb5, OpenSSL, etc.) on Windows — fragile, ad hoc
- Or extensive cmake patches to skip Unix system lib detection where unneeded

And Windows-shape on Windows host bypasses PAL entirely — our pal/sharpos/ becomes architecturally meaningless.

**Plan Open Risk #1 acknowledged exactly this**:
> HOST_WIN32 + TARGET_SHARPOS axis split — может потребовать дополнительные CMake patches beyond текущих 4

Reality: not just "few more cmake patches" — fundamental tension between two CoreCLR build paths. To validate PAL on Windows host requires **dozens** of TARGET_SHARPOS branches throughout vm/ source where `#if defined(TARGET_UNIX)` paths use PAL. Plus cmake patches to make `add_subdirectory(pal)` reachable on Windows host. Plus cmake patches to skip Unix system lib detection without disabling all Windows substrate.

---

## What we're choosing between

### Option 1: Accept Variant B as spike scope reduction

Variant B builds **successfully** but doesn't validate PAL boundary. Reframe spike scope:
- ✅ Validates CoreCLR can build with TARGET_SHARPOS patches (cmake mechanism works)
- ✅ Validates `coreclr_static.lib` artifact production (~319 MB)
- ❌ Does NOT validate PAL boundary (pal/sharpos/ inert)
- ❌ Does NOT validate SharpOSHost_* C-ABI

PAL validation deferred to Phase 6.1 (bare-metal) where Unix-shape natural (no Windows substrate to fight with). Spike becomes "build infrastructure validation only" — narrower than original Phase 2A goal.

**Pro**: Already working, no more invasive patches.  
**Con**: Spike doesn't measure what plan said it should — first runtime PAL trace, hot-path discovery, surface measurement.

### Option 2: Force PAL on Windows host (massive patches)

Force `add_subdirectory(pal)` on Windows host when TARGET_SHARPOS. Then add TARGET_SHARPOS branches in vm/ source files wherever `#if defined(TARGET_UNIX)` toggles PAL usage. Iteratively discover gates через compile errors.

Estimated effort: dozens of source patches throughout vm/, gc/, jit/. Each patch is narrow (`|| defined(TARGET_SHARPOS)`) but cumulative is invasive.

**Pro**: True PAL boundary validation. Achieves original spike goal.  
**Con**: Months of patching CoreCLR upstream files. Defeats "thin pal/sharpos/, no invasive vm/ patches" plan intent. Each upstream Microsoft change может break our patches.

### Option 3: Revisit WSL path

WSL2 spike (Phase 2A pre-redesign) had PAL working naturally — TARGET_UNIX + Linux substrate available. We retired WSL per Phase 2 Redesign because:
- Linux substrate (libunwind, pthread, signals) — antipattern for bare-metal
- Windows mental model closer to Phase 1 SharpOS (.pdata, Win64 calling convention)

But maybe Windows mental model **architecture** preference is wrong cost-benefit. On WSL:
- PAL works out-of-the-box
- Architecture: Unix-shape (vm/ → pal/sharpos/)
- Substrate ignored через abstraction — we replace bottom-level OS calls с SharpOSHost_*

On Windows host:
- PAL bypassed entirely
- Architecture: Windows-shape (vm/ → Win32)
- pal/sharpos/ structurally exists but architecturally meaningless

WSL gives **true PAL validation** при cost ignoring "architectural soul" концерн (which seems less critical than first thought now).

**Pro**: Spike actually works, validates real architecture.  
**Con**: Reverses Phase 2 Redesign decision. Need to revisit что было правильно в WSL и что нужно пересмотреть. Mental model split between WSL (PAL works) и SharpOS bare-metal (different substrate).

### Option 4: Hybrid pragmatic — accept spike incompleteness

Variant B build succeeded. Don't extend to PAL validation на Windows. Instead:
- Phase 2A scope reduced to "CoreCLR builds with TARGET_SHARPOS patches" — DONE
- Phase 6.1 (bare-metal) becomes the real PAL boundary validator
- Skip "first runtime trace + hot-path discovery" — defer к когда у нас есть bare-metal substrate

**Pro**: Pragmatic, leverages working Variant B build.  
**Con**: Phase 6.1 jumps from "validated PAL on WSL" to "validate PAL на bare-metal" — bigger leap. Loss of intermediate step.

---

## Concrete decision criteria для you

1. **Is Option 1/4 (accept incomplete spike) viable**? Plan's spike pass criteria require running Hello World через CoreCLR + first trace dump. Without PAL validation, we can't do this on Windows. Phase 6.1 commitment then requires more leap of faith — we never tested PAL boundary anywhere.

2. **Is Option 2 (force PAL) achievable in reasonable time**? Plan's "additional CMake patches" wording suggested marginal extra work. Reality showed it's a different scale — fundamental вмешательство in CoreCLR's build mental model. Worth months vs Option 3 revisit?

3. **Was Phase 2 Redesign decision right**? Round 4 analysis chose Windows-hosted over WSL because:
   - Less Linux substrate adoption risk
   - Closer to Phase 1 SharpOS mental model
   - Avoid libunwind/POSIX paths that we don't want on bare-metal
   
   But empirically, those concerns might be lower than expected — we could use WSL purely as TEST environment without committing to Linux substrate in production code. Code we write for WSL can be architecturally Unix-shape (pal/sharpos/ wrapping SharpOSHost_*) without runtime dependency on libunwind/pthread (these get supplied на WSL but conceptually replaced на bare-metal).

4. **What's the cleanest path to first PAL trace data**? Plan's 6-step trace-backed bring-up depends on PAL being exercised. Without it, no trace, no surface measurement, no real spec writing. Where do we get this data first?

---

## What we need

Pick one of Options 1-4 (or propose 5). Justify against:
- Time cost vs achievement
- Loyalty to original plan architecture (Phase 2 Redesign decision)
- Practical path to first PAL trace data

If Option 3 (revisit WSL) — explain how to reconcile with Phase 2 Redesign rationale.

If Option 2 (force PAL on Windows) — estimate realistic patch count + complexity.

If Option 1/4 — what does spike "pass" criteria mean when PAL не exercised? Какой is the next gate?

---

## Self-context

Project: SharpOS — experimental unikernel C# (NativeAOT + NoStdLib + UEFI). Phase 1 closed (managed EH, ACPI, timers). Phase 2 = PAL design + Windows-hosted spike per finalized plan.

Sources of truth:
- `work/PAL/pal-design.md` — entry point
- `work/PAL/D1-D20 FINALIZED/INDEX.md` — decision index, mentions risk #1 verbatim
- `work/PAL/D1-D20 FINALIZED/Phase_2_Redesign___FINALIZED.md` — WSL retirement rationale
- `work/PAL/D1-D20 FINALIZED/TARGET_SHARPOS_Build_Configuration___FINALIZED.md` — "Initial patches 1-4" + "configure proof gate"
- `work/PAL/_archive/wsl-spike-archive1.md` — earlier WSL findings (first hot-path = MultiByteToWideChar through minipal, ~165 PAL link surface, libstdc++ EH dependency, etc.)
- This file: `work/PAL/sage-query-phase2a-pal-boundary.md`
