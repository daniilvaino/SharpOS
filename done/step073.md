# Step 73 — PAL/OS census + quick wins (clock, env) (CLOSED)

**Status:** CLOSED. Post-step-72 (reflection-mode System.Text.Json
byte-for-byte on bare metal) we mapped *what stock .NET actually
needs from the OS* by running a defensive PAL/OS probe battery, then
knocked out the genuinely-cheap gaps. Deliverables: a living
capability registry `docs/coreclr-hosted-limits.md` (analogous to
`docs/nativeaot-nostdlib-limits.md`), and two quick-win bridges
(wall-clock — **verified**; env-vars — applied).

## What was done

### 1. PAL/OS census (scaffold `work/normal-hello/`, gitignored)

Defensive `Probe(name, body)` battery, sage-ordered: PAL census →
threading/sync → FS-minimal → crypto/RNG → globalization → process.
Classifier: `✅ / 🟡 DEGRADED / ❌ PAL-STUB (catchable SEHException) /
❌ MANAGED-EXC (clean BCL exc — layer bridged, backend absent) /
❌ OOM / ⏭️ SKIPPED / 💥 HARD-PANIC`. Probe prints its name *before*
the body so an uncatchable trap/hang leaves a dangling line = culprit.

Final tally (full run): **✅ 19 · 🟡 2 · ❌ 20 · ⏭️ 8 · 💥 1**.
Highlights: stock .NET **self-identifies correctly** (`.NET 10.0.7-dev`,
target fw, assembly id, ProcessPath); `yield`/iterators ✅ (codegen, not
threading — answered a direct question); UTF-8/Unicode, `Regex`,
`Guid`, Base64, managed PRNG, `Stopwatch`/`TickCount64`, `Path`,
`Interlocked`/`lock` all ✅; System.IO **layer is bridged** (clean
`FileNotFound`/`UnauthorizedAccess`, not traps — reading an existing
`\sharpos\*` should work); globalization-invariant mode active (named
cultures unsupported by design — no ICU).

### 2. Single uncatchable root identified (deferred frontier)

All 💥 HARD-PANIC cases — `new Socket`, `RandomNumberGenerator.Fill`/
`SHA256` (OpenSSL), OS-thread spawn (`SwitchToThread`) — share **one**
mechanism: a native-lib-load / native-C-SEH raised via
`__C_specific_handler`, whose inner filter declines, and the
`SehUnwind` walk then breaks at an `invalid Rip` (a stack value read as
a return address) before reaching any CLR handler frame →
`[SehDispatch] no handler matched → HALT`. This is the **same upstream
defect step-71/72 patched per-consumer locally** (`CallCatchFunclet`
RBP; GcInfoDecoder `GetStackSlot` RBP) and explicitly deferred. It is
not a managed gap; the real fix is the `SehUnwind` frame-chain
(C# RtlVirtualUnwind port) — a large standalone step.

### 3. Quick-win bridges

- **Wall clock — VERIFIED.** `GetSystemTimeAsFileTime` stub wrote 0 →
  every `DateTime.UtcNow` read as `1601-01-01`. New
  `OS/src/PAL/SharpOSHost/Clock.cs`: `SharpOSHost_GetUtcFileTime`
  (days_from_civil → FILETIME 100-ns ticks since 1601) /
  `SharpOSHost_GetSystemTime` (SYSTEMTIME, computed DoW), both from
  `Hal.Rtc` (CMOS). Fork `crt_imp_stubs.cpp` `GetSystemTimeAsFileTime`/
  `GetSystemTime` call the exports; `winapi_shim.cpp` weak fallbacks
  (absent export → 0 → old 1601, no fault). CMOS treated as UTC (bare
  metal has no tz DB). **Verified**: census `[clock] UtcNow.Year=2026`
  (real CMOS time). Source is shared with the AOT kernel (both use
  `Hal.Rtc` — the dual-layer-parity principle is satisfied by
  construction here).
- **Env-vars OOM — fix applied (pending re-verify).**
  `GetEnvironmentStringsW` returned `nullptr` →
  `Environment.GetEnvironmentVariables()` walked a null block,
  computed a huge length → `OutOfMemoryException`. Now returns a valid
  empty double-NUL block (constant-stub of the proven
  `GetModuleFileNameW` class) → expected ✅ empty dictionary. **Not yet
  re-run** — to confirm on the next census (documented honestly as
  such in the registry; not claimed green).

### 4. Honest scope boundary

After clock+env the "constant-cheap" well is dry. Remaining ❌ are
*real fronts, not quick*: `DateTime.Now` (no tz DB);
`cwd`/`TempPath`/`OSDescription`/`OSVersion`/`MachineName`/`UserName`/
`SystemDirectory` and `Process` introspection trap in the **Unix-PAL /
System.Native** surface (CoreCLR runs Unix-flavored — reads
`\proc\self\stat`), a whole native-shim layer; FS write/enum/delete
(host shim read-only by design + needs a VFS); `GZip`/named-cultures
(no native zlib/ICU); Socket/OpenSSL/threads (§11 SehUnwind /
threading-PAL).

## Files

Fork (`dotnet-runtime-sharpos`, `sharpos/coreclr-port`):
- `src/coreclr/pal/sharpos/crt_imp_stubs.cpp` — `GetSystemTimeAsFileTime`/
  `GetSystemTime` → kernel CMOS exports + extern decls; `GetEnvironmentStringsW`
  → valid empty block.
- `src/coreclr/pal/sharpos/winapi_shim.cpp` — weak fallbacks for the two
  new clock exports.

Kernel (`c:/work/OS`, `main`):
- `OS/src/PAL/SharpOSHost/Clock.cs` (new) — CMOS→FILETIME/SYSTEMTIME.
- `docs/coreclr-hosted-limits.md` (new) — living capability registry.
- `done/step073.md` (this writeup).

Not committed (experiment-comfort, like `Verbose`; kept in the working
tree for ongoing census runs — restore to step-72 values before any
future step commit): `Probes.cs` (boot probes off),
`CxxFrameHandler.cs` / `SehDispatch.cs` (EH-trace `const Trace=false`).
Scaffold `work/normal-hello/` is gitignored.

## Lessons

- Bridge once, serve both layers where the kernel consumes it: clock's
  source is `Hal.Rtc`, shared by AOT-kernel and hosted-CoreCLR by
  construction. Apply this dual-layer parity wherever a capability is
  consumed by both.
- Most PAL gaps are *graceful* (catchable SEH / clean BCL exceptions) —
  apps degrade defensively; only the one §11 native-C-SEH root is
  uncatchable. Triage by that distinction, not by API name.
- Honest status discipline: clock is claimed green only because the
  census showed `Year=2026`; env is documented "applied, pending
  re-verify" — not flipped to ✅ before a run proves it
  (`feedback_no_premature_root_fixed_claim`).

## Next

Choose a real frontier (none "quick"): FS-read bridge (cheapest of the
non-quick — System.IO plumbing already functional); Unix-PAL/
System.Native surface (cwd/OS-id/env/process); or the big ones —
`SehUnwind` upstream frame-chain (closes §11: Socket+OpenSSL+threads at
once) / threading-PAL (scheduler + `SwitchToThread`/waits/timers).
