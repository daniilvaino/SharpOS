# План развития SharpOS (DAG-редакция, post-6.1)

Документ — стратегическое направление: от freestanding C#-ядра к ОС,
которая хостит CoreCLR и запускает Roslyn/PowerShell как обычные
.NET-приложения.

**Эта редакция заменяет старую фазовую (Phase 0–7).** Причина (правила
корректировки §«Правила», + sage-1/sage-2 replan по
`work/plan-replan-request.md`): CoreCLR bring-up **перестал быть
critical path** — он доказанный базис (steps 68–73). Старая нумерация
Phase 0–7 устарела (фиксировала 6.1 как OPEN). Новый главный фронт —
**не «заставить CLR жить», а дать ему OS-substrate** (scheduler, waits,
IO, time, fs, console, PAL). Дальше — DAG, а не линейные фазы.

Пошаговый разбор — в `done/stepNN.md`. Живой реестр возможностей
hosted-режима — `docs/coreclr-hosted-limits.md` (oracle приоритизации,
расширять при каждом новом feature-touch).

---

## Архитектура исполнения — три tier'а (без изменений)

- **kernel-tier** — ядро + std + PAL. AOT C#, ring 0, минимальный std.
- **native-tier** — AOT C# user-apps на нашем std (быстрые утилиты).
- **hosted-tier** — IL+JIT через форк-CoreCLR с настоящей
  `System.Private.CoreLib`. Roslyn/PowerShell живут здесь.

Граница tier'ов — **ABI-линия** (`SharpOSHost_*` C-ABI POD + status
codes), не shared memory. CoreCLR GC'ит свой heap независимо; kernel-tier
GC не знает про hosted-объекты и наоборот.

## Архитектурные инварианты (без изменений, обязательны)

**Инвариант 1 — C# is the only source language.** В дереве нет ни
одного `.c/.cpp/.h/.asm/.s`. Low-level — одним из трёх: C# intrinsics
(`[RuntimeExport]`/`[UnmanagedCallersOnly]`/`delegate* unmanaged`/
`fixed`/unsafe), byte-array shellcode (exec-stub buffer), build-time
PowerShell codegen (не коммитим). Исключение: форк CoreCLR — внешний
репо с патчами, PAL на C#, граница чистая.

**Инвариант 2 — Naming discipline.** Канонические `System.*` namespace
— только для BCL-compat реализаций; частичное/экспериментальное — в
`SharpOS.Std.*`/`OS.*`. Цель: portable BCL-код собирается без
source-правок.

---

## Доказанный базис (steps ≤73) — НЕ переоткрывать

- Boot/IDT/паники, ACPI, **managed-EH сквозь JIT** (try/catch/finally/
  filter/rethrow/multiframe/native-origin/HW-fault), ClassCtorRunner,
  cctor, RTC/CMOS.
- **Сток CoreCLR на голом железе байт-в-байт**: RyuJIT, GC
  (non-moving mark-sweep), type loader, generics/shared-generics,
  interface dispatch, reflection, **reflection-mode System.Text.Json**,
  `yield`/итераторы, UTF-8/Regex/Guid/Base64/Path/Interlocked/lock,
  self-ID. Coverage-батарея 21/21, `exitCode=42`.
- PAL-bridge: BigStack 16 MiB, GetStackBounds, TerminateProcess-halt,
  ntdll/kernel32-шимы, **часы из CMOS** (`DateTime.UtcNow` реальное),
  env-vars пустой блок.
- Старый Phase 6.1-критерий **превышен**; Phase-7 «first hosted app»
  по факту достигнут (`NormalHello.dll` JIT-исполнен).
- Граница demo-grade (by design, не баг): финализаторы не работают,
  leaks, threading = hard-panic (`SwitchToThread`).

---

## Новый DAG (Phase A–I)

Линейность только там где помечено sequential-gate; остальное —
параллелизуемо.

