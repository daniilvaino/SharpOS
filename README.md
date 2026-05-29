# SharpOS

SharpOS — это экспериментальная операционная система, которая строится как **полностью C#-проект** с управляемым развитием низкоуровневых компонентов.

[![SharpOS launcher](screenshot.png)](screenshot.png)

## Архитектурные инварианты

**Инвариант 1 — C# is the only source language.** В дереве исходников нет ни одного `.c`, `.cpp`, `.h`, `.asm` или `.s` файла. Ни одного. Все низкоуровневые операции — обработчики прерываний, spill callee-saved regs, runtime-bridges, write barriers, interface-dispatch trampolines — выражаются одним из трёх способов:

1. **C# intrinsics** (включая `[RuntimeExport]`, `[UnmanagedCallersOnly]`, `delegate* unmanaged`, `fixed`, unsafe pointer arithmetic).
2. **Byte-array shellcode**, эмитится C# кодом в runtime в exec-stub buffer (allocated через `AllocatePool(EfiLoaderCode)` для гарантированной исполнимости). Пример — `OS/src/Kernel/Memory/InterfaceDispatchBridge.cs`, где 195-байтный шеллкод для interface dispatch рождается byte-по-byte в C#.
3. **Build-time codegen через PowerShell**, когда MSVC-style линкер требует символов от C-ABI (security cookie и подобное), мы генерим крохотный `.c` внутри build script, компилим, подхватываем — но в репо его нет.

Любая новая low-level задача должна решаться одним из этих трёх механизмов. Если задача кажется нерешаемой — задача сформулирована неправильно. Примеры из реальной работы: managed GC stack-spill, CR3 read/write, CPU cookie, interface dispatch с shared-generic resolver, NativeAOT module init без линкерных сентинелов `__modules_a..__modules_z` — всё это решено в рамках инварианта.

Насколько нам известно, **других OS-проектов с этим инвариантом не существует**.

**Инвариант 2 — Naming discipline.** SharpOS **не переиспользует канонические .NET namespaces и имена типов** если реализация не полностью совместима с публичным контрактом BCL (modulo ограничения, задокументированные в [docs/nativeaot-nostd-kernel-limits.md](docs/nativeaot-nostd-kernel-limits.md)). Частичные / нестандартные реализации живут в SharpOS-specific namespace-ах (`SharpOS.Std.*`, `OS.Kernel.*` и т.д.), полноценные BCL-compat — в `System.*` и `System.Collections.Generic.*` с оригинальными именами. Это правило позволяет в перспективе таскать LINQ и прочий BCL-код из dotnet/runtime целиком как есть.

## Миссия

Сделать ОС, где:

- весь ключевой код ядра, загрузки и пользовательского окружения пишется на C#;
- сборка выполняется через `dotnet`-инструменты;
- архитектура остаётся freestanding и контролируемой, без зависимости от “обычного” desktop/runtime стека;
- по мере развития расширяется собственный std/runtime слой под нужды ОС.

## Стратегическая цель

Долгосрочная цель проекта — пройти путь от минимального freestanding C#-ядра к запуску **полноценного .NET-окружения на SharpOS**.

Практически это означает:

1. Построить устойчивый low-level фундамент (boot, memory, paging, process/app ABI, diagnostics).
2. Развивать собственные системные библиотеки и строково/utility/runtime-подсистемы.
3. Поднять pipeline внешних приложений на C#.
4. Дойти до состояния, где SharpOS способен хостить полноценный .NET runtime.

## Поверхности исполнения (three execution tiers)

В SharpOS code исполняется на **трёх различных tier'ах**, каждый со своими ограничениями. Live-журнал каждого — в `docs/`:

| Tier | Что | Где | Toolchain | Подробно |
|---|---|---|---|---|
| **Kernel-AOT** | Само ядро + boot + drivers + scheduler | `OS/` | NativeAOT + NoStdLib + наш MinimalRuntime | [`docs/nativeaot-nostd-kernel-limits.md`](docs/nativeaot-nostd-kernel-limits.md) |
| **ELF-app (AOT)** | Пользовательские apps через AppService | `apps/` (`HELLO.ELF`, `FETCH.ELF`, и т.д.) | NativeAOT + NoStdLib + `apps/sdk/AppHost.cs` API | [`docs/nativeaot-nostd-elf-limits.md`](docs/nativeaot-nostd-elf-limits.md) |
| **CoreCLR-hosted** | Стоковые .NET DLL байт-в-байт | `\sharpos\*.dll` в FAT | Форк CoreCLR (`dotnet-runtime-sharpos`), статически слинкован в kernel | [`docs/coreclr-hosted-limits.md`](docs/coreclr-hosted-limits.md) |

### Легенда

