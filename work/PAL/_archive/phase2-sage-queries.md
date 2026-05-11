# Phase 2 sage queries — PAL design + Linux spike

## Контекст для мудрецов (общий префикс к обоим query)

SharpOS — freestanding C# unikernel (NativeAOT + NoStdLib + UEFI + win-x64),
single-thread. Phase 1 закрыт: managed EH (throw/catch/rethrow/finally/filter
/HW-fault/multi-frame/collided-unwind/stack-trace, 17/17 gates), GC mark-sweep
с conservative stack scan, BCL коллекции (List/Dict/Stack/Queue/HashSet/etc),
StringBuilder, ACPI, HPET, RTC, ClassConstructorRunner с TypePreinit
materialization. Apps работают как native-tier ELFs с собственным static GC pool.

Архитектурный план: Phase 6 — fork CoreCLR с custom PAL, hosted-tier для
Roslyn/PowerShell. Phase 2 (текущая) — PAL design + Linux spike. Phase 3 —
scheduler/threading/Task/async. Phase 5 — drivers (parallel).

Phase 2 deliverables (per `plan.md`):

1. **PAL design caталог** — разбор `src/coreclr/pal/inc/*.h`. Выделить minimal
   subset который CoreCLR реально дёргает (POSIX-shape). Декомпозиция по
   областям: memory / threading / sync / file I/O / time / TLS / signals /
   executable memory для JIT. Финал — спека с сигнатурами и semantics.

2. **De-risk spike на Linux host'е** — 1-2 weeks experiment. Подменить
   системный PAL на наши stubs прямо на Linux, прогнать managed Hello World
   **с JIT-компиляцией**. Validates что архитектура CoreCLR-fork в принципе
   работает до того как полгода тратить на real PAL.

**КРИТИЧНО**: у нас CoreCLR sources уже клонированы локально:
`gc-experiment/dotnet-runtime/src/coreclr/`. Реальные пути:
- `pal/inc/*.h` — header surface
- `pal/src/` — Linux/macOS implementations (reference)
- `nativeaot/Runtime/` — runtime helpers (для понимания какие PAL functions called)
- `vm/` — main runtime engine
- `gc/` — GC implementation
- `jit/` — RyuJIT

Можно ссылаться на конкретные file paths и line numbers — мы их вытащим.

Просим **указывать конкретные файлы которые надо прочитать**, не
generic advice типа "read the PAL headers".

---

## Query 1 (Sage 1 — broad architectural perspective)