### Phase A — Clean milestone freeze
- Зафлажить/убрать experiment-comfort (Probes/EH-trace) → **committable
  clean regression-режим**, не теряя boot/EH регресс-пробы.
- `docs/coreclr-hosted-limits.md` — поддерживать как oracle.
- Инвариант: hosted-батарея 21/21 не ломается ни одним последующим
  изменением (regression gate каждого step).

### Phase B — Native-tier console off-ramp (insurance, строить ПЕРВЫМ)
Параллелен A-цепочке; даёт диагностический substrate **до** SehUnwind
(чинить размотку вслепую без post-EBS канала — нельзя).
- Свой **16550 UART** драйвер (post-EBS serial; сейчас своего нет —
  всё через UEFI ConOut-зеркало, умрёт на EBS).
- **GOP framebuffer** capture + PSF/glyph рендерер + double-buffer.
- **PS/2 keyboard** (0x60/0x64, scancode→KeyEvent).
- Minimal line editor (input/backspace/enter; history позже).
- native-tier command shell: `help`/`mem`/`devices`/`run-normalhello`/
  `run-battery`/(опц. mini-Forth/StackInterpreter — бонус, не минимум).

**Критерий:** SharpOS — самостоятельная managed-OS с интерактивной
консолью и диагностикой, не «QEMU-log runner». ~2 месяца. Это
**insurance policy** если Roslyn-путь застрянет; часть B (UART/display/
keyboard) шарится с путём к §1.

### Phase C — Post-EBS survival
- **Post-EBS diagnostics contract**: serial-write всегда работает;
  panic/fault-dump **без managed-аллокаций**; bounded stack-dump;
  опц. ring-buffer.
- Snapshot до EBS: GOP ptr+dims, serial addr, ACPI-копия, memory-map
  копия, keyboard info.
- UEFI-calls за интерфейсы (`IPlatformConsole/FS/Keyboard/Timer`);
  режим «UefiServicesGone» + build-flag.
- **Физический ExitBootServices** — как только готовы UART+display+
  keyboard+memmap+ACPI+panic-path. **НЕ ждать storage/network**
  (иначе critical path раздут).

### Phase D — SehUnwind upstream fix (sequential-gate перед threading)
- **§11: починить SehUnwind frame-chain** (C#-порт RtlVirtualUnwind):
  `invalid Rip` при размотке сквозь нативные C-SEH-кадры. Единый корень
  трёх census-💥 (Socket/OpenSSL/threads); фундамент threading-EH.
- Снять локальные пластыри step-71/72 после upstream-фикса.

**Почему здесь:** оба мудреца — SehUnwind строго ДО настоящего
threading (иначе threading = генератор фантомных падений того же
класса). Делается ПОСЛЕ B (зрячая диагностика), ДО D-scheduler.

### Phase E — Cooperative threading substrate
Реализация **cooperative**, модель — совместимая с будущим preemptive.
- Per-thread stack **≥ 1 MiB + guard page** (урок Frontier-C: 128 KiB
  мало). Page-table decision: явный kernel PML4 + page-allocator (vs
  UEFI-inherited) — принять здесь, prerequisite guard-pages/VM-tracking.
- Thread struct (регистры+FXSAVE) + context-switch (byte-shellcode) +
  ready-queue + thread-state (runnable/blocked/wait-reason/safepoint).
- `Thread.Create`/`Yield`/`Sleep(0)`/`ManualResetEvent`/
  `AutoResetEvent`; cooperative TimerQueue; минимальный `ThreadPool`;
  `Task`-continuation scheduling.
- TLS per-thread (gs/fs + `RhpGetThreadStaticBase*`; Phase 5.5 был
  только main thread).
- CoreCLR threading-PAL routing: `CreateThread`/`SwitchToThread`/waits/
  `Sleep` → наш scheduler.
- Reentrancy: ревизия GcHeap/Heap-A/SharpOSHost_* на гонки (всё
  писалось single-thread).
- Preemptive (APIC-timer) — **DEFER** (Roslyn REPL fairness ОС-уровня
  не нужен; нужно лишь чтобы Task/await/waits/timers не падали).

### Phase F — Hosted-CoreCLR production mode
- GC suspend/resume **кооперация со scheduler'ом** (cooperative
  safepoints — без real signals на bare metal). **SP1, главный риск.**
