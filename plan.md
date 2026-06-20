# SharpOS roadmap — единый актуальный план

Дата актуализации: 2026-05-29.

Этот файл — единственный рабочий roadmap проекта. Старые PAL D1-D20
decision-файлы считаются архивом: они остаются как история reasoning'а,
но не являются отдельным планом и не должны читаться как current status.
Все живые направления сведены ниже.

Пошаговые writeup'ы остаются в `done/stepNN.md`. Текущие user-facing
ограничения hosted .NET — в `docs/coreclr-hosted-limits.md`. Открытые
симптомы без полноценного step'а — в `docs/open-symptoms.md`.

---

## 1. Текущий baseline

SharpOS сейчас имеет stable milestone:

- QEMU + VirtualBox green.
- CoreCLR fork в Release (`/Ox`) статически слинкован в kernel и
  хостится на bare metal.
- `coreclr_initialize` OK, `execute_assembly` OK, hosted census
  `OK=51 DEG=2 FAIL=7`.
- FH4 C++ EH personality работает для Release runtime.
- SehUnwind заполняет `KNONVOLATILE_CONTEXT_POINTERS`; phantom
  OBJECTREF class закрыт.
- NativeAOT/kernel EH, CoreCLR managed EH, HW-fault EH и stack traces
  green.
- Cooperative kernel threads, sleeps, events, semaphores, process spawn
  green.
- ThreadPool, `Task.Run`, `Task.Delay`, `System.Threading.Timer(50ms)`
  green in hosted probe.
- Post-EBS substrate green: `ExitBootServices OK`, console reroute
  present.
- Drivers baseline green: serial, framebuffer renderer, PS/2, line
  editor, shell engine, PCI scan.
- Own storage path exists for current boot/launcher: AHCI + FAT32 read,
  GPT/superfloppy mount tiers, launcher ELF chain green.

Latest validation report shape:

```text
Boot/Phase1-4/EH/PhaseE/Drivers/CoreCLR/EBS/Launcher: green
CoreCLR PAL/OS census: OK=51 DEG=2 FAIL=7
Probe totals: OK=52 VALUE=3
```

This is the current "known good" baseline. Any next step should preserve
it or explicitly document the regression.

---

## 2. Архитектура исполнения

SharpOS has three execution tiers:

| Tier | Meaning | Status |
|---|---|---|
| Kernel-AOT | SharpOS kernel, drivers, scheduler, PAL provider, NativeAOT/no-std | primary system tier, green |
| ELF-app AOT | external SharpOS apps (`HELLO.ELF`, `ABIINFO.ELF`, `HELLOCS.ELF`, etc.) | launcher green |
| CoreCLR-hosted | stock .NET DLLs loaded through forked CoreCLR | Release-hosted green, OS surface incomplete |

Hard boundaries:

- Kernel and hosted CoreCLR heaps are separate ownership domains.
- `SharpOSHost_*` is the C ABI boundary.
- No managed object references cross that ABI as raw stored pointers.
  Hosted references must be CoreCLR handles; kernel references stay kernel.
- PAL is a translation layer, not the place for kernel policy.

---

## 3. Consolidated D1-D20 outcome

D1-D20 are no longer a separate active checklist. Their useful outcomes
are collapsed into these current rules:

1. Use stable C ABI status codes at the `SharpOSHost_*` boundary.
2. Do not let exceptions cross the C ABI boundary. Convert expected
   errors to status codes; fail loud on unexpected violations.
3. PAL is thin and domain-split by function area (`memory`, `thread`,
   `file`, `sync`, `time`, `exception`, etc.).
4. CoreCLR is statically linked into the final SharpOS image. No `dlopen`,
   no separate `SharpOSHost.lib` provider model.
5. `SharpOSHost_*` provider binding is done by the linker. No runtime API
   table.
6. Any fallback provider inside the fork must be `weak` or declaration-only.
   Strong fallbacks are forbidden because Release codegen can fold calls
   before the final kernel export wins.
