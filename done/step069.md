# Step 69 — Managed exceptions on bare metal: try/catch/finally/throw works via stock CoreCLR (OPEN)

**Status:** OPEN. The managed-EH pillar is **conquered and proven by green
tests**, but the coverage battery is not yet 100% (System.Text.Json exposes a
**separate, new GC-region-allocator frontier** — not an EH bug). Fixes are
**not committed** yet (diagnostic probes still in tree — 5b strips them before
the milestone commit).

## Веха (доказано зелёными тестами)

Coverage-батарея на SharpOS bare metal, hosted by stock CoreCLR fork
(NormalHello.dll = ordinary `dotnet build`, not `-nostdlib`):

```
[t] OK  arith/Math
[t] OK  string/StringBuilder
[t] OK  array/Span/stackalloc
[t] OK  List/LINQ
[t] OK  Dictionary
[t] OK  generics/tuple/pattern
[t] OK  try/catch/finally/throw     ← THE pillar (SUPER-5 class)
[t] OK  nullable/coalesce
[t] OK  DateTime/TimeSpan
[t] OK  Guid
[t] START System.Text.Json roundtrip → crash (GC frontier, see below)
```

`throw` / `try` / `catch` / `finally` execute on bare metal through the stock
CoreCLR funclet two-pass EH — the long-standing
[[project-jit-frame-seh-unwind-pillar]] is broken through.

## Контекст

Continuation after step 68 (byte-for-byte normal program, exit 42). Step 68
proved a stock DLL *runs*; step 69 makes stock **exception handling** work end
to end. Two repos: kernel `c:/work/OS` (`main`), fork `dotnet-runtime-sharpos`
(`sharpos/coreclr-port`). Method: coverage battery in
`work/normal-hello/Program.cs` (gitignored scaffold) + probe-driven empirical
bisection; addresses resolved via
`"C:\Program Files\LLVM\bin\llvm-symbolizer.exe" --obj=OS/.qemu/esp/EFI/BOOT/BOOTX64.EFI --relative-address`
(RVA = runtime_addr − imageBase from the log's `imageBase=0x...`, which shifts
per build) and `llvm-objdump.exe -d` (file VMA = RVA + 0x180000000).

## Что сделано — 8 фиксов (EH-пилон)

The throw path is: `IL_Throw` → `DispatchManagedException` → managed
`EH.RhThrowEx` (System.Private.CoreLib) → QCall `SfiInit`/`SfiNext`
(StackFrameIterator) → personality → `RtlUnwind` → `CallCatchFunclet`.
Each wall below was localized by probe, root-caused from sources, fixed
targeted (not sledgehammer):

