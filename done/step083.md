# Step 83 — Phase E1+E2+E3+E4 LANDED: cooperative threading, first ping-pong

**Status:** First commit milestone of Phase E. Two cooperative kernel
threads alternate via `Scheduler.Yield` → `X64Asm.CoopSwitch` →
saved-state swap → next thread resumes. Boot thread is wrapped and
re-entered cleanly after both children Exit. Launcher (4/4 ELF apps)
+ CoreCLR execute_assembly + PAL/OS census run identical to pre-E
(zero regression: census stays OK=20 DEG=2 FAIL=22).

## Что взяли с собой за этот шаг

Четыре подфазы плана E (`docs/threading-architecture.md` §15):

| Sub-phase | Что | Файлы (новые / правленные) |
|---|---|---|
| **E1** | Pager root activation + XCR0 conditional lock + large-page split-on-demand + directory entries permissive в clone | `OS/src/Kernel/Paging/X64PageTable.cs` (M), `OS/src/Boot/BootSequence.cs` (M), `OS/src/Hal/X64Asm.cs` (M — Xsetbv, ReadCr4), `OS/src/Kernel/Exec/JumpStub.cs` (M — stale guard), `OS/src/Kernel/Process/AppServiceBuilder.cs` (M — stale guard) |
| **E2** | TEB facade + gs base swap + read/write MSR helpers | `OS/src/Kernel/Threading/TebFacade.cs` (N), `OS/src/Kernel/Threading/TebFacadeProbe.cs` (N), `OS/src/Hal/X64Asm.cs` (M — WriteGsBaseMsr, ReadGsBaseMsr, ReadGsQword) |
| **E3** | Atomic primitives via byte-shellcode | `OS/src/Kernel/Threading/AtomicsProbe.cs` (N), `OS/src/Hal/X64Asm.cs` (M — CmpXchg64, Xchg64, MemoryBarrier) |
| **E4** | `kernel.Thread` + `Scheduler` (Init/Spawn/Yield/Exit) + cooperative `CoopSwitch` + 2-thread ping-pong probe | `OS/src/Kernel/Threading/Thread.cs` (N), `OS/src/Kernel/Threading/Scheduler.cs` (N), `OS/src/Kernel/Threading/ThreadPingPongProbe.cs` (N), `OS/src/Hal/X64Asm.cs` (M — Fxsave, CoopSwitch) |

Плюс sage-2 review правки в `docs/threading-architecture.md` (только
текст, не код — sage прислал review уже после landing) и нвый
скрипт-анализатор пробов `tools/probe_report.ps1`.

## E1 — pager root activation (драма из трёх частей)

### 1) xsetbv #UD

Первый прогон: `xsetbv` (последовательность из E1 preamble) хард-фолтит
с #UD на голом железе QEMU/OVMF. Корень: firmware не выставляет
`CR4.OSXSAVE`, без него `xsetbv` нелегальная инструкция.

**Фикс**: в `BootSequence.ActivatePagerRootAndLockCpuFeatures` —
прочитать CR4 через `X64Asm.TryReadCr4`, проверить бит 18 (OSXSAVE),
если 0 — скипнуть `xsetbv` целиком (логгируем, что XCR0 lock не
нужен — JIT и так не увидит XSAVE-class через `CPUID.OSXSAVE=0`).
Если 1 — выставить `XCR0 = x87|SSE` как и планировали.

PV2 риск (firmware включил AVX, JIT эмиттит VEX → FXSAVE портит
upper YMM) — снят автоматически. На системах где OSXSAVE
действительно выставлен, lock сработает и явно скажет «AVX off».

### 2) Unmap 2 MiB large page → 0x565000 #PF

Второй прогон: ELF loader зовёт `Pager.Unmap(VA=0x400000)` перед тем
как mapper свой PA. `Unmap` идёт через `TryResolveMappedEntry`, ловит
**2 MiB large page** в PD[2] (firmware identity на `[0x400000..0x600000)`),
делает `*entry = 0` — **стирает весь 2 MiB регион**, включая
0x565000 где живёт KernelHeap. На следующий доступ — #PF, потому что
post-E1 clone уже active CR3 (а pre-E1 это же действие происходило
в inactive clone — невидимо для CPU).

**Фикс**: `TrySplitLargeEntry(parent, idx, level)` — split-on-demand:

