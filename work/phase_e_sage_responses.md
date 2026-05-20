# Phase E Sage Responses — verbatim archive

**Brief asked of:** `work/phase_e_sage_brief.md` (frozen 2026-05-20).
Two independent sages reviewed; this file preserves both responses
verbatim for future reference. Synthesis lives at the end of
`phase_e_sage_brief.md` (§6) and the final decisions in
`docs/threading-architecture.md`.

---

## Sage 1 response

### 1. Architectural sanity check (G1-G14)

G1-G14 list strong. Несколько что я бы добавил:

**G15 — Stack pivot during interrupt handlers.** Когда HPET IRQ fires,
hardware pushes interrupt frame на current RSP. Если current thread's
stack почти полный — IRQ может вылезти за guard page inside interrupt
handler. Это даёт double fault (no stack to push DF frame). Решение —
IST stack (Interrupt Stack Table) для HPET vector, similarly как уже
есть для #DF (step035 mention). Severity: high. Не упомянуто в G-list.

**G16 — Yield point density.** Cooperative scheduler с yield points
только на explicit yield/Sleep/I/O block может starvовать threads если
CPU-bound loop не имеет ни одного. Сложнее G2: даже знание что есть
problem не helps если no insertable yield points. JIT could insert
auto-yields на loop backedges (cooperative-equivalent of safepoints).
Это D8-flavored work что shall return when Phase F starts. Worth
flagging upfront.

**G17 — TLS array reallocation race.** CoreCLR's TEB.ThreadLocalStorage-
Pointer это array что grows when new TLS-using modules load. Если
managed thread читает array index N во время того как другой managed
thread reallocates array (load new module) — torn pointer. Stock
Windows handles this через atomic pointer swap + grace period. На bare
metal — нужно решить. Severity: medium. Не упомянуто.

**G18 — Process lifecycle на panic.** G7 mentions handle leak risk при
graceful exit. Сложнее: что если process panics inside managed catch
block? Kernel должен detect process state inconsistency. Severity:
medium. Connected к G7 но distinct.

**G19 — CoreCLR's own thread-local-fields в JIT'ted code.** JIT может
emit direct `mov rax, gs:[offset]` для `[ThreadStatic]` field access.
Это inline в managed method, не goes through PAL. Если TLS layout
changes (added field after compile) — old JIT'ted code crashes. Stock
CoreCLR solves через `t_inlinedThreadLocalStaticBase` versioning. На
SharpOS — нужно verify ваш fork inherits это. Severity: medium-high.
Subtle, не упомянуто.

**G20 — Per-thread Crst pool exhaustion.** CoreCLR pre-allocates Crst
slots в global pool. Multiple thread acquire blocks waiting на same
pool. With small thread count (4 workers + REPL + GC + finalizer = ~7
threads) — likely fine. Worth measuring under Roslyn load. Severity:
low.

**Most critical из existing G-list:**

- **G4 (GC suspend live-lock)** — biggest risk. Если cooperative thread
  в long-running C# loop без managed safepoint poll — GC blocks forever.
  Это THE SP1. Plan'ишь deferring к Phase F правильно — не пытайся
  решить в Phase E.
- **G6 (TLS update on context switch)** — must verify Phase 5.5
  implementation actually swaps gs base on switch. WRMSR
  IA32_KERNEL_GS_BASE is kernel mode operation — context switch path
  must emit это. Если step 5.5 только set gs base once at boot —
  multi-thread broken на gs:[N] reads. Critical to verify before E
  starts.
- **G1 + G14 (kernel Thread ↔ CoreCLR Thread)** — these I'd elevate.
  Это G6 prerequisite — без proper binding, gs base swap не знает what
  to swap to.

### 2. Q1-Q7 voting + corrections

**Q1 — Page tables: predisposition A (activate clone) → STRONG AGREE с
уточнением.** Twist: при `mov cr3, s_rootTable` — все mappings что были
done на UEFI inherit становятся ВИДНЫ только если они уже зеркалированы
в clone. `TryCloneTableRecursive` copied bytes at boot time. Post-boot
mappings что UEFI runtime services создали (variable storage, MMIO acpi)
— не в clone. Возможный gotcha: после CR3 switch, UEFI runtime services
calls могут падать потому что их internal mappings не visible. Если
SharpOS уже past EBS (step 91 in plan.md says yes) — runtime services
not called → not a problem. Но verify это до switch. Activation в Phase
E1 как первый sanity — correct ordering. Это prerequisite для everything
else (per-thread stacks need Map() actually working).

