# План развития SharpOS

Документ фиксирует стратегическое направление проекта: путь от текущего freestanding C#-ядра с минимальным std к OS которая хостит CoreCLR, Roslyn и PowerShell как обычные .NET приложения.

Это **план фаз**, а не список конкретных todo. Каждая фаза — законченный блок работы с критерием готовности. Разбиение на конкретные подзадачи делается внутри самой фазы по мере к ней подхода.

---

## Архитектура исполнения — три tier'а

В SharpOS два слоя приложений могут существовать одновременно:

- **Kernel-tier** — наш kernel + std + PAL. AOT-compiled C#, ring 0, использует наш минимальный std.
- **Native-tier** — AOT-compiled C# user-mode apps (как существующий `HelloSharpFs`). На устройстве IL нет, только нативный код. Использует наш std.
- **Hosted-tier** — IL bytes на устройстве, исполняется через CoreCLR (JIT). Использует настоящую `System.Private.CoreLib`. Roslyn/PowerShell живут здесь.

Три слоя не конкурируют — у каждого своя ниша. Системные утилиты пишутся как native-tier (быстро, без runtime overhead). Динамические workload'ы (Roslyn REPL, PowerShell с reflection) — hosted-tier.

---

## Архитектурные инварианты

Жёсткие правила проекта. Любая новая фаза или подзадача должна быть сформулирована и решена в их рамках.

### Инвариант 1 — C# is the only source language

В дереве исходников **не появляется** ни одного `.c`, `.cpp`, `.h`, `.asm`, `.s` файла. Каждая новая low-level задача решается одним из трёх механизмов:

1. **C# intrinsics** — `[RuntimeExport]`, `[UnmanagedCallersOnly]`, `delegate* unmanaged`, `fixed`, unsafe pointer math, `Internal.Runtime.CompilerHelpers`-стиль экспортов.
2. **Byte-array shellcode** — байтовый emitter на C# пишет инструкции в exec-stub buffer (`EfiLoaderCode` allocation от UEFI). Примеры: `InterfaceDispatchBridge`, `GcStackSpill`, `Cr3Accessor`, `JumpStub`, `ByRefAssignRefPatcher`.
3. **Build-time codegen в PowerShell build scripts** — если MSVC-линкер требует C-ABI символа (например `__security_cookie`), генерим `.c` ephemerally внутри `build_*.ps1`, компилим, подхватываем, коммитим **НЕ** в репо.

Если проблема кажется требующей asm/C файла — значит либо выбран не тот механизм из трёх, либо задача сформулирована так, что её невозможно решить правильно. Обе ситуации решаются пересмотром подхода, а не добавлением нового файла.

**Особое исключение:** если будем форкать CoreCLR/Mono (Phase 6) — он живёт как **внешний submodule с патчами**. Не коммитим C++ runtime в основной репо, патчим upstream и линкуем как third-party. Наш PAL пишется на C# через `[UnmanagedCallersOnly]`, граница чистая.

### Инвариант 2 — Naming discipline

SharpOS не переиспользует канонические .NET namespaces / type names **если реализация не полностью совместима с публичным контрактом** (modulo задокументированные ограничения в `docs/nativeaot-nostdlib-limits.md`).

Правило:
- `System.*`, `System.Collections.Generic.*`, `System.Collections.ObjectModel.*` и прочие канонические namespace — **только для BCL-compat реализаций** с совпадающей сигнатурой, поведением и (где разумно) внутренней структурой.
- Частичные / экспериментальные / platform-specific типы — в `SharpOS.Std.*`, `OS.Kernel.*`, `OS.Boot.*`, `OS.Hal.*`.

Цель: LINQ / System.Text.Json / System.Xml / прочий BCL-код должен собираться поверх нашего std **без source-level правок**. Каждый раз когда добавляем тип в `System.*`, отвечаем на вопрос: «можно ли реально взять BCL-код который этот тип использует и собрать его у нас?».

---

## Что сделано к моменту написания этого плана

Закрыто (steps 28-34, см. `done/stepNN.md`):