7. C++/Win32 helpers in the fork are acceptable only inside the
   fork/CoreCLR boundary. Kernel implementation remains C#.
8. Windows APIs in `pal/sharpos` are not architectural dependencies.
   The intended end state is a D11-style firewall/audit.
9. `.pdata`/Windows x64 unwind metadata is the canonical unwind model.
10. D5/D6/D8 have been reopened by reality:
    - D5 "threads abort" is obsolete for current Phase E; real hosted
      threads exist.
    - D6 ownership is split: kernel thread carrier + opaque CoreCLR
      binding.
    - D8 GC suspension is not solved; it moved to production CoreCLR work.

Historical D-files stay in `work/PAL/D1-D20 FINALIZED/` only as archive.

---

## 4. Current Status By Phase

| Area | Status | Notes |
|---|---|---|
| A — milestone freeze | partial | baseline exists; docs/gates need sync |
| B — native console/drivers | partial/green core | serial, framebuffer, PS/2, line editor, shell engine, PCI scan green |
| C — post-EBS survival | partial/green core | EBS + reroute green; panic/fault path still needs IST/hardening |
| D — SehUnwind | closed | step112 fixed context pointers; step113 removed old RBP override |
| E — cooperative threading | partial/advanced | kernel threads, waits, sleeps, process spawn, CoreCLR ThreadPool/Task/Timer green; final audit remains |
| F — hosted CoreCLR production | partial | Release host + FH4 green; GC suspend/finalizers/RetainVM policy not production-complete |
| E′ — storage/filesystem | partial | AHCI + FAT32 read green; PCI scan green; no virtio-blk, no FAT32 write |
| G — Roslyn REPL | not started | depends on resolver, IO, memory/GC production story |
| H — PowerShell | deferred | after Roslyn |
| I — hardware expansion | deferred | network, SMP, preemptive scheduler, USB, FAT32-write persistence, etc. |

---

## 5. Active Risks

### R1 — Release static binding regressions

The step114 `malloc -> xor eax,eax; ret` bug proved that strong fallback
definitions inside fork translation units can break Release before the
kernel export participates in final linking.

Required hardening:

- Audit every `SharpOSHost_*` fallback in the fork.
- Fallback implementations must be `__attribute__((weak))` or removed in
  favor of declarations.
- Concrete grep gate for the known-bad pattern:
  `grep -nE 'extern "C" .*SharpOSHost_.*\{' dotnet-runtime-sharpos/src/coreclr/pal/sharpos/*.cpp | grep -v __attribute__`
  must return only intentional strong provider symbols, never fallback
  stubs.
- Add a binary/symbol smoke for `malloc`, `calloc`, `realloc`,
  `__imp_malloc`, `_callnewh`.
- Treat this as D10/D11 production hardening.

### R2 — Silent triple fault on stack overflow

Step113 fixed the known infinite `sqrt <-> lm_sqrt` recursion, but the
system can still triple fault silently if #PF/#DF handlers fault on an
overflowed stack.

Required hardening:

- Add IST/TSS stacks for #PF/#DF/NMI or equivalent emergency fault stacks.
- Panic path must avoid managed allocation.
- Keep QEMU `-d int,cpu_reset,guest_errors -no-shutdown` as the first
  diagnostic tool for any silent exit.

### R3 — Hosted finalizers

`GC.WaitForPendingFinalizers()` hangs (`SYM-003`). Current theory:
finalizer thread/event completion path is incomplete.

Required investigation:

- Confirm whether finalizer thread starts.
- Confirm finalizer queue drain.
- Confirm finalizer-done event signaling.
- Decide whether finalizers are required before Roslyn or can remain
  parked until later F work.

### R4 — Hosted GC production mode

CoreCLR-hosted execution is green, but production GC cooperation is not
closed.

Open items:

- GC suspend/resume cooperation with scheduler.
- RetainVM/decommit policy.
- Hosted heap cleanup.
- Cross-GC reference discipline audit.

### R5 — Storage gap for Roslyn

Current read path is enough for boot/launcher/probes. Roslyn needs more
predictable `System.IO` probing and package resolution.