**Q2 — Context switch: predisposition A (byte-shellcode) → STRONG AGREE.**
Это твой established pattern (JumpStub, IDT trampolines). Естественное
продолжение. Plus: shellcode emit code can be shared between init and
per-thread template (each thread context save area same layout).

Sage's specific question — FXSAVE vs xsave:
- Minimum safe floor: FXSAVE (legacy SSE2). Captures: GP regs, 8 MMX,
  16 XMM. ~512 bytes.
- For SSE4/AVX: XSAVE with state mask covering AVX register zone.
  ~832 bytes.
- For AVX-512: XSAVE with state mask covering ZMM zone. ~2-3 KB.

CoreCLR's RyuJIT does emit AVX2 instructions for Roslyn / Linq
workloads. Без AVX save — context switch corrupts upper YMM halves →
managed crashes. **Recommendation: use XSAVE from start with full
xstate_bv mask.** Slightly bigger per-thread save area но correctness
guaranteed. Detect AVX-512 support and skip if not present. Reference:
stock CoreCLR на Linux uses XSAVE для thread context.

**Q3 — TLS: predisposition C (hybrid TEB-facade) → AGREE strongly.**

Sage's specific question — what CoreCLR actually reads from gs:

```
gs:[0x10] — TEB.StackLimit            (used by __chkstk, Thread::IsAddressInStack)
gs:[0x20] — TEB.Self (NT_TIB.Self)    (legacy)
gs:[0x30] — TEB.NtTib.ExceptionList   (SEH 32-bit, x64 normally ignored)
gs:[0x58] — TEB.ThreadLocalStoragePointer  (every __declspec(thread) C++ access)
gs:[0x88] — TEB.ThreadId (DWORD)      (GetCurrentThreadId)
gs:[0x100..0x500] — TLS Slot 0..63    (TlsAlloc-style)
gs:[0x1480..] — TLS Slot 64+          (extended)
```

Plus CoreCLR uses `__declspec(thread)` for `t_pCurrentThread` etc. These
go through `gs:[0x58]` → array → indexed by `_tls_index` (module-specific).

**Critical: managed code что использует [ThreadStatic] тоже going через
TLS path. JIT emits `mov rax, gs:[t_inlinedThreadLocalStaticBase_offset]`.**

Recommendation для hybrid:
```
TEB layout (minimum 0x200 bytes):
  +0x00: NtTib.ExceptionList = NULL
  +0x08: NtTib.StackBase
  +0x10: NtTib.StackLimit
  +0x18: NtTib.SubSystemTib = NULL
  +0x20: NtTib.Self = pointer to this TEB
  +0x30: NtTib.ExceptionList alias
  +0x58: ThreadLocalStoragePointer = pointer to array of TLS-module bases
  +0x88: ThreadId
  +0x100..0x500: TlsSlots (64 slots)
  +0x600: SharpOS Thread* (our bookkeeping, fixed offset)
```
IA32_KERNEL_GS_BASE MSR points к этой TEB. Context switch:
1. WRMSR IA32_KERNEL_GS_BASE = new thread's TEB pointer
2. SWAPGS если switching to/from kernel context — но мы single AS, не нужно

**Q4 — Wait queue: predisposition B + hybrid timer (C) → AGREE.**
"Cooperative + IRQ-assisted timer" не нарушает cooperative invariant.
IRQ handler что только flips a flag is not a thread switch — pattern
same как Windows's APC delivery. One caveat: IRQ handler must not be
too complex (no allocations, no logging through paths что lock).

**Q5 — GC suspend: predisposition C (Zero-GC stays, defer to Phase F) →
AGREE.** ZeroGC = no collection = no suspend needed. Threading works
независимо. Risk: ThreadPool + Task allocations → heap grows
monotonically → eventually OOM. Mitigation: bound heap size; detect OOM
through Pager → graceful shutdown; time-limit Phase E demos.

**Q6 — ThreadPool size: predisposition A (N=4 fixed) → AGREE с
уточнением.** ThreadPool=1 → definite deadlock risk
(EvaluateAsync awaits internal Tasks → Tasks queued to ThreadPool →
Worker blocks awaiting another Task in same queue). **Refinement: N=4
fixed reasonable BUT add growable bound (max=8) for safety.** PowerShell
pipelines may need more.