- **NumberFormatting + char helpers + string queries + string transforms** в `std/no-runtime/shared/`.
- **Managed mark-sweep GC** с conservative stack scan (steps 30-31).
- **Базовые BCL коллекции** — List/Dict/Stack/Queue/HashSet/LinkedList/SortedList/ROCollection/RODict (steps 30, 31, 33).
- **Shared-generic interface dispatch** end-to-end (step 32).
- **StringBuilder + Concat/Split/Join + BCL runtime fundament** (Span/Unsafe/MemoryExtensions/Buffer, step 34).

Всё это — kernel-tier и native-tier инфраструктура. Hosted-tier ещё не начат.

---

## Активные фазы

### Phase 0 — IDT + BCL base

**Critical-path шаг: IDT first.** До любой другой работы Phase 0 — без IDT любой баг даёт triple-fault и ребут, после IDT — читаемый panic с RIP/CR2/registers.

- **IDT + signal-dispatch** (256-entry interrupt descriptor table, обработчики `#PF/#GP/#UD/#DE/#DF/#NM/#TS/#NP/#SS`, GDT/TSS+IST для double-fault, диагностический dump RIP/CR2/registers; MSI vectors 0x40-0xFE зарезервированы; архитектурно сразу под PAL signal-API).

- **BCL base — расширение std/no-runtime до стабильного surface'а:**
  - MemoryExtensions (IndexOf, SequenceEqual, и т.д.)
  - Math для float/double
  - Array методы (Sort, Find, BinarySearch)
  - String/StringBuilder остальные методы
  - IntPtr arithmetic
  - Debug реальные методы (Assert, WriteLine с реальной диагностикой)

**Критерий готовности:** `int* p = null; *p = 42;` даёт читаемый panic, не reboot. BCL surface достаточен чтобы portable BCL-код (например LINQ Where/Select когда придёт) собирался без правок.

### Phase 1 — kernel exception handling + platform infrastructure

- **Полноценное managed exception handling** (НЕ урезано до longjmp): personality function для NativeAOT/Itanium ABI, stack unwinding через `.eh_frame` / `.pdata`, `System.Exception` базовый класс с Message/StackTrace/InnerException, производные типы, finally блоки. Используется нашим kernel/std/native-tier кодом. **Самый рисковый пункт плана** — может занять 2-6 месяцев. Если упрёмся — fallback на longjmp-only milestone.
- **ClassConstructorRunner портирование** (полноценный путь cctor для static reference fields; разблокирует `string.Empty` как field, lazy static patterns; дропаем `--resilient` режим, появятся настоящие compile errors на missing helpers — каждый чиним).
- **ACPI parsing** (RSDP discovery → RSDT/XSDT walk → MADT для APIC topology, HPET table для timer base, MCFG для PCIe ECAM; обязательный фундамент для всего hardware-aware кода).
- **RTC + HPET/TSC для timekeeping** (RTC через CMOS ports для wall-clock, HPET memory-mapped через адрес из ACPI HPET table, TSC через `RDTSC` для high-resolution; `Stopwatch` API).

**Критерий готовности:** `try { throw new InvalidOperationException("test"); } catch (Exception e) { Console.WriteLine(e.Message); }` ловит exception и логирует. ACPI таблицы парсятся, APIC/HPET адреса найдены, `Stopwatch` показывает корректное время.

### Phase 2 — PAL разведка и дизайн

- **PAL design — каталог требуемых функций** (разбор `src/coreclr/pal/inc/*.h`, выделение minimal subset который CoreCLR реально дёргает, POSIX-shape; декомпозиция по областям: memory / threading / sync / file I/O / time / TLS / signals / executable memory для JIT; финал — спека с сигнатурами и semantics на каждую функцию).
- **De-risk spike на Linux host'е** (1-2 weeks эксперимент: подменить системный PAL на наши stubs прямо на Linux, прогнать managed Hello World **с JIT-компиляцией**; валидирует что архитектура CoreCLR-fork в принципе работает до того как полгода потратить на real PAL).

**Критерий готовности:** есть письменная спека PAL surface'а; spike на Linux показывает что наш stub-PAL принимает CoreCLR call'ы и managed Hello World отрабатывает.

### Phase 3 — managed runtime infrastructure (single-core)