- ✅ — работает, доказано прогоном (см. probe в [`OS/src/Kernel/Diagnostics/`](OS/src/Kernel/Diagnostics/) или гейт в [`tools/probe_report.ps1`](tools/probe_report.ps1)).
- 🟡 — частично / через ограниченный API.
- ⏳ — отложено к указанной phase (см. [`docs/threading-architecture.md`](docs/threading-architecture.md) §15).
- 🔴 — пока что отсутствует / временно не работает (код не написан или сломан, но архитектурно достижимо).
- 🚫 — архитектурно невозможно (ограничение by design либо не применимо к данной подсистеме).

### Компаративная таблица фичей

| Функционал | Kernel-AOT | ELF-app | CoreCLR-hosted | Комментарий |
|---|---|---|---|---|
| `new T()` / managed heap | ✅ | ✅ | ✅ | |
| `string`, primitives, structs | ✅ | ✅ | ✅ | |
| `try` / `catch` / `finally` / `throw;` / `when`-filter | ✅ | ✅ | ✅ | |
| HW-fault → managed exception (`#PF` → `NullReferenceException`) | ✅ | ✅ | ✅ | |
| `Exception.StackTrace` | ✅ | ✅ | ✅ | |
| Collections (`List<T>`, `Dictionary<K,V>`, и т.д.) | ✅ | ✅ | ✅ | |
| LINQ extensions | 🟡 | 🟡 | ✅ | |
| `yield return` (Roslyn state machine) | ✅ | ✅ | ✅ | |
| `async/await` | ⏳ | ⏳ | ⏳ | |
| **Reflection runtime metadata** | 🚫 | 🚫 | ✅ | AOT strips metadata |
| **`Reflection.Emit` / `Activator.CreateInstance(Type)`** | 🚫 | 🚫 | ✅ | требует JIT |
| **`dynamic` / DLR / `Expression<T>.Compile()`** | 🚫 | 🚫 | ✅ | DLR через Reflection.Emit |
| **`Type.GetType("Some.Class.Name")`** | 🚫 | 🚫 | ✅ | string→Type требует metadata |
| **Generic `as T` / `(T)x` с `where T : class`** | 🔴 | 🔴 | ✅ | AOT: generic RhTypeCast helper не вытянут в std (concrete варианты есть) |
| `System.Threading.Thread.Start()` | ✅ | ⏳ | ✅ | |
| `Interlocked.CompareExchange` (real atomic) | ✅ | 🟡 | ✅ | |
| Cooperative `Yield()` / `Sleep(ms)` | ✅ | ⏳ | ✅ | |
| `Event` / `Semaphore` / `Mutex` | ✅ | ⏳ | ✅ | |
| Multi-thread Process | ✅ logical | ⏳ | ✅ logical | |
| `Task.Run` | ⏳ | ⏳ | ✅ | |
| `Task.Delay` | ⏳ | ⏳ | ✅ | step111 (HPET QPC + wfmo n=1 forwarder) + step112 (`GetThreadIOPendingFlag` stub для shutdown) |
| `ThreadPool.QueueUserWorkItem` | ⏳ | ⏳ | ✅ | |
| **`AssemblyLoadContext` (multiple ALCs)** | 🚫 | 🚫 | ⏳ | требует JIT |
| File I/O (read) | ✅ | ✅ | ⏳ | |
| File I/O (write) | 🔴 | 🔴 | 🔴 | RO-FAT32 |
| Network I/O | 🔴 | 🔴 | 🔴 | нет NIC driver |
| Console keyboard input | ✅ | ✅ | ⏳ | |
| **Direct hardware (CR3 / PCI / MMIO / IDT)** | ✅ | 🚫 | 🚫 | guest tiers — design boundary |
| AVX / AVX-512 | 🔴 | 🔴 | 🔴 | XCR0 заперт на x87\|SSE |
| `Math.Sqrt` / `Math.Abs` (SSE intrinsics) | ✅ | ✅ | ✅ | |
| `Math.Pow` / `Math.Sin` / `Math.Log` (libm) | 🔴 | 🔴 | 🔴 | trap-stub |
| GC (mark-sweep, precise stack scan) | ✅ | ✅ | ✅ | hosted — свой GC через PAL |
| Process exit code propagation | ✅ | ✅ | ⏳ | |
| **Per-process MMU isolation** | 🚫 | 🚫 | 🚫 | unikernel design |
| **Parallel execution at same VA** | 🚫 | 🚫 | 🟡 | single ALC (threads) ✅; multi-ALC ⏳ E12 |
| Preemptive scheduling | ⏳ | ⏳ | ⏳ | IRQ-driven HPET wake |
| SMP / multi-core | ⏳ E13+ | ⏳ E13+ | ⏳ E13+ | AP startup + per-CPU TEB + memory barriers |

### Известные проблемы и временные ограничения