**Q7 — Reentrancy audit list R1-R8 → mostly complete.** Дополнения:
- R9 — `s_currentDomain` static в AppDomain.cs (if exists)
- R10 — `g_pConfig` reads во время config var lookups
- R11 — Profiler hooks (`g_profControlBlock`)
- R12 — `Thread::m_pFrame` chain (Phase D landed; per-thread anchor — verify)
- R13 — DAC globals

### 3. Hidden CoreCLR dependencies

Quick scan для multi-thread assumptions:
- `vm/threadsuspend.cpp` — entire file dedicated к suspend/resume.
  Won't fire пока ZeroGC stays.
- `vm/methodtablebuilder.cpp` — type loading. Uses CrstHolder. Worth
  audit for assumed-uncontended fast-paths.
- `vm/classcompat.cpp` — generic instantiation. Has TLS-cached lookup
  `t_GenericArgs`.
- `vm/eepolicy.cpp` — process exit handling.
- `gc/gc.cpp` — many `assert(GCHeapUtilities::IsGCInProgress() ||
  g_TrapReturningThreads)`. ZeroGC sidesteps.
- `jit/jit.h` — `JitTls` — JIT compiler uses TLS for compilation
  context. Multi-thread JIT requires real TLS. **G6 critical here — JIT
  crashes obscurely если TLS broken.**
- `pal/sharpos/crt_imp_stubs.cpp` — `_tls_index = 0` placeholder. If
  multi-thread C++ code uses `__declspec(thread)` — wrong slot, garbage
  data.

**Most likely first crash on real CreateThread:**
1. GC thread starts → calls into uninit GC state → crash в gc/gcheaputilities.cpp
2. Finalizer thread starts → similar
3. ThreadPool worker starts → TLS for `t_runtime_thread_locals` not set → null deref

Order of crashes will guide implementation. **Expect 2-3 weeks debugging
through these.**

### 4. Roslyn / PowerShell concurrency requirements

**Roslyn CSharpScript.EvaluateAsync specifically:**
- Main analyze pass — caller's thread
- Generation/emit phase — `Task.Run` via ThreadPool worker
- Symbol resolution — mostly synchronous, может spawn Task для file I/O
- JIT compile — happens на executing thread (background thread)

Minimum viable ThreadPool: 2 workers (1 for emit, 1 for execute). Safe
choice 4. Async/await chain depth: ~6-8 levels deep for trivial
expression eval.

**Recommendation для §1a REPL:**
- ThreadPool: 4 workers
- Reject `.Wait()` style calls в demo code
- Use `await` properly
- Real production будет need ThreadPool growth bounded by memory
  pressure

**PowerShell Invoke() pipeline:** Honest estimate для PS minimal:
ThreadPool=8 workers нужно. ThreadPool=4 will likely work for
`1..10 | ForEach-Object` but fail on more complex pipelines.

### 5. D6 — Thread state ownership

This is G1+G14 question. **Proposed clean architecture:**

```
KERNEL TIER
  kernel.Thread struct (per-thread)
    - regs save area (GP + XSAVE)
    - stack pointer + bounds
    - ready/wait state
    - tier-specific extension pointer
      ↓
    ext = NULL              (kernel thread)
    ext = ProcessTLS*       (AOT app)
    ext = CoreCLRThread*    (managed)

KERNEL SCHEDULER
  Just iterates kernel.Thread queue
  No knowledge of tier-specific state
  Calls GC-suspend-callback BEFORE switching if extension == CoreCLRThread

CORECLR Thread* (vm/threads.h)
  Linked to kernel.Thread via ext ptr
  Owns: GC roots, JIT context, Crst, per-thread locks, exception state
  vm/ continues to own its Thread*
```

**Key insight:** vm/ owns CoreCLR Thread, kernel owns kernel.Thread*.
They link via pointer in extension slot. Не trying merge into one
struct — each tier has its own concerns.

During context switch:
1. Kernel sees current thread has `ext.kind == CoreCLR`
2. Calls back into managed/PAL to allow CoreCLR Thread to prepare for
   suspension (release locks, write barriers flush)
3. CoreCLR Thread returns "ready" or "not ready" (cooperative)
4. If ready — proceed switch
5. If not ready — schedule another thread, retry later