- Финализаторы; реальная RetainVM/decommit policy; hosted-heap
  cleanup; убрать demo TARGET_SHARPOS ABORT/zero-GC конфиг
  (D5/D6/D8 переоткрыть).
- Инвариант ABI managed-ref discipline: `SharpOSHost_*` НЕ хранит
  hosted OBJECTREF; hosted-refs только через CoreCLR handles;
  kernel-refs никогда не отдаются как hosted; диагностика
  cross-GC-указателей.

### Phase E′ — Storage / filesystem (параллельно D/E, gate для G)
- **PCI enumeration** (ECAM/MCFG или legacy; BAR; MSI) — prerequisite
  любого device-драйвера.
- **virtio-blk** block-драйвер.
- **FAT32 read** `[critical for Roslyn]`. **FAT32 write** `[DEFER до
  PowerShell/persistence]`.

### Phase G — Roslyn REPL (§1a)
- **Hosted assembly resolver**: стратегия runtimeconfig/deps.json
  (ignore/preflatten/parse); single-app-dir probing;
  Microsoft.NETCore.App closure; Roslyn package closure;
  детерминированный fail-dump «assembly X not found». Без этого
  Roslyn/PS умрут на хаотичном probing, не на «нет feature».
- System.IO read/path/enumeration достаточно для probing.
- Cooperative scheduler + ThreadPool + waits + real-ish timers
  достаточны.
- Console I/O.
- **Timer semantics matrix** (вынести как dependency-node):
  `DateTime.UtcNow`=RTC+monotonic-delta; `Stopwatch`=TSC/HPET;
  `Task.Delay`=scheduler timer-queue; `Thread.Sleep`=wait-queue;
  timeouts=тот же backend.
- ICU/globalization: **invariant mode** (`System.Globalization.
  Invariant=true`) — убирает ICU-зависимость, режет SP4.
- **`CSharpScript.EvaluateAsync` smoke** → `var x=1+1; x.ToString()`
  == `"2"`.

### Phase H — PowerShell (§1b/§1c)
Всё из G + кратно больше: System.IO (включая write), process/env/path/
user/machine shims (Unix-PAL/System.Native bucket), pipeline/cmdlet
host, culture, runspaces. **Scope-split (честно):**
- §1b PowerShell-minimal (базовые cmdlets, pipeline; без WMI/COM/
  registry) — +6-12 мес после Roslyn.
- §1c PowerShell-full — `[DEFER индефинитно]` (WMI/COM/registry/
  surface explosion).

### Phase I — Hardware/feature expansion (DEFER до §1a)
network (virtio-net/TCP — без DNS/TLS), preemptive scheduler
(APIC-timer), SMP, FAT32-write, DNS/TLS/HTTPS, USB, multi-NIC,
power-management, crash-dump-to-disk.

---

## Decision points (трекать явно)

| Решение | Принято (sage-1+2) |
|---|---|
| Scheduler | **cooperative first**, preemptive — defer |
| Filesystem | **read-only FAT32 first**, write — defer |
| Time | **UTC-only** initially (`Now==UtcNow`, нет zoneinfo) |
| Globalization | **invariant mode** (нет ICU) |
| ExitBootServices | **до storage**, как только UART/display/kbd/panic готовы |
| PowerShell | **после Roslyn**; split §1b/§1c |
| Page tables | явный kernel PML4 (решить в Phase E) |
| Network | defer (не нужен для §1) |

## Critical path к §1a (Roslyn C# REPL) — sequential