- PD large 2 MiB → 512 × 4 KiB PT (каждый child наследует identity
  + flags родителя)
- PDPT large 1 GiB → 512 × 2 MiB PD large

Интегрировано в `GetOrCreateNextTable` (для `Map`-пути) и перерписан
`Unmap` (walk + split-on-large по дороге, потом чистит **один**
PT entry для целевой VA — большой регион сохраняется).

### 3) NX inheritance → ELF entry instruction-fetch #PF

Третий прогон: ELF загрузился, `JumpStub.Run`, прыжок на entry
`0x400010` → #PF vec=14 ERR=0x11 (P=1 + I=1, instruction-fetch
restriction). Leaf PTE имеет `NX=0`, но fetch блочится.

Корень: в x86-64 NX bit в **directory pointer** entries (PML4E /
PDPTE / PDE → next table) действует как MASK над всеми потомками.
Firmware отметила 2 MiB large для `[0x400000..0x600000)` с NX=1
(low memory это data в её модели). После split, моя `TrySplitLargeEntry`
проагировала NX=1 на новую directory pointer entry. И в самом
`TryCloneTableRecursive` — он memcpy'ил PML4E/PDPTE/PDE как есть,
включая NX bit. Любая loaded VA в `[0..1 GiB]` блокировалась на
fetch.

**Фикс**: directory pointer entries — **maximally permissive**: P, W,
U, NX=0. Лифы решают сами. Применено в двух местах:

- `TrySplitLargeEntry`: новая parent (директорный pointer post-split)
  = `(childPage & AddressMask) | P | W | U` (без NX)
- `TryCloneTableRecursive`: при клонировании non-large present entries
  переписывает их на permissive (memcpy всё ещё копирует leaf-only
  большие страницы как есть — их NX остаётся как у firmware)

После этого ELF apps бегут.

## E2 — TEB facade

Чистая разделительная работа: `CoreClrProbe.SetupTebFacade` 
смешивал TEB allocation (общее) с CoreCLR-specific TLS template
copying. Вынес общую часть:

- `OS/src/Kernel/Threading/TebFacade.cs`: `Allocate(stackBase, stackLimit)`
  кладёт NT_TIB шапку (Self at 0x30, StackBase at 0x08, StackLimit
  at 0x10), `SetActive(teb)` через `X64Asm.WriteGsBaseMsr`,
  `TryGetActive(out teb)` через `ReadGsBaseMsr`.
- `OS/src/Kernel/Threading/TebFacadeProbe.cs`: CLI/STI fenced swap:
  capture orig gs → alloc teb2 → SetActive(teb2) → read gs:[0x30] и
  gs:[0x10] → restore orig → проверить совпадение с teb2.Self и
  limit2. CoreCLR-specific TLS остаётся в `CoreClrProbe.SetupTebFacade`
  (не трогал, оно работает).

Probe ran clean first try: `Self=ok Limit=ok teb2=0x107360
gs.Self=0x107360 gs.Limit=0x1FE8F000 origGsBase=0x0`.

## E3 — atomic primitives

Тройка через byte-shellcode (по образцу E0/E1/E2 stubs в `AsmExecBuffer`):

- `CmpXchg64(loc, value, comparand) → old`: 9 байт, `mov rax, r8;
  lock cmpxchg [rcx], rdx; ret`. Win64 ABI: RCX=loc, RDX=value, R8=comparand.
- `Xchg64(loc, value) → old`: 7 байт, `xchg [rcx], rdx; mov rax, rdx;
  ret`. XCHG с memory operand имплицитно locked.
- `MemoryBarrier() → void`: 4 байта, `mfence; ret`.

`AtomicsProbe`: stack-resident ulong, 4 сценария (hit/miss/xchg/mfence),
все 4 ok первым прогоном.

## E4 — kernel.Thread + cooperative switch

### Thread / Scheduler

- `Thread` (managed class): Id, State (New/Runnable/Running/Exited),
  Next (queue link), ContextBlock (raw 528-byte с SavedRsp@0 + 512B
  FxsaveArea@16), Stack base/top, Teb, Entry.
