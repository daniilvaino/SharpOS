# SharpOS

SharpOS — это экспериментальная операционная система, которая строится как **полностью C#-проект** с управляемым развитием низкоуровневых компонентов.

[![SharpOS launcher](screenshot.png)](screenshot.png)

## Архитектурные инварианты

**Инвариант 1 — C# is the only source language.** В дереве исходников нет ни одного `.c`, `.cpp`, `.h`, `.asm` или `.s` файла. Ни одного. Все низкоуровневые операции — обработчики прерываний, spill callee-saved regs, runtime-bridges, write barriers, interface-dispatch trampolines — выражаются одним из трёх способов:

1. **C# intrinsics** (включая `[RuntimeExport]`, `[UnmanagedCallersOnly]`, `delegate* unmanaged`, `fixed`, unsafe pointer arithmetic).
2. **Shellcode-эмиссия из C#** — генерим машинный код в exec-stub buffer (аллокация через `AllocatePool(EfiLoaderCode)` для гарантированной исполнимости):
   - **Early-boot (compile-time codegen Iced assembler через `BootAsm.Generator`)** — Roslyn incremental source generator материализует kernel-shellcode стабы **на этапе сборки OS** из Iced api в pre-baked `ReadOnlySpan<byte>` template'ы в `.rdata`. На runtime — `Span.CopyTo` из template'а в exec-stub, плюс точечный патч qword'ов для managed-callback адресов через явно параметризованные дырки (`MovHole`, `JmpRelHole`, `DataSlotHole`, `PushImm32Hole`). Покрывает 16 стабов (interface dispatch, byref assign, IDT trampolines, EH funclets, GC stack spill, port I/O, etc) — весь early-boot тонкий слой, до того как managed GC/heap/threading доступны.
   - **Late-tier (runtime Iced assembler)** — после того как boot закончился и `KernelHeap`/`GcHeap`/managed exceptions работают, новый shellcode можно эмитить Iced прямо в runtime: `new Assembler(64); a.mov(rax, rcx); a.Assemble(writer, rip);`. Используется для динамически-параметризованного кода, после старта std. 
   - Граница: compile-time codegen Iced — пока ничего нельзя аллоцировать, runtime Iced — когда уже всё доступно.
3. **Build-time COFF data symbol emission через `CoffStub.Generator`** — когда MSVC-style линкер требует native data symbol (пример — `__security_cookie` для CRT-aware codegen) и ILC's `[RuntimeExport]` на static field его не эмиттит (исторический ILC gap), MSBuild Task сканит C# код Roslyn'ом, находит `[CoffDataSymbol(...)]` атрибут, материализует tiny `.obj` файл с native data symbol'ом и кидает его в `@(NativeLibrary)` перед link'ом. Из managed C#:
   ```csharp
   [BootAsm.CoffDataSymbol("__security_cookie", Section = ".data", Alignment = 8)]
   public static ulong SecurityCookie = 0x2B992DDFA232UL;
   ```
   Никаких `.c` файлов в дереве, никаких ручных compile-step'ов — pure C# source с атрибутом, всё остальное делает build pipeline. Native apps (`apps_native/`) собираются как freestanding win-x64 PE через тот же `CoffStub.Generator` (общий `apps_native/sdk/FreestandingPe.props`, `dotnet publish` без WSL); ELF-тулчейн выпилен (step137).

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