**This is cooperative GC integration done right — but defer actual
implementation к Phase F.** Phase E just sets up the structure (ext
pointer slot), GC suspend logic comes later.

Plan.md says vm/ owns — agree, with caveat that kernel also owns its
own struct. Two-layer approach, не one merged struct.

### Phase E sequencing recommendation

13 sub-milestones. **Honest estimate если все going well: 8-12 weeks
for E1-E10. E11-E13 likely add 4-6 weeks через debugging.**

E1: Page table activation (Q1 resolution)
E2: Thread struct + context switch (Q2 + Q3 + G6)
E3: Cooperative scheduler core
E4: Wait primitives (Q4)
E5: Process abstraction (G7)
E6: ELF thread API (SDK extension)
E7: Reentrancy audit pass 1 (R1-R8 partial)
E8: CoreCLR PAL threading routing (D5 reopen)
E9: First multi-thread managed code
E10: ThreadPool basic (Q6)
E11: Task / async await (G2 risk зона)
E12: AssemblyLoadContext smoke test
E13: Reentrancy audit pass 2

### Bottom line

Brief well-written. G-list strong, Q-list well-framed, scope clear.

**Main concerns:**
1. **G6 verification first** — confirm Phase 5.5 actually does WRMSR
   on context switch. **Verify before E1 starts.**
2. Hidden multi-thread bugs в vm/ — expect 2-3 weeks debugging surface
3. Phase E scope is large — 13 milestones, 2-4 months realistic
4. SP5 (memory pressure) — Phase E risk, plan for OOM handling

**Actionable suggestion:** before starting E1, do 1-day investigation of
WRMSR/SWAPGS plumbing currently in Phase 5.5. Confirm context switch
path knows to swap gs base. Если не — this is gating prerequisite for
everything else.

---

## Sage 2 response

### Главный verdict

Phase E можно делать как cooperative threading + logical processes +
CoreCLR PAL routing, но я бы внёс **4 обязательные поправки до кода**:

1. **D6** решать не как "vm/ owns" или "kernel owns", а как **split
   ownership**. Kernel owns physical scheduling: stack, regs, wait-
   state, ready queue, TEB pointer, process-ref. CoreCLR owns managed
   state: Thread*, GC mode, frames, locks, ThreadStore. Между ними
   нужен `ManagedThreadBinding`, а не смешивание полей в одном объекте.

2. **Q3 содержит конкретную ошибку:** TEB.Self на x64 — это
   **`gs:[0x30]`**, не `gs:[0x20]`. `gs:[0x20]` — это FiberData/Version
   область NT_TIB. В вашем CoreClrProbe уже правильно выставлено
   teb + 0x30, значит brief надо поправить.

3. В Q3 также **неверно опасно сформулирован MSR:** для обычных
   `gs:[...]` нужен **`IA32_GS_BASE = 0xC0000101`**, а не
   `IA32_KERNEL_GS_BASE`, если вы не используете SWAPGS.
   `IA32_KERNEL_GS_BASE` участвует именно в SWAPGS, который меняет
   текущий GS base с MSR `C0000102H`.

4. **Q1 "activate clone" правильно, но только если сделать это рано.**
   Сейчас `VirtualMemory/MapKernel` уже пишут в активный firmware
   PML4, а не в clone. **Если активировать clone после этих маппингов,
   можно потерять live mappings CoreCLR VM window/JIT/GC area.** Brief
   сам фиксирует, что Map() пишет в inactive clone и CPU этого не
   видит, поэтому активацию надо делать **сразу после clone и до
   `VirtualMemory.SelfTest()` / CoreCLR / framebuffer mappings**, либо
   вводить dual-write/backfill.

### Голосование по Q1-Q7

