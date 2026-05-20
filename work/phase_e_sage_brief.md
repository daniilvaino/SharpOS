# SharpOS Phase E — Threading Architecture Brief for Sages

**Дата:** 2026-05-20 (questions frozen); **review:** 2026-05-20 (sage 1+2).
**Контекст:** Phase D (FrameChain walker integration) закрыт (commit
84ac2fd, см. `done/step081.md`). §11 закрыт, managed `try/catch` через
P/Invoke и Socket-style trap'ы работает. Threading cohort (Thread/
ThreadPool/Task/Timer/Sleep) **остаётся гарантированным HALT** — direct
`SharpOSHost_Panic` из `SleepEx`/`SwitchToThread` стабов, никакого
C-SEH; orthogonal к §11. Это и есть Phase E территория.

**Status:** §0-§5 — original brief (questions). §6 — synthesis after
sage review (corrections applied, final decisions). Full sage responses
verbatim в `phase_e_sage_responses.md`.

**FACTUAL CORRECTIONS APPLIED post-sage review:**
- Q3: TEB.Self = `gs:[0x30]`, NOT `gs:[0x20]` (Sage 2). Sage 1 had it
  right informally (mentioned `gs:[0x20]` as legacy/rare). Final TEB
  layout in §6.
- Q3: MSR = `IA32_GS_BASE (0xC0000101)`, NOT `IA32_KERNEL_GS_BASE
  (0xC0000102)` для direct `gs:[N]` без SWAPGS (Sage 2, also confirmed
  by existing `CoreClrProbe.cs` MSR usage).
- Q2 FXSAVE/XSAVE: sages disagree; final = FXSAVE с явно отключённым
  AVX bit в XCR0 (Sage 2; Sage 1 wanted XSAVE).
- Q1 timing: «activate clone EARLY» — до `MapKernel` (Sage 2 caught
  potential split-brain).

Original questions preserved below as authored.

**Цель брифа:** проверить архитектурное решение Phase E (cooperative
threading + processes + ELF threading + CoreCLR threading + multi-
task within CoreCLR) **до** написания кода — нужно вскрыть притаившиеся
hidden gotchas и выбрать поведение на 7 открытых развилках.

---

## 0. Внутренний cross-check — что может вылезть скрытое

Перед тем как обсуждать развилки, ставлю на стол потенциальные
противоречия / hidden gotchas в нашей собственной архитектуре:

| # | Possible gotcha | Тяжесть | Адрес |
|---|---|---|---|
| G1 | **Один scheduler на все три tier'а** = ELF/AOT/CoreCLR-thread'ы кладутся в одну ready queue. Но CoreCLR Thread* несёт managed-runtime state (GC roots, JIT context, locks). Если kernel scheduler не различает managed/native thread → GC scan может промахнуться. | Высокая | Привязка kernel Thread struct ↔ CoreCLR Thread* (D6 reopen) |
| G2 | **Cooperative + managed CPU-bound loop = звезды на месте.** Reflection / большой Linq над миллионом items не имеет yield-point. Блокирует ВСЕ остальные threads до return. | Средне-высокая | Acceptable for demos; production-PS = problem; SP1-adjacent |
| G3 | **HPET wakup**: HPET без interrupt = polling. Polling-thread ест CPU. С interrupt = «не совсем cooperative» (IRQ может прервать managed code). | Средняя | Hybrid: IRQ flips флажок, wake происходит на next yield, до того IRQ-handler не лезет в managed |
| G4 | **GC suspend на cooperative = wait-forever risk.** GC scan требует все-threads-stopped. Если хоть один thread не доходит до safepoint, GC blocked indefinitely. Live-lock. | Высокая | **SP1**, plan.md flagged. Решение откладывается до Phase F (`D8` reopen там же). |
| G5 | **GcHeap.AllocateRaw — single-thread** (memory note: step031 «Без Interlocked»). Concurrent alloc = race. | Высокая | Phase E13 (reentrancy audit). Минимум — spinlock вокруг AllocateRaw |
| G6 | **TLS update on context switch.** Если CoreCLR использует gs:[fixed] для `t_pCurrentThread`, наш SwitchTo обязан обновлять `IA32_KERNEL_GS_BASE` MSR (или своп gs base) — иначе CoreCLR будет видеть thread X в треде Y. | Высокая | Step 5.5 уже принёс TLS — проверить как именно ставится |
| G7 | **Process в одном AS = handle leak risk.** При process exit handle table должна очиститься. Если процесс крашится — кто чистит? Сейчас current launcher cleans up serial path, но при concurrent processes — нужен process-lifecycle. | Средняя | Phase E2 — proper Process lifecycle |
| G8 | **Stack guard pages в одном AS.** Если все процессы в одном address space, guard pages защищают только в пределах своего стека. Cross-stack overrun не ловится (но и Unix этого не ловит без MMU isolation). OK для нашего scope. | Низкая | Acceptable |
| G9 | **AssemblyLoadContext multi-tasking.** Stock-CoreCLR'овский ALC изолирует ASSEMBLIES, не threads. Несколько PS-runspaces = несколько ALC, но threads делят heap/GC. CoreCLR это умеет — мы получим бесплатно когда G1+G4 решатся. | Низкая | Pass-through, не на нашей стороне |
| G10 | **ClassConstructorRunner CAS-spin** (memory step040): нынешний early-return при state==2 = **single-thread-only** обход. С реальными threads CAS-loop надо вернуть. | Средняя | E13 audit — открытый список |
| G11 | **ExInfoHead single-thread** (memory step048): static IntPtr вместо per-thread storage. Каждый throw читает один глобал — concurrent throws испортят. | Средняя | E13 audit — мигрировать в `Thread::m_pExInfoStackHead` |
| G12 | **DebugLog reentrancy guard `s_inLine`** (memory step050) — single-thread, no atomicity. Под multi-thread torn logs. | Низкая | E13 audit — заменить на per-thread guard или real lock |
| G13 | **`GetLastError/SetLastError` через `thread_local s_lastError`** (memory step066) — placeholder, не реальный TLS. | Средняя | E13 audit — переподключить на per-thread |
| G14 | **CoreCLR threads vs kernel threads — entry semantics.** Kernel thread = function ptr. CoreCLR thread = managed delegate + JIT setup + GC roots. Один Thread struct не покроет оба. | Средняя | Разделить layer'ы: kernel Thread = низкий; CoreCLR-Thread = верхний над kernel-Thread |