Не таблица фич, а сводный реестр того что **сломано / висит / ждёт hardening**. Не дублирует основную таблицу выше. Подробности — в [`docs/coreclr-hosted-limits.md`](docs/coreclr-hosted-limits.md), [`docs/open-symptoms.md`](docs/open-symptoms.md), активные риски R1-R5 в [`plan.md`](plan.md).

| Проблема | Tier | Статус | Источник / комментарий |
|---|---|---|---|
| `GC.WaitForPendingFinalizers` зависает | CoreCLR-hosted | 🔴 hang | SYM-003: finalizer-thread completion event не wired; `GC.Collect` сам работает |
| `DateTime.Now` (local timezone) | все | 🔴 | нет tz DB; `DateTime.UtcNow` через CMOS+HPET ✅ |
| `Process.Start` | CoreCLR-hosted | 🔴 | `SystemNative_RegisterForSigChld` отсутствует |
| `GZipStream` / `System.IO.Compression` | все | 🔴 | `libSystem.IO.Compression.Native` отсутствует |
| Hosted GC suspend/resume cooperation | CoreCLR-hosted | ⏳ R4 | cooperative safepoints + RetainVM/decommit policy не production-complete |
| Strong-fallback аудит `SharpOSHost_*` | Fork/PAL | ⏳ R1 / D10-D11 | step114 урок: fallback'и в той же TU обязаны быть `weak`, иначе Release clang-fold подменяет до линковки |
| IST / emergency fault stacks (#PF/#DF/NMI) | Kernel | 🔴 R2 | stack overflow → silent triple-fault (step113 урок); panic path должен не аллоцировать |
| FH4 catch-object construction (`dispCatchObj` / copy-ctor) | Fork EH | ⏳ | паритет с FH3 (тоже без него); вся EH-батарея зелёная без него, но `catch(Exception&)` by-value не построится |
| `CultureInfo.GetCultureInfo("ru-RU")` non-invariant | CoreCLR-hosted | ⏳ | runtimeconfig прибит к `InvariantGlobalization=true`; ICU/icudt.dat не пакуется, `System.Globalization.Native` PAL не реализован. Не архитектурный запрет — отложено до конкретной потребности |
| Self-modifying shellcode без cpuid-serializer | Kernel | ⏳ | патчеры (ByRefAssignRef, InterfaceDispatch, BootStackSwitch, EH funclets) пишут байты и сразу зовут; QEMU forgiving, реальное железо может выполнить stale prefetch |
| AOT хойстит non-volatile MMIO-poll | Kernel | ⚠️ контракт | ILC LICM выносит MMIO-чтение из spin-петли (compile-time); все HW-poll **обязаны** идти через `NoInlining` Rd-барьер или `volatile` |

**Легенда**: 🔴 — известно сломано, ⚠️ — действующий контракт/ограничение, ⏳ — отложено / в работе.

**Текущий roadmap:** актуальный единый план ведётся в [`plan.md`](plan.md). Состояние на 2026-05-29: Release CoreCLR-hosted green на QEMU+VirtualBox, Phase D закрыта, Phase E частично/сильно закрыта (threads/ThreadPool/Task/Timer green), post-EBS substrate green. Дальше — D10/D11 hardening, IST для #PF/#DF, затем hosted-GC/finalizers или Roslyn resolver+IO.

## Принципы проекта

- `dotnet-first`: всё, что возможно, должно собираться стандартным .NET toolchain.
- `C#-first`: новые подсистемы приоритетно реализуются на C#.
- `contracts first`: сначала API/ABI и границы слоёв, затем расширение функциональности.
- `incremental bring-up`: маленькие проверяемые шаги вместо больших “переписать всё сразу”.

## Контуры Репозитория

- `OS/src/Boot|Hal|Kernel|TestApp` — код операционной системы и слои ядра.
- `apps/sdk` — ABI/SDK для внешних приложений SharpOS.
- `std/` — отдельный контур разработки no-runtime std/runtime компонентов:
  - `std/no-runtime/` — общий слой для замены частей отсутствующей стандартной библиотеки.

Правило: всё, что относится к эволюции std/runtime, развивается в `std/`, а не в слоях ОС.

**Активная цель:** расширять `std/no-runtime/` и постепенно переводить unsafe-код в managed C#. Каждая новая строковая/утилитная операция — сначала в `std/`, затем используется из ядра и SDK. `unsafe` остаётся только на ABI-границах и там, где прямой доступ к железу неизбежен.

## Что важно

Этот репозиторий фиксирует направление проекта и целевую архитектурную траекторию.  
Текущий прогресс по этапам ведётся отдельно в папке `done/`.

## Лицензия

[CC0 1.0 Universal](LICENSE) — общественное достояние. Используй, изменяй и распространяй в любых целях, в том числе коммерческих.