Open items:

- Hosted assembly resolver.
- Deterministic file probing diagnostics.
- FAT32 read completeness under real Roslyn closure.
- FAT32 write is deferred unless persistence becomes required.

---

## 6. Immediate Next Steps

Recommended order:

1. **Freeze the green Release baseline.**
   - Write a step for the current QEMU+VBox green report if not already
     captured.
   - Update limits/status docs from step112-114.

2. **D10/D11 hardening audit.**
   - Audit all fork `SharpOSHost_*` fallbacks.
   - Run the `extern "C" SharpOSHost_* { ... }` grep gate from R1 and
     convert any fallback hits to `weak` or declaration-only.
   - Verify final `OS.exe` symbol binding for CRT alloc/new-handler path.
   - Add repeatable symbol checks to the bring-up checklist.

3. **IST / emergency fault stacks.**
   - Prevent future stack overflow from turning into silent triple fault.
   - This is infrastructure, not feature work.

4. **Finish Phase E audit.**
   - Stress ThreadPool/Task/Timer in loops.
   - Reentrancy audit for allocator, handles, wait blocks, PAL locks.
   - Decide whether E12 ALC smoke is needed before Roslyn.

5. **Pick next F or G gate.**
   - F route: finalizers + GC suspend/RetainVM.
   - G route: assembly resolver + System.IO read/probing smoke.
   - Do not start PowerShell before Roslyn smoke.

---

## 7. Roslyn Path

Roslyn REPL is the next major hosted-tier product goal, but not the next
single engineering task.

Minimum prerequisites:

- Release CoreCLR baseline stays green.
- ThreadPool/Task/timers stable enough for async machinery.
- Assembly resolver can load Roslyn closure deterministically.
- System.IO read/path/enumeration handles package probing.
- Memory pressure does not explode the hosted runtime.
- Globalization stays invariant initially.

First acceptance target:

```csharp
CSharpScript.EvaluateAsync("var x = 1 + 1; x.ToString()")
```

Expected result: `"2"` or a deterministic missing-feature report naming
the first absent assembly/API.

---

## 8. PowerShell Path

PowerShell is explicitly after Roslyn.

Minimal PowerShell is a separate project:

- more System.IO including write,
- process/env/path/user/machine shims,
- pipeline/cmdlet host,
- runspaces,
- culture/globalization decisions,
- much wider BCL surface.

Full PowerShell with WMI/COM/registry-style expectations is deferred
indefinitely.

---

## 9. Deferred Hardware/OS Expansion

Deferred until a Roslyn smoke exists or a concrete blocker requires it:

- preemptive scheduling,
- SMP,
- virtio-net/TCP/IP/DNS/TLS,
- USB,
- FAT32 write persistence,
- crash dumps to disk,
- full GUI/windowing/audio,
- PowerShell-full,
- **Firecracker microVM drivers** — virtio-mmio (не PCI) transport + virtio-blk / virtio-net / virtio-vsock + i8042-less serial console. Цель: запуск SharpOS как guest под Firecracker (AWS microVM hypervisor) для serverless/функциональных сценариев — быстрый cold-start, минимальная attack surface. Virtio-mmio лежит ниже virtio-{blk,net} в стеке (transport-слой, не device-слой); общая часть с virtio-PCI — устройства, разное — discovery (DT/ACPI MMIO vs PCI config space) и notify path (MMIO write vs PCI BAR). Триггер: реальный use-case или законченный virtio-net по PCI.

---

## 10. Documentation Rules

- `plan.md` is current roadmap.
- `done/stepNN.md` is historical evidence for each implemented step.
- `docs/coreclr-hosted-limits.md` is the live feature oracle for hosted
  .NET.
- `docs/open-symptoms.md` is for unresolved symptoms only.
- `work/PAL/D1-D20 FINALIZED/` is archive. Do not add new active work
  there; copy the surviving rule into this file instead.
- If a new direction becomes active, update this file in the same step
  that changes the implementation.