**Самое важное:** G1+G4+G6 — взаимосвязанные. Если выберем плохо
схему интеграции kernel-Thread ↔ CoreCLR-Thread, GC suspend и TLS
работать не будут. G4 ещё и **SP1 main risk** — может потребовать
Phase F.

---

## 1. Target architecture (картинка)

```
┌──────────────────────────────────────────────────────────────────────┐
│ KERNEL = single OS image, ONE address space                          │
│                                                                      │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │ ОДИН cooperative scheduler + ready queue                       │  │
│  │   Thread struct: regs + FXSAVE + stack ptr + state + PID-ref   │  │
│  │   Wait primitives: Event/Semaphore/TimerQueue (HPET-deadline)  │  │
│  │   Yield points: explicit + I/O block + Sleep/Wait              │  │
│  │   NO preemption-tick (cooperative-first per plan.md)           │  │
│  └────────────────────────────────────────────────────────────────┘  │
│                              ▲                                       │
│        ┌─────────────────────┼──────────────────────────┐            │
│        │                     │                          │            │
│  ┌─────┴──────┐    ┌─────────┴────────────┐   ┌─────────┴─────────┐  │
│  │ KERNEL     │    │ AOT-APP PROCESSES    │   │ CORECLR RUNTIME   │  │
│  │ THREADS    │    │                      │   │ (one instance,    │  │
│  │            │    │ Process struct:      │   │ stat-linked)      │  │
│  │ - boot     │    │  PID                 │   │                   │  │
│  │ - idle     │    │  thread set          │   │ - GC threads      │  │
│  │ - async I/O│    │  memory regions      │   │ - ThreadPool      │  │
│  │ - timer cb │    │  handle table        │   │ - User threads    │  │
│  │            │    │                      │   │ - ALC #1: REPL    │  │
│  │            │    │ ELF/PE app launched: │   │ - ALC #2: PS      │  │
│  │            │    │ - Process A, B, C…   │   │ - ALC #3: script  │  │
│  │            │    │ - concurrent OK      │   │                   │  │
│  │            │    │ - thread API via SDK │   │ Thread API via    │  │
│  │            │    │   → kernel scheduler │   │ PAL hook → kernel │  │
│  └────────────┘    └──────────────────────┘   └───────────────────┘  │
└──────────────────────────────────────────────────────────────────────┘
```

**5 пунктов пользователя:**
1. Потоки в ядре — `KERNEL THREADS` колонка.
2. Процессы (минимум) — `AOT-APP PROCESSES` колонка, Process struct.
3. Потоки в ELF — через app SDK, проксируется в kernel scheduler.
4. Потоки CoreCLR — PAL hook (`CreateThread`/`SwitchToThread`/waits).
5. Multitasking CoreCLR — `AssemblyLoadContext` (stock CoreCLR feature),
   получаем бесплатно когда 4 работает.