- **Scheduler** (Thread struct с регистрами + FXSAVE state, per-thread stack с guard page, context-switch routine как shellcode-эмиттер, ready queue, Local APIC timer для preemption, atomic primitives через CPU instructions).
- **Atomic operations as managed API** (`System.Threading.Interlocked` методы, `Volatile.Read/Write`, `Thread.MemoryBarrier`; всё через CPU intrinsics, ILC компилит в `LOCK CMPXCHG` / `XADD` / `MFENCE`).
- **BCL: System.Threading.Thread + Monitor + lock + sync primitives** (`Thread.Start/Join/Sleep`, `Monitor.Enter/Exit` под `lock` keyword, ManualResetEvent, AutoResetEvent, Semaphore, ReaderWriterLockSlim; портируем 1:1 из dotnet/runtime).
- **BCL: Task + async/await** (полная state-machine инфраструктура — `Task<T>`, `TaskCompletionSource`, `AsyncTaskMethodBuilder`, `IAsyncStateMachine`, `INotifyCompletion`/`ICriticalNotifyCompletion`, `TaskAwaiter`, `SynchronizationContext`, `TaskScheduler`, `ThreadPool`; ~2-3k строк портированного BCL; риск Roslyn-специфических требований по сигнатурам типов как было с yield return).

**Критерий готовности:** `Task.Run(() => {...})` работает. `await Task.Delay(100)` работает. `lock(obj) {...}` работает. Два потока через `Thread.Start` корректно time-slice'ятся под APIC timer.

### Phase 3.5 — SMP / multi-core (опциональная)

Не блокирует Roslyn/PS. Решать **после** того как single-core threading стабильно работает и видны реальные bottleneck'и. +3-6 месяцев focused работы.

- AP startup (через INIT-SIPI sequence из ACPI MADT)
- Per-CPU storage
- IPI infrastructure (через APIC)
- Lock-free updates где нужно
- Multi-core scheduler

**Критерий готовности:** N потоков реально параллелятся на M ядер с linear speedup на CPU-bound workload'е.

### Phase 3.7 — Native-tier StackInterpreter (integration milestone)

Промежуточный proof-of-life **до** того как CoreCLR пайплайн начнёт строиться. Доказывает что весь kernel-tier стек (Phases 0-3) работает интегрированно на реальном workload'е, а не только на probe'ах.

- **RPN calculator или mini-Forth** на нашем std/.
- Использует Thread/Task/exceptions из Phase 1+3.
- **Mini interactive console** — readline-like input с line buffering, history, basic editing; output через UEFI Console (драйверы ещё не на этом этапе). Это **не финальная PS shell**, отдельный простой компонент. Переиспользуется потом в Phase 7 для Roslyn REPL.

**Критерий готовности:** запускается StackInterpreter.elf через launcher, принимает выражения из mini-console, корректно считает, exceptions при bad input ловятся. Hours-long uptime без deadlock'ов и memory leaks.

### Phase 4 — UEFI инкапсуляция (без вызова ExitBootServices)

Архитектурный refactor, не функциональная задача. Готовит почву к Phase 5+.

- Архитектурный рефакторинг — все UEFI calls за интерфейс (`IPlatformConsole`, `IPlatformFileSystem`, `IPlatformKeyboard`, `IPlatformTimer`).
- Snapshot всего что нужно после ExitBootServices (serial port адрес, framebuffer pointer + dimensions, ACPI tables копия, memory map копия).
- Реализовать "UefiServicesGone" режим — заглушки на UEFI calls с диагностикой "вызвано после ExitBootServices".
- Возможность переключения режимов по команде / build flag (для тестирования).
- ExitBootServices физический вызов — опционально, по желанию когда драйверы готовы.

**Критерий готовности:** существующая функциональность работает идентично через interface'ы. Build-flag переключает между UEFI-active и UEFI-gone режимами; в UEFI-gone все service-calls дают controlled errors.

### Phase 5 — Drivers

Track независимый от Phase 6/7 — может вестись параллельно. Все драйверы пишутся подходом **1:1 port из reference impl + unit tests**: берём готовый код из открытого проекта (BSD/MIT лицензия), портируем на C# вербатим, тесты переносим вместе с кодом.

