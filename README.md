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
| `new T()` / managed heap | ✅ | ✅ | ✅ | ELF делит heap с kernel; CoreCLR — через PAL |
| `string`, primitives, structs | ✅ | ✅ | ✅ | |
| `try` / `catch` / `finally` / `throw;` / `when`-filter | ✅ | ✅ | ✅ | ELF: через kernel EH |
| HW-fault → managed exception (`#PF` → `NullReferenceException`) | ✅ | ✅ | ✅ | gate L13 |
| `Exception.StackTrace` | ✅ | ✅ | ✅ | gate L14 |
| Collections (`List<T>`, `Dictionary<K,V>`, и т.д.) | ✅ | ✅ | ✅ | kernel/ELF — ported из dotnet/runtime; CoreCLR — full BCL |
| LINQ extensions | 🟡 | 🟡 | ✅ | AOT-tier: partial modulo iterator features |
| `yield return` (Roslyn state machine) | ✅ | ✅ | ✅ | |
| `async/await` | ⏳ E11 | ⏳ E11 | ⏳ E11 | |
| **Reflection runtime metadata** | 🚫 | 🚫 | ✅ | AOT strips metadata at build; нет рантаймного walker'а |
| **`Reflection.Emit` / `Activator.CreateInstance(Type)`** | 🚫 | 🚫 | ✅ | требует JIT — NativeAOT по определению без JIT |
| **`dynamic` keyword / DLR / `Expression<T>.Compile()`** | 🚫 | 🚫 | ✅ | DLR строит call sites через Reflection.Emit |
| **`Type.GetType("Some.Class.Name")`** | 🚫 | 🚫 | ✅ | string→Type требует metadata table |
| **Generic `as T` / `(T)x` с `where T : class`** | 🚫 LNK2001 | 🚫 LNK2001 | 🚫 PAL-STUB/SEH | kernel/ELF: std/no-runtime реализует только конкретный `RhTypeCast_IsInstanceOfClass`; generic `RhTypeCast_IsInstanceOf`/`CheckCast` нет → LNK2001. **Hosted: тоже падает** — CoreCLR JIT-helper для generic cast (вероятно `CORINFO_HELP_ISINSTANCEOFANY` / `CHKCASTANY`) у нас тоже не вытянут / выкинут. Workaround единый: `if (target is not Foo f) ...` |
| `System.Threading.Thread.Start()` | ✅ `Scheduler.Spawn` | ⏳ E8 (deferred) | ✅ E9.a | step 98: CREATE_SUSPENDED + ResumeThread + per-thread TEB |
| `Interlocked.CompareExchange` (real atomic) | ✅ через `X64Asm.CmpXchg64` | 🟡 single-thread stub | ✅ | |
| Cooperative `Yield()` / `Sleep(ms)` | ✅ | ⏳ E8 | ✅ E9.a | через SharpOSHost_Sleep / SwitchToThread |
| `Event` / `Semaphore` / `Mutex` | ✅ | ⏳ E8 | ✅ E9.b | step 100: HandleTable-routing CreateEventW/CreateSemaphoreEx/CreateMutex/Set/Reset/Release; реентрантный Win32Mutex с abandoned detection |
| Multi-thread Process | ✅ logical | ⏳ E8 | ✅ logical E9.a | no MMU isolation в любой phase |
| `Task.Run` / `Task.Delay` | ⏳ E11 | ⏳ E11 | ⏳ E11 | |
| `ThreadPool.QueueUserWorkItem` | ⏳ E10 | ⏳ E10 | ⏳ E10 | |
| **`AssemblyLoadContext` (multiple ALCs)** | 🚫 | 🚫 | ⏳ E12 | AOT: динамическая загрузка assembly = JIT, чего нет |
| File I/O (read) | ✅ FAT direct | ✅ через AppSDK | ⏳ через PAL | |
| File I/O (write) | 🔴 | 🔴 | 🔴 | RO-FAT32; нужен RW driver |
| Network I/O | 🔴 | 🔴 | 🔴 | нет NIC driver (RTL8139 / e1000 / virtio-net) |
| Console keyboard input | ✅ PS/2 driver | ✅ `TryReadKey` | ⏳ через PAL | |
| **Direct hardware (CR3 / PCI / MMIO / IDT)** | ✅ | 🚫 | 🚫 | ELF/CoreCLR — guest tiers; HW exposure ломает design boundary |
| AVX / AVX-512 | 🔴 | 🔴 | 🔴 | XCR0 заперт на x87\|SSE в Phase E (FXSAVE-only ctx switch); ослабим если понадобится |
| `Math.Sqrt` / `Math.Abs` (SSE intrinsics) | ✅ | ✅ | ✅ | |
| `Math.Pow` / `Math.Sin` / `Math.Log` (libm transcendentals) | 🔴 | 🔴 | 🔴 | trap-stub; нужен libm port |
| GC (mark-sweep, conservative stack scan) | ✅ | ✅ | 🟡 | CoreCLR использует свой GC через PAL |
| Process exit code propagation | ✅ | ✅ через `TryRunApp` | ⏳ | |
| **Per-process MMU isolation** | 🚫 | 🚫 | 🚫 | unikernel design ([§10](docs/threading-architecture.md) "logical, no MMU isolation") |
| **Parallel execution at same VA** | 🚫 | 🚫 | 🚫 | следствие выше; для ELF — relink через PIE даёт different-VA concurrency |
| Preemptive scheduling | ⏳ E13+ | ⏳ E13+ | ⏳ E13+ | IRQ-driven HPET wake + IST stack |
| SMP / multi-core | ⏳ E13+ | ⏳ E13+ | ⏳ E13+ | AP startup + per-CPU TEB + memory barriers |

**Текущий phase прогресса по threading:** E1..E7 closed (cooperative threads / Sleep / Event / Semaphore / Process abstraction). Дальше — E9 (CoreCLR PAL routing для managed threading), потом E10-E13 (ThreadPool / Task / ALC / final audit).

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