**Архитектурные инварианты:**
- ОДИН address space (без MMU-изоляции; Process = logical isolation).
- ОДИН scheduler на все tier-а.
- ОДНА CoreCLR instance (stat-linked).
- Cooperative-first; preemption — Phase Future.

---

## 2. Семь открытых развилок (нужен sage-input)

### Q1. Page tables — explicit kernel PML4 vs UEFI-inherited?

**Текущее состояние (verified):** `OS/src/Kernel/Paging/X64PageTable.cs`
(696 строк, plus `Pager.cs` 219) — у нас **уже есть hybrid с
паразитной инактивностью**:
- `s_kernelRootTable` = UEFI-inherited CR3 (текущий running);
- `s_rootTable` = клонированный recursive PML4 (наш «pager root»);
- На boot'е делаем `TryCloneTableRecursive` — копируем всю иерархию
  ОТ UEFI inherit в наш s_rootTable.
- **CR3 никогда не переключаем** — продолжаем run на UEFI inherit.
- `Map()` operations target `s_rootTable` (the inactive clone) →
  **мapping'и НЕ видны runtime'у** (комментарий явно: «its entries
  are invisible to» — line 182).
- `IsPagerRootActive()` всегда даст false (CR3 не свой).

Это значит: у нас **третье состояние** — клон-но-неактивен. Не A
(полностью свой PML4 with switch CR3) и не B (просто inherit без
клонирования). Это «теневая PML4 для перспективной активации».

**Опции для Phase E:**
- **A. Activate the clone (`mov cr3, s_rootTable`).** Switch CR3 на
  свой клон, дальше делаем Map() и они становятся видны. Pro:
  minimal change, infrastructure готова. Con: при switch — момент
  «split-brain», все running mappings должны быть тождественны.
- **B. Rebuild from scratch.** Сами строим PML4 с нуля, identity-map
  + selective remapping. Pro: контроль; Con: переоткрывает риски
  «забыли что-то замапить».
- **C. Stay с inactive-clone, но добавить «proxy»** — Map() пишет
  И в inactive clone И в active UEFI inherit. Pro: zero-switch
  изменения. Con: дублирование state, конфликты.

**Predisposition:** **A (activate the clone)**. Инфраструктура
готова, только switch CR3 не сделан. Сделать активацию в Phase E1
как первый сanity-checkpoint.

**Что просим sage:** что мы пропустили в инактив-clone'е, из-за
чего нет switch'а? Возможно есть исторический коммит со списком
«остаётся сделать». Что R2R-loaded DLL'ки сейчас живут в каких VA —
не сломает ли активация?

### Q2. Context-switch механизм

**Состояние:** есть наработка — `JumpStub` (Phase C / step 78,
trampolinе for app launch), `CaptureContextStub` (для EH), уже
существуют как byte-shellcode. Layout: GP regs + FXSAVE + stack ptr +
segment selectors.