- `Scheduler`: статика. `Init()` (wrap бoot thread в `Thread` с
  snapshot'нутым FXSAVE), `Spawn(entry, stackBytes=0→64KiB)` 
  (page-aligned stack из PhysicalMemory.AllocPages + synthetic 9-frame
  на стэке — 8 callee-saved GPR zeros + entry RIP), `Yield()`
  (round-robin runnable queue + CoopSwitch), `Exit()` (NOT enqueue,
  switch в next runnable; panic если никого нет).
- `ThreadPingPongProbe`: T1Entry / T2Entry — `[UnmanagedCallersOnly]`
  методы, по 5 итераций каждая, между — `Scheduler.Yield()`, в конце
  `Scheduler.Exit()`. Main yield'ит в loop пока оба не финишируют.

### CoopSwitch shellcode (39 bytes)

`OS/src/Hal/X64Asm.cs::CoopSwitch(curr, next)`:

```
push rbx/rbp/rsi/rdi/r12/r13/r14/r15      ; 12 bytes
fxsave  [rcx + 0x10]                      ; 4 bytes
mov     [rcx], rsp                        ; 3 bytes (save curr rsp)
mov     rsp, [rdx]                        ; 3 bytes (load next rsp)
fxrstor [rdx + 0x10]                      ; 4 bytes
pop r15/r14/r13/r12/rdi/rsi/rbp/rbx       ; 12 bytes
ret                                       ; 1 byte
```

### FXSAVE alignment drama

Первый прогон ping-pong: #GP на fxsave. RCX=0x147A98 — не 16-aligned.
Корень: `KernelHeap.Alloc` возвращает 8-aligned payloads (24-byte
`HeapBlock` header → +24 от 16-aligned region base → +8 mod 16).
FXSAVE строго требует 16. 

**Фикс**: `Scheduler.AllocateContextBlock()` через
`PhysicalMemory.AllocPage()` — целая 4 KiB страница per thread.
4096-aligned ⇒ 16-aligned ⇒ ContextBlock+0x10 (FxsaveArea) тоже 16.
3 потока в probe = 12 KiB overhead, незначительно. (Будущий 
оптимизатор может ввести 16-aligned allocator над KernelHeap.)

После фикса — ping-pong зелёный: T1=5/5 T2=5/5 ok.

## Sage-2 doc-only поправки (review пришёл после landing)

`docs/threading-architecture.md` обновлён согласно sage-2 review.
Ни один пункт не требует изменения кода — все либо чисто spec
clarifications, либо описание текущего фактического поведения, либо
future-work backlog.

| Sage-2 пункт | Где исправлено | Тип |
|---|---|---|
| AVX/OSXSAVE wording (OSXSAVE ≠ XCR0 mirror; AVX-enabled = OSXSAVE ∧ XCR0.SSE ∧ XCR0.YMM) | §5 | spec |
| xsetbv требует CR4.OSXSAVE=1; safe-by-omission путь в текущем коде | §17 PV2 | spec |
| mov cr3 не flush'ит global TLB; нужен CR4.PGE toggle | §17 PV4 (NEW) | future-fix backlog |
| TEB ThreadId offset (0x88 → 0x48 ClientId.UniqueThread); critic про GdiTebBatch неправ но 0x88 всё равно не Windows ABI | §6 | spec |
| PAL surface incomplete (+ Suspend/Resume, GetThreadContext, QueueUserAPC, WaitOnAddress family, IOCP trio); LowLevelLifoSemaphore.Windows IOCP not WaitOnAddress | §12 | spec |
| FS_BASE / SWAPGS invariant | §6 | spec |
| FXSAVE 16-byte alignment requirement | §3 + §5 | spec (наш код уже compliant) |
| Guard-page #PF policy | §3 | future spec |
| ProcessorCount=2 caveat (known-lie disclosure) | §9 | spec |
| XSAVE size wording (~832B AVX vs «3KB» в исходнике) | §5 | spec |
| MOOS license: public-domain Unlicense-like, **не** MIT — critic неправ | §16 | correction |
| deadlineDue lost wakeup: `xchg`-based atomic consume | §7 | future spec (нет таймера ещё) |
| Frozen JIT TLS offsets (0x00..0x500) | §6 | spec |

## probe_report.ps1 tool

`tools/probe_report.ps1` — PowerShell-скрипт для регрессионного
анализа boot log'ов:

- Покрывает ~50 пробов: kernel/Phase 1-4, EH L1..L17 со своими gold values, drivers (Serial/FbRender/Ps2/LineEdit/Shell/PCI), threading (TebFacade/Atomics/PingPong), CoreCLR (initialize/execute_assembly/exitCode/PAL census), EBS, launcher per-app
- Каждый probe: detect-regex + status-regex + expected value, классифицирует OK/FAIL/VALUE/HALT/UNKNOWN
- Exit code 0 если ни FAIL ни HALT (CI-friendly)
- ASCII-only (PS5 на Windows читает .ps1 в system codepage; em-dashes в комментах ломали парсер)
- Approved verb `Get-ProbeStatus`

## Артефакты

### Новые

- `OS/src/Kernel/Threading/Thread.cs`
- `OS/src/Kernel/Threading/Scheduler.cs`
- `OS/src/Kernel/Threading/ThreadPingPongProbe.cs`
- `OS/src/Kernel/Threading/TebFacade.cs`
- `OS/src/Kernel/Threading/TebFacadeProbe.cs`
- `OS/src/Kernel/Threading/AtomicsProbe.cs`
- `tools/probe_report.ps1`
- `done/step083.md` (этот файл)

### Модифицированные

- `OS/src/Hal/X64Asm.cs` (+ Xsetbv @0x60, ReadCr4 @0x70, ReadGsQword @0x140, WriteGsBaseMsr @0x160, ReadGsBaseMsr @0x180, CmpXchg64 @0x1A0, Xchg64 @0x1C0, MemoryBarrier @0x1E0, Fxsave @0x28C, CoopSwitch @0x380)
- `OS/src/Boot/BootSequence.cs` (Phase 3: ActivatePagerRootAndLockCpuFeatures; Phase 4: TebFacadeProbe + AtomicsProbe + ThreadPingPongProbe вызовы)
- `OS/src/Kernel/Diagnostics/Probes.cs` (+ TebFacadeSwap, Atomics, ThreadPingPong gates)
- `OS/src/Kernel/Paging/X64PageTable.cs` (TrySplitLargeEntry; GetOrCreateNextTable split-on-large; Unmap walk+split; MapKernel/TryQueryKernel/TrySetKernelFlagsEx/TryGetKernelLeafPte → s_rootTable; TryCloneTableRecursive permissive directory entries)
- `OS/src/Kernel/Exec/JumpStub.cs` (stale IsPagerRootActive guard removed)
- `OS/src/Kernel/Process/AppServiceBuilder.cs` (stale IsPagerRootActive guard removed)
- `docs/threading-architecture.md` (+108 lines от E0; +~150 lines от sage-2; §15 row E4 updated)

## Результат — probe report

```
=== SharpOS probe report -- .\last_build.log ===

[Boot]              7/7  OK (xcr0Lock VALUE because OSXSAVE=0 path)
[Phase1]            1/1  OK
[Phase2]            1/1  VALUE (entries=16)
[Phase3]            1/1  VALUE (timestamp)
[Phase4]            4/4  OK
[EH]               16/16 OK (L1..L17 all gold)
[PhaseE]            4/4  OK (TEB + Atomics + PingPong + Verdict)
[Drivers]           6/6  OK
[CoreCLR]           4/4  OK (PAL/OS census OK=20 DEG=2 FAIL=22 — нулевая регрессия)
[EBS]               2/2  OK
[Launcher]          4/4  OK (HELLO=10, ABIINFO=11, HELLOCS=21, MARKER=12)

--- totals ---  OK 47   VALUE 3   FAIL 0   HALT 0
```

## Что дальше

Phase E5 — TimerQueue (HPET-IRQ-assisted) + Event/Semaphore + Sleep/Yield.
По `docs/threading-architecture.md` §7. После этого E6 (allocator/page/VM
locks — R1-R7 reentrancy fixes), потом E7 (Process struct + concurrent
ELF launches).

Sage-2 backlog (когда понадобится):

- **PV4 (CR4.PGE)** — текущая активация работает потому что clone
  preserves firmware mappings PA-for-PA, но если будем мутировать
  firmware-mapped entries после E1, TLB вернёт старое. Acceptance:
  CR4.PGE toggle off → mov cr3 → CR4.PGE restore.
- **Guard-page handler** (§3 sage-2 add) — пока нет реальных guard
  pages, политика документирована.
- **deadlineDue xchg-based consume** (§7 sage-2 add) — пока нет
  TimerQueue, реализуется на E5.