| Функционал | Kernel-AOT | PE-app | CoreCLR-hosted | Комментарий |
|---|---|---|---|---|
| `new T()` / managed heap | ✅ | ✅ | ✅ | |
| Collections (`List<T>`, `Dictionary<K,V>`, и т.д.) | ✅ | ✅ | ✅ | ( * - см ниже)|
| `string`, primitives, structs | ✅ | ✅ | ✅ | |
| `string.Format` / `StringBuilder.AppendFormat` | 🟡 | 🟡 | ✅ | частичное и слабое покрытие в std реализации |
| `lock` (`Monitor.Enter`/`Exit`) | 🔴 | 🔴 | ✅ | `System.Threading.Monitor` отсутствует в std/no-runtime |
| `System.Enum` | 🟡 | 🟡 | ✅ | ToString, Parse, GetNames не реализованы |
| `try` / `catch` / `finally` / `throw;` / `when`-filter | ✅ | ✅ | ✅ |  |
| HW-fault → managed exception (`#PF` → `NullReferenceException`) | ✅ | ✅ | ✅ | |
| `Exception.StackTrace` | ✅ | ✅ | 🟡 | в hosted CoreCLR `StackTrace` пустой для exception'ов брошенных из CLR-internal C++ EH path (`0xE06D7363 PEAVEEMessageException`); см. [`docs/coreclr-hosted-limits.md`](docs/coreclr-hosted-limits.md) §12 |
| Cctor — exception → `TypeInitializationException` wrapping | ✅ | ✅ | 🟡 | в hosted exception из cctor пробрасывается **raw** (не оборачивается в TIE); managed catch на конкретный тип сработает, но `catch (TypeInitializationException)` нет |
| Boxing / unboxing | ✅ | ✅ | ✅ | int/long/struct/Nullable<T>-as-underlying — все работают; `[BoxedEnumerator]` thunks для интерфейсных enumerator'ов на value-типах |
| `[ModuleInitializer]` | ✅ | ✅ | ✅ |  |
| `yield return` (Roslyn state machine) | ✅ | ✅ | ✅ | |
| `async/await` | ⏳ | ⏳ | ⏳ | |
| `Task.Run`, `Task.Delay` | ⏳ | ⏳ | ✅ | |
| `ThreadPool.QueueUserWorkItem` | ⏳ | ⏳ | ✅ | |
| Array covariance / `stelem.ref` | 🟡 | 🟡 | ✅ | в AOT `RhpStelemRef` **skipped все checks** (null/bounds/covariance) — wrong-type store даёт silent UB вместо `ArrayTypeMismatchException`. Монотипичный stelem работает корректно |
| Generic sharing (USG — `__Canon`) | ✅ | ✅ | ✅ |  |
| Virtual dispatch / interface dispatch (полный резолвер) | ✅ | ✅ | ✅ ||
| Write barrier (`RhpAssignRef`, `RhpStelemRef`) | ✅ (∅) | ✅ (∅) | ✅ | non-generational mark-sweep в AOT → barrier seman'тически no-op; контракт ILC соблюдён.  |
| `GC.Collect` / explicit collection | ✅ | ✅ | ✅ | full mark-sweep cycle; `GC.WaitForPendingFinalizers` зависает в hosted runtime (SYM-003 — finalizer thread не online) |
| Array.Copy overlap (memmove semantics) | ✅ | ✅ | ✅ | left + right shift с overlapping src/dst в одном массиве (`List<T>.RemoveAt`/`Insert` path) |
| `System.Collections.Concurrent.*`, `System.Collections.Immutable.*`, `SortedDictionary`, `SortedSet`, `BitArray`, `KeyedCollection`, `Array.BinarySearch`| 🔴 | 🔴 | ✅ | еще не реализовано, при этом известных блокеров - нет |
| `System.Text.RegularExpressions.Regex` | 🔴 | 🔴 | ✅ | нет имплементации|
| `ValueTuple<...>` / `DateTime` / `DateTimeOffset` | 🔴 | 🟡 | ✅ | отсутствуют в std/no-runtime; `Tuple<T1,T2>` + минимальный `TimeSpan` есть (step 141), `DateTime` в PE-аппах — стаб (`Now` = epoch, до RTC-сервиса) |
| LINQ extensions | ✅ | ✅ | ✅ | `System.Linq.Enumerable` mini-LINQ (step 134; в PE-аппах — step 141). Source — `List<T>` / итератор / string, **не** голый массив (см. [limits §4](docs/nativeaot-nostd-kernel-limits.md)) |
| **Managed delegates / lambаdas** | ✅ | ✅ | ✅ | завендорены из dotnet/runtime v8.0.27 (step 131; в PE-аппах — step 141); вырезано в `NotSupportedException`: reflection-поверхность, GVM, open-instance, variance-cast (limits §5) |
| **Reflection runtime metadata** | 🚫 | 🚫 | ✅ | AOT strips metadata |
| **`Reflection.Emit` / `Activator.CreateInstance(Type)`** | 🚫 | 🚫 | ✅ | требует JIT |
| **`dynamic` / DLR / `Expression<T>.Compile()`** | 🚫 | 🚫 | ✅ | DLR через Reflection.Emit |
| **`Type.GetType("Some.Class.Name")`** | 🚫 | 🚫 | ✅ | string→Type требует metadata |
| **Generic `as T` / `(T)x` с `where T : class`** | 🔴 | 🔴 | ✅ | AOT: generic RhTypeCast helper не вытянут в std (concrete варианты есть) |
| **Runtime x64 assembled (Iced lib)** | ✅ | 🚫 | 🚫 | пока что `NO_EVEX`, без managed-delegate путей; Guest tiers — by design, доступно после инициализации std |
| **Compile time x64 assembled (Iced lib)** | ✅ | 🚫 | 🚫 | пока что `NO_EVEX`, без managed-delegate путей; Guest tiers — by design |
| `System.Threading.Thread.Start()` | ✅ | ⏳ | ✅ | |
| `Interlocked.CompareExchange` (real atomic) | ✅ | 🟡 | ✅ | `System.Threading.Interlocked` это fake-stub из std (read-compare-write без `LOCK` prefix, корректно только для single-thread); ядро же зовёт `X64Asm.CmpXchg64` (real LOCK CMPXCHG) напрямую через `OS.Hal`. AppSDK не expose'ит kernel atomic primitives |
| Cooperative `Yield()` / `Sleep(ms)` | ✅ | ⏳ | ✅ | |
| `Event` / `Semaphore` / `Mutex` | ✅ | ⏳ | ✅ | |
| Multi-thread Process | ✅  | ⏳ | ✅  | |
| **`AssemblyLoadContext` (multiple ALCs)** | 🚫 | 🚫 | ⏳ | требует JIT |
| File I/O (read) | ✅ | ✅ | ⏳ | |
| File I/O (write) | 🔴 | 🔴 | 🔴 | RO-FAT32 |
| Network I/O | 🔴 | 🔴 | 🔴 | нет NIC driver |
| Console keyboard input | ✅ | ✅ | ⏳ | |
| **Direct hardware (CR3 / PCI / MMIO / IDT)** | ✅ | 🚫 | 🚫 | guest tiers — design boundary |
| AVX / AVX-512 | 🔴 | 🔴 | 🔴 | XCR0 заперт на x87\|SSE |
| `Math.Abs` (int/long/short/sbyte) | ✅ | ✅ | ✅ | integer-only в std/no-runtime |
| `Math.Sqrt` / `Math.Abs` (double, SSE intrinsics) | ✅ | ✅ | ✅ | |
| `Math.Sin` `Cos`  `Tan` `Atan` `Atan2` `Exp` `Log` `Log10` `Log2` `Pow` `Cbrt` `Sinh` `Cosh` `Tanh` `Asin` `Acos` (транцы) | 🔴 | 🔴 | 🟡 | **временные приближения** — в форке живут `lm_*` реализации через Taylor-ряды. Технически исполняется но не точно. Порт правильных алгоритмов (musl/glibc — Cody-Waite reduction + Remez полином) в kernel C# — в планах |
| `Math.Floor` / `Math.Ceiling` / `Math.Truncate` / `Math.Round` | 🔴 | 🔴 | ✅ | Kernel-AOT: не объявлены. Hosted: битовые операции над IEEE 754 (не транценденты) — работают точно |
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
| Strong-fallback аудит `SharpOSHost_*` | Fork/PAL | ⏳ R1 / D10-D11 | fallback'и в той же TU обязаны быть `weak`, иначе Release clang-fold подменяет до линковки |
| IST / emergency fault stacks (#PF/#DF/NMI) | Kernel | 🔴 R2 | stack overflow → silent triple-fault; panic path должен не аллоцировать |
| FH4 catch-object construction (`dispCatchObj` / copy-ctor) | Fork EH | ⏳ | паритет с FH3 (тоже без него); вся EH-батарея зелёная без него, но `catch(Exception&)` by-value не построится |
| `CultureInfo.GetCultureInfo("ru-RU")` non-invariant | CoreCLR-hosted | ⏳ | runtimeconfig прибит к `InvariantGlobalization=true`; ICU/icudt.dat не пакуется, `System.Globalization.Native` PAL не реализован. Не архитектурный запрет — отложено до конкретной потребности |
| Self-modifying shellcode без cpuid-serializer | Kernel | ⏳ | патчеры пишут template из `.rdata` (через `BootAsm.Generator`) и сразу зовут без cpuid serializing; QEMU forgiving, реальное железо может выполнить stale prefetch |
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

## Отдельное спасибо

Проекты на плечах которых воздвинут SharpOS. Перечислены по убыванию вклада:

- **[zerosharp](https://github.com/MichalStrehovsky/zerosharp)** (Michal Strehovský, MIT) — стартовый baseline: UEFI hello-world на NativeAOT, с которого SharpOS стартовал.
- **[dotnet/runtime](https://github.com/dotnet/runtime) + [runtimelab](https://github.com/dotnet/runtimelab)** (Microsoft, MIT) — NativeAOT toolchain (форк в `dotnet-runtime-sharpos/`) + сотни BCL-портов в наш std (`List<T>`, `Dictionary<K,V>`, `String.Format`, `Array.Sort`, introsort, ожидания компилятора, байтовый алайнинг, и т.д.).
- **[Iced](https://github.com/icedland/iced)** (icedland, MIT) — x86/x64 encoder. Используется в двух режимах: (1) `BootAsm.Generator`-ом для compile-time codegen kernel-шеллкодов на этапе сборки, (2) baked-in в kernel image для runtime fluent-API shellcode emission после того как boot закончился.
- **[ManagedDotnetGC](https://github.com/kevingosse/ManagedDotnetGC)** (Kevin Gosse, MIT) — mark/sweep референс для GC.
- **[MOOS](https://github.com/nifanfa/MOOS)** (nifanfa, Unlicense / public domain) — драйверы `AHCI`, `Disk`, `PCI(Express)`.
- **[Font 8x8](https://github.com/dhepper/font8x8)** (Daniel Hepper, на основе Marcel Sondaar / IBM VGA, Public Domain) — глифы консоли framebuffer.
- **[DiscUtils](https://github.com/DiscUtils/DiscUtils)** (Kenneth Bell, MIT) — структура FAT/GPT — FAT-референс.
- **[ChaN FatFs](https://elm-chan.org/fsw/ff/)** (BSD-1-clause) — второй FAT-референс.
- **[UpsilonGC](https://github.com/kkokosa/UpsilonGC)** (Konrad Kokosa, GPL-3) — референс по custom GC под .NET.
- **[Cosmos](https://github.com/CosmosOS/Cosmos)** (BSD-3) — концептуальный референс managed-OS подхода (stack-only conservative scan inspiration).

## Лицензия

[CC0 1.0 Universal](LICENSE) — общественное достояние. Используй, изменяй и распространяй в любых целях, в том числе коммерческих.