**Опции:**
- **A. Byte-shellcode (как JumpStub)** — emitter в C# пишет машинные
  байты в EfiLoaderCode buffer; SwitchTo(currThread, newThread) =
  pointer to shellcode. Pro: invariant 1 (single-source C#), уже
  pattern есть; Con: 100-200 байт shellcode писать аккуратно.
- **B. Inline-asm в hand-written .S/.asm** — нарушает invariant 1, мы
  это **не делаем**.
- **C. `[UnmanagedCallersOnly]` + ILC inline asm** — C# не позволяет
  inline asm; нет.

**Predisposition:** A (byte-shellcode). Образец готов.

**Что просим sage:** sequence FXSAVE/load — нужен ли `xsetbv`/AVX512
state save? Для CoreCLR Hello World JIT, скорее всего достаточно
FXSAVE/FXRSTOR (legacy). Но Roslyn использует SSE2/AVX2 — нужно
тестировать. Подсказать минимальный safe-floor.

### Q3. TLS detalisation — где per-thread storage?

**Текущее состояние (verified):** Phase 5.5 принёс **минимум** —
`_tls_index = 0` link-time placeholder ([`CrtAndEhStubs.cs:602`](
../OS/src/PAL/SharpOSHost/CrtAndEhStubs.cs#L602)). Сам комментарий
там: «Runtime access via `gs:[58h]` is a SEPARATE wall (no TEB setup
yet) — those specific ctors (`t_random`, `g_threadHolderTLS`) will
fault when reached если no TEB facade.»

Также `gs:[10h]` (TEB.StackLimit) специально замечен в `ChkstkStub.cs`
как требующий TEB. То есть **TEB facade НЕ построена** — Phase 5.5
не дала per-thread storage в полном смысле; «main thread» функционирует
потому что специфичные cctor'ы (`t_random`, `g_threadHolderTLS`) ещё
не triggered.

Это значит **D7 в реальности — partially covered, а не fully**.

**Опции для Phase E:**
- **A. TEB-facade на per-thread (Win64-canonical).** Аллоцируем
  TEB-like struct per kernel Thread; `IA32_KERNEL_GS_BASE` (через
  WRMSR) указывает на неё; context switch пишет MSR. CoreCLR'овские
  `gs:[58h] + idx*8` запросы попадают в `TEB.ThreadLocalStoragePointer`
  → массив указателей on TLS-modules. Pro: drop-in Win64; CoreCLR
  ожидает ровно это.
- **B. Custom layout (нашу собственную struct).** gs:[base] = `Thread*`
  напрямую (не TEB). Pro: проще. Con: ломает CoreCLR, который
  hardcoded'но ожидает `gs:[58h]` и `gs:[10h]` paths.
- **C. Hybrid:** TEB-facade с минимально необходимыми offsets
  (`gs:[10h] StackLimit`, `gs:[20h]` Self, `gs:[58h]` TLS-array),
  плюс `gs:[base+0x100]` = `Thread*` для нашего own usage.

**Predisposition:** **C (hybrid)** — минимум TEB-compat но reserve
зоны для own bookkeeping.

**Что просим sage:** **что *именно* CoreCLR читает из gs:[N]**?
Конкретные offset'ы и semantics: `gs:[58h]` это
`TEB.ThreadLocalStoragePointer` (TLS-slot array), `gs:[10h]` это
`TEB.StackLimit`, `gs:[20h]` это `TEB.Self`. Что **ещё** читает
форк/managed-JIT? Особенно `t_pCurrentThread` — какой path?

### Q4. Wait-queue / cooperative-block

**Сценарий:** `ManualResetEvent.WaitOne(1000)`. Thread должен
блокироваться на event, до того момента когда (a) event signaled или
(b) timeout истёк. Cooperative — никаких real signals.

**Опции:**
- **A. Pull-polling.** Thread проверяет state периодически; никогда
  не «спит». Pro: тривиально. Con: 100% CPU pinned waiting threads.
- **B. Sleep + Wake list.** Thread добавляется в wait list event'а
  + в timer queue с deadline; scheduler вычеркивает из ready queue.
  Wake: либо `Set()` на event'е пинает (переносит в ready), либо
  HPET interrupt дотрагивает timer queue → пинает. Pro: правильный
  scheduler. Con: complexity.
- **C. Hybrid:** Timer = HPET с interrupt (но IRQ только взводит
  флажок-«deadline-due»); next yield checks flag, перебрасывает
  expired в ready. Это сохраняет cooperative semantics (managed code
  не прерывается mid-execution), HPET handler не лезет в managed.

**Predisposition:** B + hybrid timer как C (B sets state, HPET-IRQ
nudge'ает scheduler).

**Что просим sage:** не нарушим ли мы invariant cooperative-first
тем, что у нас есть IRQ-handler для HPET? Технически handler не
делает context switch, только set flag → wake on next yield. Это
«cooperative + IRQ-assisted timer» — приемлемо?

### Q5. GC suspend на bare metal (D8 reopen)

**Сценарий:** GC хочет all-threads-stopped, чтобы сделать stack scan.
Stock CoreCLR на Linux использует `SIGUSR1` (PAL signal) → ucontext в
handler, чтобы prozriach'ить stack. На bare metal:

**Опции:**
- **A. Cooperative safepoint poll** — managed code сам проверяет
  flag `g_suspendRequested`, и если установлен → освобождает GC
  state и блокируется (как на yield-point). JIT добавляет poll-call'ы
  на back-edges loops. Pro: deterministic. Con: live-lock if thread
  doesn't yield (CPU-bound loop без back-edges); требует JIT
  cooperation; SP1.
- **B. APIC-IPI (Inter-Processor Interrupt)** — отсылаем self-IPI,
  handler делает stack snapshot. Но мы single-core; self-IPI = no-op
  cause we ARE that core. Не работает без SMP.
- **C. Single-thread mode of GC (Zero GC stays, D8 doesn't reopen)** —
  оставляем D5/D8 в текущем состоянии (no threading + Zero GC),
  для Phase E не трогаем GC. Откладываем до Phase F.
- **D. Cooperative + JIT-emitted polls (вариант A) + Roslyn-AOT
  предкомпайл частей** — приведённое в plan.md SP2-fix.

**Predisposition:** **C сначала** — в Phase E не трогаем GC. Threading
работает с Zero-GC (heap-grow only). Phase F (где SP1 = real GC suspend)
— отдельный фронт. Это согласуется с plan.md (SP1 — Phase F, не E).

**Что просим sage:** правильно ли мы понимаем, что cooperative
threading **без** GC-suspend возможно? Zero-GC просто аллоцирует и
никогда не GC'ит; threading работает, GC pause не происходит. Risk:
ThreadPool / Task завалит heap → ABORT.

### Q6. ThreadPool size

**Сценарий:** stock CoreCLR ThreadPool spawn'ит N workers по нагрузке.
N зависит от CPU count (Environment.ProcessorCount).

**Опции:**
- **A. Fixed N=2-4** — без elasticity.
- **B. Lazy grow по нагрузке** — старт N=2, growth по unique
  enqueue rate.
- **C. ProcessorCount-based** — но single-core SharpOS → ProcessorCount=1
  → ThreadPool=1. Нелогично.

**Predisposition:** A с N=4. Достаточно для Roslyn timing + 2 ALC PS-
runspaces.

**Что просим sage:** какой минимум для Roslyn `CSharpScript.Evaluate-
Async`? Эта функция использует Task внутри. Если ThreadPool=1 —
deadlock на await?

### Q7. Reentrancy audit detail

**Список single-thread assumptions** (из running notes, sweep):

| ID | Файл | Что | Fix |
|---|---|---|---|
| R1 | `std/no-runtime/shared/Threading.cs` | Interlocked.CompareExchange = read-modify-write (не атомарно) | Заменить на `lock cmpxchg` через byte-shellcode |
| R2 | `Runtime/ClassConstructorRunner.cs` | CAS-spin early-return при state==2 | Вернуть полный CAS-loop с yield |
| R3 | `OS/src/Boot/EH/ExInfo.cs` | `s_pExInfoHead` static | Per-thread (gs:[off]) |
| R4 | `OS/src/Kernel/Diagnostics/DebugLog.cs` | `s_inLine` reentrancy guard | Per-thread flag |
| R5 | `OS/src/Kernel/Memory/KernelHeap.cs` | Alloc без lock | Spinlock вокруг alloc/free |
| R6 | `std/no-runtime/shared/GC/GcHeap.cs` | AllocateRaw без lock | Spinlock |
| R7 | `pal/sharpos/crt_imp_stubs.cpp` GetLastError | `thread_local s_lastError` placeholder | Реальный per-thread storage через gs:[off] |
| R8 | `OS/src/Boot/EH/StackFrameIterator.cs` etc. | walker statics | Per-thread state object |

**Predisposition:** делать R1..R8 поэтапно во время Phase E13 —
сначала minimum работающий (locks), потом per-thread optimization.

**Что просим sage:** мы что-то пропустили в списке? Есть ли в форке
ещё единичные «единственный thread»-assumption'ы которые надо вынести?

---

## 3. Существующие решения и context

### 3.1 plan.md Decision-points (locked)

| Решение | Значение |
|---|---|
| Scheduler | **cooperative first**, preemptive defer |
| Per-thread stack | **≥ 1 MiB + guard page** (урок Frontier-C: 128 KiB мало) |
| Page tables | explicit kernel PML4 — **«решить в Phase E»** (= это Q1) |
| TLS | per-thread `gs/fs` + `RhpGetThreadStaticBase*` — done Phase 5.5 = D7 covered |
| ExitBootServices | сделано (Phase C); post-EBS substrate = default boot (step 91) |
| GC | Zero-GC remains in Phase E; real GC + suspend = Phase F (SP1) |

### 3.2 D1-D20 PAL D-decisions (`work/PAL/D1-D20 FINALIZED/`)

| ID | Что | Reopen в Phase E? |
|---|---|---|
| **D5** | `ABORT_FATAL` стабы в `CreateThread`/thread-PAL. Zero-GC + finalizer skip + no ThreadPool init → `CreateThread` физически НЕ вызывается. | **ДА** — Phase E4 реализует реальные стабы |
| **D6** | Thread state ownership (vm/ vs host). Initial inclination: vm/ owns. | **ДА** — Phase E1 решает (см. G1, G14) |
| D7 | TLS covered via D2/Phase 5.5 | done |
| **D8** | GC thread suspension. Initial: SIGUSR1+ucontext (Linux PAL). На bare metal — другой механизм. | **частично** — мы остаёмся на Zero-GC в Phase E; D8 переоткрывается в Phase F |

Phase D (только что закрытая) **не** трогала D5/D6/D8 — она про
`Thread::m_pFrame` чтение, не про threading-PAL routing.

### 3.3 Single-thread legacy notes (will refactor)

(см. таблицу Q7 R1..R8)

Кроме того:
- step028: kernel + Workstation GC have parallel heaps (двойная
  bookkeeping).
- step031: «Без Interlocked (kernel single-threaded)».
- step035: TSS+IST для #DF — Phase 3/scheduler требует.
- step039: HPET interrupt comparators not active — Phase 3 нужно.
- step048: ExInfoHead single-thread; per-thread migration Phase 3.
- step052: Thread/Abort/Hijack/DoNotTriggerGc Skip'нуты как safe-for-
  single-thread в форке.
- step059: «Phase 1 = closed; следующая — SUPER-6 (multi-thread)».
- step066: `GetLastError/SetLastError` через `thread_local s_lastError`
  — placeholder.

### 3.4 Воровская карта (что и откуда)

| Кusок | Откуда | Что берём |
|---|---|---|
| Context-switch shellcode shape | MOOS [Threading.cs:14-50](../gc-experiment/MOOS/Kernel/Misc/Threading.cs#L14) — `IDTStackGeneric*` struct + register-save pattern | Структурный референс stack layout'а. Сам preemptive-driver не используем (cooperative). |
| Per-thread stack alloc | MOOS `Allocator.Allocate` pattern; мы свой через Pager | Pattern идея, не код. |
| HPET driver | MOOS [HPET.cs](../gc-experiment/MOOS/Kernel/Driver/HPET.cs) (63 строки, Unlicense) | Прямой port (как FAT32/AHCI делали). Attribution-header. |
| HPET interrupt setup | MOOS [LocalAPICTimer.cs](../gc-experiment/MOOS/Kernel/Driver/LocalAPICTimer.cs) (37 строк) + APIC docs | HPET configure IRQ vector — есть в MOOS. |
| TLS scheme | CoreCLR форк `vm/threads.h` `t_pCurrentThread` etc. + наш Phase 5.5 bring-up | Дополнить scheduler integration. |
| AssemblyLoadContext (multi-task) | stock CoreCLR `System.Runtime.Loader.AssemblyLoadContext` | Pass-through. Если G1+G4 работают, ALC «само» работает. |
| Frame layout knowledge | Phase D наработки (`vm/frames.h`) | Уже имеем — для GC suspend в Phase F. |
| Cooperative wait primitive design | CoreCLR `pal/src/synchmgr` (Unix PAL Event/Semaphore) | Design pattern. Реализация наша через TimerQueue + ready/wait queues. |

**License-status матрица** (memory `reference_fs_steal_license_map`):

| Source | License | Можно ли красть | Применимость к threading |
|---|---|---|---|
| MOOS | Unlicense (public domain) | ✅ direct port + attribution | HPET, IDTStackGeneric layout |
| CoreCLR fork | MIT (наш форк) | ✅ внутри форка | TLS, ALC, threadpool design |
| stock CoreCLR (dotnet/runtime) | MIT | ✅ копировать | Synch manager pattern |
| old-SharpOS | GPL | ❌ табу | — (нельзя даже смотреть architecture) |

---

## 4. Конкретные просьбы

1. **Архитектурный sanity check.** Пройти секцию 0 (G1-G14) — какие
   gotchas мы недооценили или упустили? Есть ли cross-tier conflicts
   которые мы не заметили?
2. **Q1-Q7 — голосование/корректировка predisposition'ов** с
   обоснованием. Особенно Q4 (cooperative + IRQ-assisted timer) и
   Q5 (Zero-GC до Phase F — приемлемо?).
3. **Hidden CoreCLR dependencies.** Что в форке (vm/, gc/, pal/sharpos)
   уже неявно ассумит multi-thread, и какие баги мы триггернем, когда
   CreateThread реально начнёт работать (D5 reopen)?
4. **Roslyn / PowerShell concurrency requirements.** Для §1a (REPL)
   ThreadPool=4 хватит? Какие конкретно managed APIs использует Roslyn
   `CSharpScript.EvaluateAsync` и сколько threads они тащат?
5. **D6 — Thread state ownership.** Initial inclination plan.md: vm/.
   Но мы хотим **один scheduler на все тиры** — это смешит owners
   (kernel Thread struct для scheduler + CoreCLR Thread* для managed
   state). Какой клей правильнее?

---

## 5. Декларация scope

**Что в Phase E:**
- kernel scheduler (cooperative)
- Thread struct + context switch
- per-thread stack (≥ 1 MiB + guard)
- TLS multi-thread
- Wait/Event/Semaphore/TimerQueue
- Thread.Create/Yield/Sleep/ManualResetEvent
- CoreCLR threading-PAL routing (D5/D6 reopen)
- Process abstraction (PID + thread set + handle table)
- ELF thread-API via SDK
- Reentrancy audit (R1..R8)
- ThreadPool / Task subset

**НЕ в Phase E:**
- Preemption (APIC-timer-tick) — Phase Future
- Real GC suspend (D8 deep reopen) — Phase F (SP1)
- Concurrent GC — Phase Future
- SMP (multi-core) — Phase Future
- MMU process isolation — not in plan
- Network — Phase I
- Sockets P/Invoke (хоть catchable уже) — Phase E′/G
- AssemblyLoadContext implementation — stock CoreCLR feature, comes free
  with E4 + F

---

**Контактная информация:** kernel commit history at
`git log --oneline | head -20`. Latest: `84ac2fd step 90: Phase D landed`.
Plan canonical source: `plan.md`. D-decisions: `work/PAL/D1-D20 FINALIZED/`.

---

## §6 — Synthesis (post-sage review)

Two independent sages reviewed (см. `phase_e_sage_responses.md`).
Strong convergence on architecture; two factual errors found
(applied at top of doc).

### Convergence table

| Item | Final decision | Both? |
|---|---|---|
| D6 ownership | **Split**: kernel.Thread (physical) + CoreCLR Thread* (managed) linked by explicit `ManagedThreadBinding` pointer. Kernel never reads CoreCLR-Thread fields — opaque. | ✓ |
| Q1 page tables | A (activate clone) — **must be EARLY** (Sage 2): activation before `VirtualMemory.MapKernel` to avoid losing live mappings. Either move activation into `InitializePager()` immediately after clone, or introduce dual-write/backfill. | ✓ |
| Q2 ctx-switch | A (byte-shellcode) — save area: GPR + RSP/RIP/RFLAGS + fxsave64 area (512 bytes) + gsBase/TEB pointer | ✓ |
| Q3 TLS | C (hybrid TEB-facade) with **corrected offsets** + **`IA32_GS_BASE` MSR** (NOT KERNEL_GS_BASE) | ✓ |
| Q4 wait queue | B + C: event/timer queue + HPET-IRQ-assisted with strict IRQ discipline (no heap, no log, no managed, no ctx-switch) | ✓ |
| Q5 GC suspend | C (Zero-GC до Phase F) + **explicit fail-fast PANIC** on any GC-suspend attempt | ✓ |
| Q6 ThreadPool | N=4 fixed (Sage 1: grow-to-8 bound; Sage 2: fixed=4 floor) | ✓ |
| Pre-E1 verification | G6 (PV1) — Phase 5.5 actually swaps gs base on ctx-switch? **MUST verify before starting E1.** | ✓ |

### Final TEB layout (Sage 2 corrected, both sages reviewed)

```
TEB / NT_TIB layout (minimum 0x100 bytes, exposed via gs:):
  +0x00: NtTib.ExceptionList  = NULL
  +0x08: NtTib.StackBase
  +0x10: NtTib.StackLimit
  +0x18: NtTib.SubSystemTib   = NULL
  +0x20: NtTib.FiberData / Version  (legacy — DO NOT use for Self)
  +0x30: NtTib.Self            = pointer-to-this-TEB     ← correct Self offset
  +0x58: ThreadLocalStoragePointer = pointer to TLS-module-bases array
  +0x60: PEB                  = NULL (we have no PEB; pointer is read but
                                 only specific paths dereference)
  +0x68: LastErrorValue       (DWORD; per-thread `GetLastError`)
  +0x88: ThreadId             (DWORD)
  +0x100..0x500: TlsSlots (64 slots; can be zero-init initially)
  +0x600: SharpOS-private: pointer back to kernel.Thread (our bookkeeping)
```

MSR for gs base: **`IA32_GS_BASE = 0xC0000101`** (not 0xC0000102 which is
`IA32_KERNEL_GS_BASE`, only meaningful with `SWAPGS`). Since we don't
use SWAPGS (single AS, no kernel/user split), all `gs:[N]` reads go
through `IA32_GS_BASE`.

Context switch must `wrmsr 0xC0000101, newThread.tebPointer` on each
switch.

### Q2 FXSAVE/XSAVE — divergence resolution

Sage 1: XSAVE from start (AVX2 для Roslyn).
Sage 2: FXSAVE достаточно если AVX отключён в XCR0.

**Resolution: Sage 2 (FXSAVE + AVX-off).** Обоснование:
- На bare metal **мы контролируем XCR0** (через `xsetbv` при boot'е).
- RyuJIT проверяет AVX через `Avx.IsSupported`, который реально
  делает CPUID + `OSXSAVE` bit check. Если XCR0 не включает AVX bit,
  `OSXSAVE` reads false → AVX disabled → RyuJIT падает на SSE2.
- FXSAVE = 512 байт vs XSAVE+AVX-512 = ~2-3 KB. Per-thread economy
  важна (4-8 threads × 512 vs × 3000).
- Phase E demo не требует AVX.
- Sanity-check для E0: явный `xsetbv` с `XCR0 = SSE | x87` only;
  verify Roslyn JIT не emit'ит VEX-prefix.
- Переход на XSAVE — single config change в Phase F+ когда нужно.

### Expanded G-list (sages contributed)

| ID | Source | Что | Action |
|---|---|---|---|
| G15 | Sage 1 | IST stack для HPET vector (как для #DF в step 35) | E1 prep |
| G16 | Sage 1 | Yield point density / CPU-bound starvation | Phase F |
| G17 | Sage 1 | `gs:[0x58]+idx*8` torn pointer при module load | E2 design |
| G18 | Sage 1 | Process lifecycle на panic (consistency) | E7 |
| G19 | Sage 1 | **JIT-emitted direct `mov rax, gs:[offset]` для `[ThreadStatic]`** — critical, no PAL hop | E2 verify |
| G20 | Sage 1 | Crst pool exhaustion (low priority) | observe |
| H1 | Sage 2 | `IA32_GS_BASE` vs `IA32_KERNEL_GS_BASE` (factual fix applied) | done |
| H2 | Sage 2 | TEB.Self offset 0x30 (factual fix applied) | done |
| H3 | Sage 2 | Clone activation timing — before MapKernel | E1 |
| H4 | Sage 2 | Spinlock cooperative single-core deadlock — need scheduler-aware blocking | E3/E6 |
| H5 | Sage 2 | Full PAL surface for D5 reopen (15+ functions, not just CreateThread) | E9 |

### Expanded R-list

R9-R13 (Sage 1): AppDomain.cs, g_pConfig, profiler hooks, m_pFrame
per-thread anchor, DAC globals.

R14-R20 (Sage 2): PhysicalMemory, VirtualMemory, page-table spare list,
process list / PID allocator, handle tables, console/logging path
locks, root lists, PAL critical sections, wait handles, monitor/syncblock,
current-thread/process globals.

### Final Phase E sequence (E0-E13)

```
E0  — Pre-E1 verifications (PV1/PV2/PV3) + decision doc snapshot
E1  — Page table activation EARLY (Sage 2 H3) OR dual-write design
E2  — TEB facade (gs:[0x30] Self, IA32_GS_BASE swap) + ctx-switch verify on single thread
E3  — Atomic byte-shellcode primitives (`lock cmpxchg`, `xchg`, `mfence`)
E4  — KernelThread + cooperative ctx-switch + 2-thread alternation (no CoreCLR yet)
E5  — TimerQueue (HPET-IRQ-assisted) + Event/Semaphore + Sleep/Yield
E6  — Allocator/page/VM locks (scheduler-aware blocking) + critical R-items
E7  — Logical Process: PID + thread set + handle table; exit cleanup (G18)
E8  — ELF thread API via AppSDK proxy
E9  — CoreCLR PAL routing (D5 reopen full): CreateThread/Wait/Sleep/Switch/TLS/Crst — H5 list
E10 — ThreadPool fixed N=4 (grow bound 8)
E11 — Task / async-await; SynchronizationContext
E12 — AssemblyLoadContext smoke — 2 scripts isolated
E13 — Reentrancy audit pass 2 (R-list + H-list driven by E9-E12 crashes)
```

Realistic estimate (Sage 1): **8-12 weeks E1-E10 + 4-6 weeks E11-E13 =
3-4.5 months solo**.

### Next action — `docs/threading-architecture.md`

Single canonical decision doc (analog of `docs/eh-model.md`) consolidating
all decisions above. After that — E0 (PV1/PV2/PV3 verifications) +
commit «step 92: Phase E preparation».

---

**End of synthesis. Decisions are LOCKED here. Implementation deviation
must be justified by what's actually encountered in code, not by
preference.**