| Q | Vote | Комментарий |
|---|---|---|
| Q1 Page tables | A, but **early-only** | Активировать clone в Phase 3 сразу после InitializePager(), до всех MapKernel. Иначе split-brain. После активации MapKernel должен стать alias к active-root mapping. |
| Q2 Context switch | A byte-shellcode | Save-area: GPR + RSP/RIP/RFLAGS + fxsave64 area + gsBase/TEB pointer |
| Q3 TLS | C, but **fix offsets/MSR** | Hybrid TEB facade правильный. Минимум: StackBase 0x08, StackLimit 0x10, **Self 0x30** (not 0x20), ThreadLocalStoragePointer 0x58, PEB 0x60, LastErrorValue 0x68. TLS pointer на x64 сидит по 0x58, Self читается через `gs:[0x30]`. |
| Q4 Wait queue | B + C | wait-list + timer queue, HPET IRQ только ставит флаг/тик. Это не ломает cooperative invariant, пока IRQ не делает context switch и не вызывает managed/runtime code. |
| Q5 GC suspend | C в Phase E | Zero-GC + threading возможен, если реально нет collection/suspend path. **Но нужно явно fail-fast на любой попытке GC suspend, иначе получите тихую порчу.** |
| Q6 ThreadPool size | Fixed 4 для Phase E | Default min привязан к processor count, у вас single-core даст 1, что плохо для nested waits/async continuations. |
| Q7 audit | R1-R8 верно, но **неполно** | Добавить PhysicalMemory, VirtualMemory, page-table spare list, process list/PID allocator, handle tables, console/logging, root lists, PAL critical sections, wait handles, monitor/syncblock, current thread/process globals. |

### Q2: FXSAVE или XSAVE?

Для Phase E safe-floor:
- Если вы **не включаете AVX в XCR0**, то **FXSAVE/FXRSTOR достаточно**
  для x87/MMX/SSE/XMM/MXCSR. FXSAVE сохраняет state в 512-byte area.
- Но если вы включите OSXSAVE/XCR0 AVX bits или CoreCLR/JIT решит,
  что AVX доступен, FXSAVE уже недостаточен: upper YMM/ZMM state не
  сохраняется.

**Практический совет: в Phase E не включать AVX, держать XCR0 на
`x87|SSE`, и явно проверить, что JIT не видит AVX as OS-supported.
XSAVE оставить Phase E+ или Phase F.**

### D6: правильный glue между kernel thread и CoreCLR thread

```
KernelThread
  id
  state: Ready/Running/Waiting/Dead
  regs/context
  fxsave/xsave area
  stackBase/stackLimit/guard
  teb*
  process*
  waitBlock*
  kind: Kernel | AotApp | CoreClr
  managedBinding? -> ManagedThreadBinding

ManagedThreadBinding
  clrThreadOpaquePtr   // CoreCLR Thread*
  osThreadHandle       // handle object exposed to PAL
  osThreadId
  tlsSlots
  gcModeMirror/debug flags
```

CoreCLR Thread* **нельзя превращать в kernel scheduler object**.
ThreadStore держит список runtime threads и GC mode state.

Поэтому:
1. kernel переключает execution context;
2. PAL CreateThread создаёт `KernelThread(kind=CoreClr)` и entry
   trampoline;
3. trampoline ставит TEB/TLS/stack bounds, затем вызывает CoreCLR
   start routine;
4. CoreCLR делает SetupThread / ThreadStore / managed object state;
5. **kernel хранит CoreCLR Thread* только как opaque pointer, не
   читает поля напрямую.**

### Q4: cooperative + HPET IRQ — можно

Правило безопасности:
```
HPET IRQ:
  increment tick / set deadlineDue flag
  maybe EOI
  return

Scheduler/yield path:
  if deadlineDue:
      move expired timers to ready queue
  switch cooperatively
```

IRQ не должен: брать heap lock; писать в обычный лог; вызывать
managed/CoreCLR; делать context switch; ходить по сложным wait queues
без защиты.

Важно честно зафиксировать semantics: таймеры сработают не раньше
deadline, но реально thread станет runnable только на следующем
yield/safepoint текущего running thread-а. **CPU-bound managed loop
без yield всё равно остановит мир.** Это уже ваш G2/G4, и это нормально
для Phase E demo-scope.

### Q5: Zero-GC до Phase F — приемлемо, но с оговорками

Создаёт 3 риска:
1. **Roslyn/PowerShell быстро раздуют heap.** У Roslyn давний issue:
   каждый `CSharpScript.EvaluateAsync("...")` может генерировать новую
   assembly, которая не unload-ится, из-за чего растёт число assemblies
   и память.
2. **Любой accidental GC path должен быть fail-fast.** Не "попробовать
   SuspendRuntime", а явно PANIC: "GC suspend not supported in Phase E".