- **PCI bus enumeration** (config space через MCFG ECAM из ACPI или legacy 0xCF8/0xCFC; vendor:device discovery, BAR mapping, MSI/MSI-X setup; необходим перед любым device driver).
- **Display: PSF-font renderer на GOP framebuffer** (выход из UEFI Console, рендер glyph'ов из PSF font на raw framebuffer, `ExitBootServices`-safe; **pilot для 1:1-port подхода** — 150 строк C из reference impl, render-hash unit tests против эталонных bitmap'ов).
- **Keyboard: PS/2 driver** (legacy IO ports 0x60/0x64, scancode → KeyEvent translator; OSDev wiki как reference, port-IO mock + sequence tests на host'е; USB HID откладываем максимально долго).
- **Storage: virtio-blk + FAT32 reader** (virtio-blk driver для QEMU из virtio-spec, FAT32 через FatFs port — embedded FAT lib MIT-лицензия со своим тестовым корпусом; AHCI для real SATA откладываем).
- **ExitBootServices transition** (memory-map snapshot, переход от UEFI services на свои драйверы; работает только когда display + keyboard + storage готовы; после — мы настоящая standalone OS, не "большой UEFI app").
- **Network stage 1: Ethernet/ARP/IP/UDP/DHCP** через lwIP port (MIT, ~30k LOC, со своим test suite). Goal: DHCP даёт IP, UDP пакеты ходят.
- **Network stage 2: TCP + DNS** (TCP state machine из lwIP, DNS resolver на UDP). Goal: `connect()` работает, DNS resolution возвращает IP.
- **Network stage 3: HTTP client** (тривиальный HTTP/1.1 поверх TCP, GET/parse). Goal: `http://example.com` качается end-to-end.

**Критерий готовности:** SharpOS работает после `ExitBootServices()` со своим экраном/клавиатурой/диском. `wget http://example.com` (или эквивалент) завершается с правильным контентом.

### Phase 6 — PAL implementation и CoreCLR

- **PAL implementation** (реализация всех каталогизированных функций на C# через `[UnmanagedCallersOnly]`; threading-функции pthread-shape — тонкие обёртки над scheduler'ом из Phase 3; memory paging API; file I/O через VFS из Phase 5; executable memory management для JIT). Перед стартом — повторить de-risk spike на актуальном CoreCLR snapshot'е (полугодовая давность Phase 2 spike'а).
- **CoreCLR fork + build pipeline + hosting layer** (форкаем `dotnet/runtime`, патчим PAL слой на наш, cross-build на host'е через cmake/clang, упаковка libs в SharpOS image, упрощённый аналог hostfxr/hostpolicy для запуска `coreclr_initialize` + `coreclr_execute_assembly`).

**Граница между tier'ами — ABI line, не shared memory.** CoreCLR аллоцирует свой heap из нашего PAL `VirtualAlloc`, GC'ит независимо. Наш kernel-tier GC не знает про hosted-tier объекты, и наоборот. Boundary crossing — только через PAL calls.

**Критерий готовности:** наш kernel умеет загрузить CoreCLR runtime, инициализировать его, передать ему IL assembly для исполнения.

### Phase 7 — hosted-tier

- **First hosted-tier app** (DLL с IL внутри загружается через `Assembly.Load`, метод вызывается через `MethodInfo.Invoke`, `Console.WriteLine` идёт через настоящую BCL → PAL → наш kernel; первое реальное JIT-исполнение IL на нашем железе — integration milestone).
- **Roslyn REPL** (Roslyn как обычный NuGet-пакет поверх работающего CoreCLR, host-process читающий ввод и вызывающий `CSharpScript.EvaluateAsync`, переиспользует mini-console из Phase 3.7 расширенную до multi-line input + history).
- **PowerShell host** (`System.Management.Automation` через `PowerShell.Create()`, базовые cmdlets — Write-Host/Output, Get-Variable, ForEach-Object, основной pipelining; адаптация platform-specific частей; режется до ~5% surface).

**Критерий готовности:** интерактивный C# REPL работает (`var x = 1+1; x.ToString()` возвращает `"2"`); `PowerShell.Create().AddScript("1..10 | ForEach-Object { $_ * 2 }").Invoke()` возвращает корректный массив.

---

## Что выкинуто из scope (намеренно)

Эти вещи приходят бесплатно из CoreCLR в Phase 6+, нет смысла дублировать в std:

- AppendFormat / ISpanFormattable / ReadOnlyMemory<T>.
- LINQ.
- Managed delegates (`System.Delegate`, lambda support).
- Reflection / `typeof(T).GetMethods()` / `Assembly.Load`.

Эти вещи откладываем на неопределённый срок:

- **TLS / HTTPS** — отдельный мега-проект (BoringSSL/mbedTLS port + AES-NI/SHA-NI intrinsics).
- **USB stack** — нужен только для real HW без legacy-emulation; PS/2 покрывает QEMU и большую часть desktop.
- **GUI / window manager / audio** — вне scope.
- **Multi-NIC coverage real HW** — virtio-net + один real driver (e1000) минимум.

---

## Зависимости и параллельность

**Sequential (нельзя перепрыгнуть):**
- Phase 0 (IDT) → всё остальное (без IDT debugging невозможен).
- Phase 1 (exceptions + ACPI) → Phase 2 (PAL design знает про signal model) → Phase 3 (scheduler хочет HPET).
- Phase 3 (scheduler) → Phase 3.7 (StackInterpreter использует threading) → Phase 6 (PAL опирается на scheduler).
- Phase 5 storage + display + keyboard → ExitBootServices (нельзя без своих драйверов).
- Phase 6 → Phase 7 (CoreCLR должен работать перед hosted-tier app'ами).

**Параллельно:**
- Phase 5 (drivers) полностью независим от Phases 2/3/4 — может идти параллельно.
- Внутри Phase 0 — BCL base items независимы друг от друга, можно параллелить.
- Phase 3.5 (SMP) можно отложить на любой момент после Phase 3 main, **до** или **после** Phase 7.

---

## Ориентиры по времени

Оценки для одного разработчика без FTE, с буфером на «неожиданности». Phase 1 (full unwinding) и Phase 6 (PAL impl + CoreCLR fork) — классические места долгого застревания.

| Фаза | Диапазон |
|---|---|
| Phase 0 (IDT + BCL base) | 2-3 месяца |
| Phase 1 (exceptions + ACPI + timers + ClassConstructorRunner) | 3-6 месяцев |
| Phase 2 (PAL design + spike) | 1-2 месяца |
| Phase 3 (scheduler + threading + Task/async) | 4-6 месяцев |
| Phase 3.7 (StackInterpreter) | 1 месяц |
| Phase 4 (UEFI encapsulation) | 1-2 месяца |
| Phase 5 (drivers до HTTP) | 8-15 месяцев (параллелизуется) |
| Phase 6 (PAL impl + CoreCLR fork) | 9-18 месяцев |
| Phase 7 (hosted-tier до Roslyn REPL) | 2-4 месяца после Phase 6 |
| **До Roslyn REPL внутри SharpOS** | **24-36 месяцев** |
| Phase 7 (PowerShell host) | +6-12 месяцев после Roslyn |
| **До интерактивного PS shell** | **30-48 месяцев** |
| Phase 3.5 SMP (опционально) | +3-6 месяцев когда понадобится |

После Phase 3.7 в любой момент можно остановиться и получить работающую minimal managed OS, способную запускать native-tier C# приложения. Всё дальше — расширение до hosted-tier для Roslyn/PS.

---

## Правила корректировки плана

- Фазу можно разбить на подзадачи только внутри неё самой, не в этом документе.
- Если критерий готовности оказывается недостижим в заявленном scope — сначала сужается scope, потом пересматривается критерий, потом двигается граница фазы.
- Переход на следующую фазу — только после того как критерий готовности предыдущей выполнен на реальном железе или QEMU strict-nx.
- Документ `done/stepNN.md` фиксирует результаты по завершении каждой фазы или её значимой части.
- При появлении новой стратегической развилки (например, "Mono вместо CoreCLR" или "skip Phase 7 в пользу embedded use case") — обновляется этот документ, не игнорируется.