1. **`RtlVirtualUnwind_Unsafe` static thunk** — `vm/rtlfunctions.cpp`.
   `EnsureRtlFunctions()` is gated `#ifndef TARGET_UNIX` (ceemain.cpp:761) and
   SharpOS *is* TARGET_UNIX-shaped → never called → pointer stays NULL →
   `call 0`. File-scope `SharpOS_RtlVirtualUnwind_Thunk` + static init
   `RtlVirtualUnwind_Unsafe = &thunk` (link-time const; thunk forwards through
   `__imp_RtlVirtualUnwind` → in-image C# SehUnwind). No EEStartup/ntdll dep.
2. **CFG off for SharpOS** — `eng/native/configurecompiler.cmake`.
   `/guard:cf` routes every indirect call through `__guard_dispatch_icall_fptr`
   (.rdata slot the NT loader patches). No loader on bare metal → slot 0 →
   first CFG-guarded indirect call = `call 0`. Disasm-confirmed. Gate
   `CLR_CONTROL_FLOW_GUARD` OFF only when `CLR_CMAKE_TARGET_SHARPOS`.
3. **`VirtualUnwindCallFrame` native branch** — `vm/stackwalk.cpp`. SharpOS=
   TARGET_UNIX → the `!TARGET_UNIX` `RtlLookupFunctionEntry` path is compiled
   out; the TARGET_UNIX fallback uses a local Invalid `EECodeInfo` for native
   coreclr PCs (coreclr is linked into the kernel image, not a registered
   module) → m_pJM NULL → `call 0`. Under `TARGET_SHARPOS && !DACCESS_COMPILE`:
   `if (pCodeInfo==NULL || !pCodeInfo->IsValid())` → call winnt.h's
   `RtlLookupFunctionEntry` (do NOT redeclare — conflicts; symbol resolves to
   C# SehUnwind via `__imp_` from CRT_STUB) which reads the kernel-image static
   `.pdata` (covers linked-in coreclr native code).
4. **`CheckRegDisplaySP` non-fatal** — `vm/threads.cpp`. `DEBUG_REGDISPLAY`
   `_ASSERTE(SP within GetCachedStackLimit/Base)` — CoreCLR's cached thread
   stack bounds don't describe the kernel stack; SP is correct for the real
   unwind. Early `return` under `TARGET_SHARPOS`.
5. **`RtlUnwind` [RuntimeExport]** — `OS/src/PAL/SharpOSHost/SehDispatch.cs`.
   CoreCLR `ClrUnwindEx` calls Windows `RtlUnwind` for 2nd pass; fork had only
   `CRT_STUB(RtlUnwind)` (trap, no C# impl unlike RtlVirtualUnwind). Added the
   C# 2nd-pass unwinder (mirror of SehDispatch's loop, stop at targetFrame,
   set Rip/Rsp/Rax, RestoreContextAsm). Fork unchanged (CRT_STUB = __imp_
   alias; C# wins at OS.obj-first link).
6. **`DbgAssertDialog` non-fatal+logged** — `utilcode/debug.cpp`. The fork is a
   DEBUG build; CoreCLR `_ASSERTE` fires on host-environment invariants that
   don't hold when hosted on the kernel (stack bounds, TEB shape, GetControlPC
   vs GetReturnAddress, isFrameless, ...). On Windows an assert is *ignorable*;
   here it ran `FailFastOnAssert → RaiseFailFastException → HALT`, turning every
   benign mismatch fatal and blocking the EH path. Early `return` (logs
   `[ASSERT] file:line: expr (ignored)` — far better than the old `[ODS-A] ?`).
   Gate `TARGET_SHARPOS && !SELF_NO_HOST` (SELF_NO_HOST excludes the
   utilcodestaticnohost/dac flavors → mscordbi/crossgen link); plain extern
   decls (a 2nd weak def → duplicate-symbol at standalone coreclr.dll link).
   Generalizes #4: targets the assert *fatality mechanism*, keeps diagnostics.
7. **Step 3 — R2R static `.pdata`** (3 files). `peimagelayout.cpp:512` R2R
   `.pdata` install is `#if !defined(TARGET_UNIX)` → compiled out for SharpOS →
   R2R CoreLib code (mapped at VM-window 0x500008090000) has no unwind info →
   `[seh] invalid Rip 0x5000… — stop walk`. Gate
   `(!TARGET_UNIX || TARGET_SHARPOS)`; fork `RtlAddFunctionTable` impl +
   weak `SharpOSHost_RegisterStaticFunctionTable`; `SehUnwind.cs` static-table
   registry + binary-search `StaticTableLookup` + `InStaticRange` folded into
   `InDynamicRange` (IsValidIp). Confirmed: `[RtlAddFunctionTable]
   base=0x500008090000 count=0xC9F1`, R2R frames now unwind.
8. **ole32 + BCrypt prokinuty to kernel** — `pal/sharpos/crt_imp_stubs.cpp` +
   `OS/src/PAL/SharpOSHost/CrtHeapStubs.cs`. `Guid.NewGuid()` P/Invokes
   `ole32!CoCreateGuid`; `System.HashCode`/JSON P/Invoke `BCrypt!BCryptGenRandom`.
   No DLL files on bare metal → DllNotFound → native C++ EEMessageException →
   could not unwind JIT → HALT. sentinel-HMODULE pattern (like kernel32):
   `LoadLibraryExW` returns sentinel, `GetProcAddress` maps to in-image symbol.
   **Logic stays in C#** per invariant 1: fork = thin forwarder + weak
   fallback; real impl C#-side (CoCreateGuid→`SharpOSHost_CreateGuid`;
   BCryptGenRandom→ new weak `SharpOSHost_FillRandom` reusing Guid.NewGuid
   entropy). User explicitly rejected an inline-PRNG in the fork as an
   invariant-1 violation.

## Lessons / диагностические находки

- **SharpOS is TARGET_UNIX-shaped** (PAL-shaped port; `-DTARGET_SHARPOS
  -DTARGET_UNIX`). Recurring root class: every `#ifndef TARGET_UNIX` Windows
  `.pdata`/range/RtlLookup path is compiled out, but SharpOS uses Windows-style
  table-driven SEH and *needs* them → re-enable under TARGET_SHARPOS (fixes
  #1,#3,#7 are this class).
- In a DEBUG fork hosted on bare metal, `[ODS-A] ?` + `RaiseFailFastException
  flags=0x0` = a fired `_ASSERTE`, NOT a real "unhandled". Fix #6 makes the
  whole class non-fatal; don't whack-a-mole each assert.
- **R2R-`JitCodeToMethodInfo` hypothesis DISPROVEN.** `[R2R JCMI*]` probes →
  0 hits ⇒ `pRS->_pjit` ≠ ReadyToRunJitManager for the failing PCs. The 33212
  `[EECodeInfo::Init INVALID]` are on page-aligned `0x…0000` addrs = benign
  non-code probing (GC/stackwalk/IsManagedCode); the system runs fine with them
  (10 tests green AFTER the non-fatal-assert). `codeman.h:B5E m_pJM!=NULL` =
  same benign noise, not a root.
- Probe discipline: rate-limit (a 33k-line flood crashed the VM with no panic —
  serial spam, not a bug). One-shot/`s_<n><N` counters.

## Остаточный фронт — System.Text.Json = GC, НЕ EH

JSON crash stack (symbolized): `[RSP+0]=UMThunkUnwindFrameChainHandler`,
`[RSP+0x70]=WKS::region_allocator::align_region_up` (Workstation-GC region
allocator), RDI/R12/R13/R15 = `0x9FC…` (GcHeap region), RAX/RCX/R8/R9 = 0,
RIP=0 → call/ret to 0 in the **GC region allocator** under heavy JSON
allocation (reflection / ConcurrentDictionary / ResourceManager). Distinct new
domain, separate from the EH pillar (which is understood + fixed + proven —
this is not papering over the EH root).

**Next:** instrument the `region_allocator_callback_fn fn` invocation in
`gc.cpp region_allocator::allocate` (4078) and the `allocate_region` →
`virtual_alloc` / GCToOSInterface path — find the pointer/return going to 0
under JSON allocation. Separate focused GC session.

## Файлы

Fork (`dotnet-runtime-sharpos`, `sharpos/coreclr-port`):
`vm/rtlfunctions.cpp`, `eng/native/configurecompiler.cmake`,
`vm/stackwalk.cpp`, `vm/threads.cpp`, `utilcode/debug.cpp`,
`vm/peimagelayout.cpp`, `pal/sharpos/crt_imp_stubs.cpp` (RtlAddFunctionTable,
ole32/BCrypt sentinels, weak SharpOSHost_FillRandom/RegisterStaticFunctionTable),
+ probes (jitinterface.cpp, exceptionhandling.cpp, ceeload.cpp, codeman.cpp).

Kernel (`c:/work/OS`, `main`):
`OS/src/PAL/SharpOSHost/SehDispatch.cs` (RtlUnwind), `SehUnwind.cs`
(static-table registry), `CrtHeapStubs.cs` (SharpOSHost_FillRandom).

## Что откладываем / перед коммитом (5b)

- Strip ALL diagnostic probes: `[DME]`, `[SfiInit]` A..E/loop/result,
  `[GetFunctionEntry INVALID]`, `[EECodeInfo::Init INVALID]`, `[R2R JCMI*]`,
  `[CallCatchFunclet]`, `[RunEagerFixups]` (jitinterface/exceptionhandling/
  ceeload/codeman). Keep the 8 fixes.
- Then commit the milestone (kernel `main` + fork `sharpos/coreclr-port`),
  `git diff --cached --stat` verified (no build artifacts).
- GC-region-allocator frontier (JSON) — next session.
- Eventually S6 → pwsh.