3. **Allocator locks обязательны до ThreadPool.** GcHeap.AllocateRaw,
   KernelHeap.Alloc/Free, page allocator, VM commit — всё это сейчас
   single-thread-ish. Spinlock допустим на single-core только как
   короткий non-blocking critical section с interrupts disabled. Для
   wait-able locks нужен scheduler-aware mutex/semaphore.

### Roslyn / EvaluateAsync / ThreadPool=4

Для простого REPL ThreadPool=4 хватит. `CSharpScript.EvaluateAsync<T>` —
wrapper через `RunAsync<T>(...).GetEvaluationResultAsync()`.

**Важный нюанс:** EvaluateAsync исторически не обязан немедленно
вернуть управление до завершения синхронной части скрипта; есть
открытый Roslyn issue, где EvaluateAsync фактически блокирует до
завершения или первого await.

Поэтому для Phase E:
- ThreadPool=1 — риск deadlock/starvation
- ThreadPool=4 — нормальный floor
- Для REPL лучше не делать бесконечный EvaluateAsync каждый раз; лучше
  `CSharpScript.Create`, cache script/delegate, reuse where possible

### Что вы недооценили / что добавить к gotchas

**H1. `IA32_KERNEL_GS_BASE` vs `IA32_GS_BASE`.** Это самый опасный
низкоуровневый gotcha. В вашем текущем CoreClrProbe используется
правильный MSR `0xC0000101`, то есть `IA32_GS_BASE`. Brief в G6/Q3
лучше поправить: context switch должен писать current GS base, если
нет SWAPGS.

**H2. TEB.Self offset.** Исправить:
```
gs:[0x08] StackBase
gs:[0x10] StackLimit
gs:[0x20] FiberData / Version, НЕ Self
gs:[0x30] Self
gs:[0x58] ThreadLocalStoragePointer
gs:[0x60] PEB
gs:[0x68] LastErrorValue
```

**H3. Activation clone ломается после MapKernel.** Ваш Q1 говорит
"активировать clone в E1", но в текущем коде уже есть инфраструктура,
где `VirtualMemory` пишет в active firmware root. Значит, если вы
активируете clone поздно, потеряете всё, что было MapKernel-only. Это
не теоретика, а прямой конфликт с текущим design.

**H4. Spinlock на cooperative single-core может deadlock-нуть.** Если
thread A держит spinlock и делает yield/block/panic, thread B будет
крутиться вечно. Поэтому:
- raw spinlock только для коротких секций без yield/allocation/logging;
- для PAL locks, Monitor, WaitHandle, Event/Semaphore — scheduler-
  aware blocking primitive;
- в allocator lock нельзя логировать через heap-allocating path.

**H5. Нужно реализовать не только CreateThread.** CoreCLR/managed stack
почти сразу потребует family:
```
CreateThread
ExitThread / thread return cleanup
WaitForSingleObject
WaitForMultipleObjects
SleepEx
SwitchToThread
CloseHandle / Duplicate-ish handle lifetime
GetCurrentThreadId
GetCurrentThread
TLS: TlsAlloc/TlsGetValue/TlsSetValue/TlsFree
LastError per-thread
CriticalSection / SRW-ish / condition-variable equivalents
```

### Рекомендованный порядок Phase E

```
E0. Thread/Process object model + D6 binding contract
E1. Activate pager clone early OR make MapKernel dual-root
E2. Per-thread TEB facade + GS_BASE switch test
E3. Atomic primitives: lock cmpxchg, xchg, mfence
E4. KernelThread + cooperative switch shellcode, no CoreCLR yet
E5. TimerQueue + Event/Semaphore + Sleep/Yield
E6. Allocator/page/VM locks
E7. Logical Process lifecycle + handle table
E8. ELF thread API
E9. CoreCLR PAL CreateThread/Sleep/Wait/Switch/TLS
E10. ThreadPool fixed N=4
E11. Task/Timer/ManualResetEvent probes
E12. Roslyn smoke only, not full PowerShell
E13. Full reentrancy audit R1-R8 + added H-list
```

### Итоговая формулировка для плана

**Один scheduler — да. Один универсальный thread-object для всего —
нет.** KernelThread является физическим carrier'ом исполнения; CoreCLR
Thread остаётся VM-owned managed/runtime state; связь — explicit
binding.

Это снимает главный риск G1/G14 и не ломает будущий Phase F GC suspend.