> Мы готовимся к Phase 2 — каталогизация PAL для предстоящего CoreCLR fork
> в Phase 6. Раньше PAL design делалcя руками сидя над headers. У нас CoreCLR
> sources на руках, можем делать automated extraction + manual review.
>
> 1. **Какой минимальный PAL surface для запуска managed Hello World с JIT?**
>    Не "что в pal.h полностью", а "что CoreCLR ACTUALLY calls" в hello-world
>    scenario — `Console.WriteLine("hello")` через JIT (managed.exe → IL →
>    JIT → native code → стандартный output).
>
>    Подскажи стратегию extraction: какие entry points (`coreclr_initialize`,
>    `coreclr_execute_assembly`) trace'ить? Какие nm/objdump/grep patterns
>    использовать чтобы найти все undefined PAL symbols которые CoreCLR
>    binary references? Через какие layers идёт fan-out (BCL → vm/ → pal/)?
>
> 2. **Mac/Linux PAL difference — какой baseline copy'ить?** Linux PAL
>    предположительно closer к нашей будущей kernel (POSIX-style), но Mac
>    может иметь cleaner abstractions. Какой выбрать как reference?
>
> 3. **De-risk spike — какой scope realistic for 1-2 weeks?** Stub'нуть
>    весь PAL surface зayro? Или подмножество? Где разумные cuts (e.g.
>    no signals, no threading в spike — single-threaded JIT)?
>
> 4. **Какие PAL функции CRITICAL и какие nice-to-have?** Hierarchy:
>    must-have для basic Hello World, must-have для Task/async, must-have
>    для Roslyn (file system/reflection), nice-to-have (sockets/logging).
>
> 5. **Известные tarpits** — где люди обычно zaстряют? Signal-based EH
>    delivery (CLR uses POSIX signals для HW exceptions on Linux)?
>    Threading model (pthread keyed TLS vs `__thread`)? Memory model
>    (CoreCLR's `VirtualAlloc/VirtualFree` semantics vs simple `mmap`)?
>
> Reference paths которые точно нужны и какие — SKIP (slow path для
> reading): `pal/inc/`, `pal/src/`, `nativeaot/Runtime/`, `vm/init/`,
> `vm/clrhost*`, `dlls/mscoree*`?

## Query 2 (Sage 2 — deeper technical perspective)

> Phase 2 PAL design + Linux spike. Имеем CoreCLR sources локально.
>
> 1. **Точная procedure для extract'а реального PAL surface**:
>    - `objdump -T libcoreclr.so | awk '$2=="U"'` — undefined symbols
>    - `nm -u` filter
>    - cross-ref с `pal/inc/*.h` declarations
>    - eliminate libc symbols (printf/malloc) что libc provides нативно
>    - eliminate POSIX symbols (read/write/mmap) что Linux kernel provides
>    - что остаётся = CoreCLR-specific PAL что мы должны implement
>
>    Дай конкретные shell commands + post-processing pipeline для
>    automated extraction. Куда output дампить — markdown table?
>
> 2. **Calling convention details которые stand'арт PAL header не показывает**:
>    - Который функции stdcall vs cdecl (на x64 — 99% one convention,
>      но возможно edge cases)
>    - Который функции принимают ABI-specific data (GCC/Clang internal
>      structs vs portable types)
>    - Который функции require specific TLS/FS setup (CoreCLR's
>      Thread Object pointer)
>
> 3. **For the Linux spike**:
>    - Простейший hello-world managed binary который имеет MAXIMUM coverage
>      по PAL functions exercised? Что-то вроде Roslyn-compiled .NET 7
>      Hello.exe?
>    - Как hostfxr/hostpolicy interact с PAL? Можно ли skip тhem (use
>      direct `coreclr_initialize` from C host)?
>    - Stub failure modes: PAL function returns ERROR_NOT_SUPPORTED → as
>      далеко GC может нести нас? PAL function silently returns 0 →
>      крашит лишь quickly? Какой modes preferable для diagnostic?
>
> 4. **Concrete code paths из CoreCLR sources которые CRITICAL для
>    Hello World и которые TRIVIAL пропустить**. Например:
>    - `EEStartupInit` — must-trace
>    - `g_pCEEInfo` initialization — needed before any JIT?
>    - `LoadAssembly` flow — needed для finding Main()
>    - Threading initialization — какой minimum (just main thread TLS)?
>    - GC initialization — что called before first managed allocation?
>
>    Конкретные file:line references would be valuable.
>
> 5. **Risks/blockers что мы можем not anticipate**:
>    - Hardware-specific assumptions (e.g., CLR assumes specific TLS
>      register layout)
>    - Compiler-specific assumptions (CLR built с MSVC vs Clang vs GCC)
>    - Platform-specific assumptions (Linux ELF vs Mach-O vs PE)
>
>    На Linux spike мы будем иметь реальный Linux + real CoreCLR
>    binary. Что ВОЗМОЖНО slip past stub PAL и потом всплыть когда мы
>    портируем реально на bare metal SharpOS?

## Что мы хотим обратно от мудрецов

- **Прямые file:line references** в `gc-experiment/dotnet-runtime/`
- **Concrete shell commands** для PAL surface extraction
- **Расstavленные приоритеты**: must-have / nice-to-have / skip
- **Realistic spike scope** (1-2 weeks, not 6 months)
- **Anticipated tarpits** с recommendations

Format reply: per-query, structured (numbered list match'инг questions),
no fluff. Short = good.

## Что мы НЕ хотим

- Generic "read the PAL documentation" advice
- Tutorials about CoreCLR architecture (we know the high-level)
- Speculation о features we haven't asked about
- Re-iteration of plan.md content