Phase A (freeze) → **B (native console/UART/kbd)** → **D (SehUnwind)**
→ **E (cooperative scheduler+TLS+threading-PAL)** → **F (hosted-GC
suspend/production)** → **G (assembler resolver + System.IO + timers +
invariant + CSharpScript)**.
Параллельно к D/E: **E′ (PCI→virtio-blk→FAT32-read)**, остаток
Phase C (EBS), B-доводка.
`[DEFER до §1a]`: network, preemptive, SMP, FAT32-write, DNS/TLS, USB,
PowerShell.

## Stuck-point watch (после 6.1 — новые «classic stuck»)

- **SP1 scheduler↔hosted-GC suspend** (Phase F) — архитектурный, плохо
  документирован MS, нет real signals. **Главный риск.** Если не
  разрешается за 4-6 недель — отдельный sage-раунд + пере-оценка
  стратегии.
- **SP2 Roslyn JIT cold-start** — trivial-expr трогает сотни типов;
  первый compile может быть секунды. Возможно нужен AOT-предкомпайл
  части Roslyn.
- **SP3 assembly resolution/TPA closure** — Roslyn ~30-50 пакетов,
  multi-target, type-forwarding.
- **SP4 ICU/globalization** — снят выбором invariant mode.
- **SP5 memory pressure** — Roslyn прожорлив; coupled с SP1 (нужен
  реально собирающий hosted-GC).
- **SP6 PowerShell surface explosion** — кратно шире Roslyn.
- **Threading illusion** — `CreateThread` мало; всплывут TLS/stack-
  bounds/GC-suspend/waits/timer-queue/ThreadPool.
- **Post-EBS blindness** — снят Phase B/C (UART+panic-contract вперёд).
- **Unwind debt** — пластыри step-71/72 держат демо; threading может
  снова открыть рану → снят Phase D перед E.

## Вне scope (намеренно)

Приходит из CoreCLR бесплатно (не дублируем в std): LINQ, delegates,
reflection, AppendFormat/ISpanFormattable. Откладываем индефинитно:
TLS/HTTPS (мега-проект), USB (PS/2 покрывает QEMU), GUI/WM/audio,
multi-NIC real HW, PowerShell-full (§1c).

## Ориентиры по времени (honest, solo, sage-consensus)

| Веха | Диапазон |
|---|---|
| Phase A (freeze) | дни |
| Phase B (native console off-ramp) | ~2 мес (insurance) |
| Phase C (post-EBS) | 2-3 нед |
| Phase D (SehUnwind upstream) | недели-месяц (риск unwind) |
| Phase E (cooperative threading) | 1-2 мес |
| Phase E′ (PCI→virtio-blk→FAT32-read) | 3-5 нед (параллельно) |
| Phase F (hosted-GC production) | **SP1 — wildcard, 1-3 мес** |
| Phase G (Roslyn REPL §1a) | поверх — недели после F |
| **До §1a (C# REPL)** | **~6-12 мес** (или **~2-3 мес** при всех strategic-cuts: cooperative+RO-FS+UTC+invariant+no-EBS-via-UART) |
| Phase H §1b (PowerShell-minimal) | +6-12 мес после §1a |
| §1c PowerShell-full | годы / `[DEFER]` |

Off-ramp (Phase B + native shell) ≈ 2 мес — гарантированно работающий
OS-grade артефакт независимо от Roslyn-успеха.

## Правила корректировки плана (без изменений)

- Фазу дробить на подзадачи только внутри неё, не здесь.
- Недостижим критерий в scope → сужается scope → пересматривается
  критерий → двигается граница.
- Переход к следующей фазе — после критерия предыдущей на железе/
  QEMU strict-nx.
- `done/stepNN.md` фиксирует результат значимой части.
- Новая стратегическая развилка → обновляется этот документ
  (как сделано этой DAG-редакцией по sage-1/sage-2).
- Empirical checkpoints: «после X измерить Y до коммита к Z»
  (напр.: после F — Roslyn smoke до завершения G; после FAT32-read —
  измерить что Roslyn реально требует от System.IO до FAT32-write).
