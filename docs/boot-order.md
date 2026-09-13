# Boot order

Порядок загрузки ядра. Источник истины — `OS/src/Boot/BootSequence.cs`; здесь
разбивка по фазам и зависимости между подсистемами. За точной формой вызовов
идти в код: этот файл устаревает первым.

## Фазы

```
Phase 0  Critical    режим паники + IDT (любой отказ читаем)
Phase 1  Memory      физические страницы, куча ядра
Phase 2  Runtime     exec-стабы + управляемый рантайм + GC + материализация статики
Phase 3  Platform    пейджер + ACPI + HPET + фреймбуфер
Phase 4  Probes      самопроверки, драйверы, снятие UEFI, размещённый CoreCLR
Phase 5  Apps        обход \apps и запуск лаунчера
```

У каждой фазы в коде комментарий с явными `Pre:` / `Post:`.

**Вырожденный путь.** Если прошивка не отдала карту памяти, `Run` пропускает
фазы 1–4 и идёт сразу в Phase 5 — `DemoApp` памяти не требует.

## Phase 0 — Critical

```
Panic.Mode = Halt                      ← не Shutdown: при панике память ценна,
                                         выключение её уничтожает, а halt
                                         оставляет QMP живым для dump_virt.ps1
Idt.Install(bootInfo)
SystemBanner.Print
[gated] InputDiagnostics.Run           ← Probes.KeyboardInput
```

**Post:** любой отказ ядра даёт читаемый `PanicDump` с RIP/CR2/регистрами.

## Phase 1 — Memory

```
PrintMemorySummary
PhysicalMemory.Init                    ← постраничный аллокатор по карте UEFI
PhysicalMemory.AllocPage × 3           ← проверка вменяемости
KernelHeap.Init
[gated] KernelHeapSmokeTest.Run
[gated] SimdProbe.Run                  ← Probes.Simd: узнал ли ILC наши Vector128
```

**Post:** `KernelHeap.Alloc/Free` работает; `NumberFormatting` может выделять
строки.

## Phase 2 — Runtime

```
X64Asm.SetExecBuffer
AtomicBackendInstaller.Install         ← как можно раньше: всё, что берёт замок,
                                         стоит на Interlocked, а до этого он
                                         обычное чтение-запись, которое тик делит
X64PageTable.SetExecBuffer
GcStackSpill.TryInitialize             ← консервативный спилл регистров
GcContextSpill.TryInitialize           ← полный CONTEXT для точного обхода
InstallInterfaceDispatchBridge         ← отказ = паника: без моста первая же
                                         диспетчеризация умрёт «stub not patched»
InstallByRefAssignRefShellcode
InstallChkstkShellcode
InstallPortIoShellcode
InstallCaptureContextShellcode
InstallThrowExShellcode
InstallCallCatchFuncletShellcode
InstallRethrowShellcode
InstallCallFinallyFuncletShellcode
InstallCallFilterFuncletShellcode
EhProbe.InstallStep5_5TestHarness
X64PageTable.SetJumpStubBuffer
GcHeap.Init                            ← отказ = паника
GC.s_collectHook → KernelGC.CollectConservative
GcHeap.s_enterCritical/s_leaveCritical → Preemption.Suppress/Allow
NativeAotModuleInit.TryInitialize      ← обход RTR, TypeManager
CoffRuntimeFunctionTable.TryInitialize ← .pdata образа, RIP → метод
GcStaticsMaterializer.Materialize
```

**Post:** работают `new T()` / `new T[n]` / `new string(...)`, диспетчеризация
интерфейсов из shared-generic, канонический `static readonly T x = new T()`,
port-I/O, и весь набор EH-стабов пропатчен.

Про хук сборки: `System.GC.Collect()` из std делает слепой `MarkAll` и не видит
корней в регистрах, сохраняемых вызываемым. Хук уводит его в `KernelGC`, который
спиллит регистры. Без этого живой локал может быть подметён.

## Phase 3 — Platform

```
InitializePager                        ← свои 4-уровневые таблицы (клон)
ActivatePagerRootAndLockCpuFeatures    ← CR3 становится наш; XCR0 запирается
                                         на x87|SSE, если прошивка дала OSXSAVE
DumpExecBuffers
RunPagerValidation
VirtualMemory.SelfTest                 ← отказ = паника: от окна зависит
                                         размещённый CoreCLR из фазы 4
[gated] FpFaultProbe.Run               ← переживают ли XMM отложенную страницу
[gated] ProtectPagesProbe.Run          ← становится ли страница исполняемой
Framebuffer.TryInit                    ← не фатально: headless идёт дальше
InitializeAcpi                         ← RSDP/XSDT/MADT/HPET/MCFG
InitializeHpet
ReportCpuClock
FbPerfProbe.Run
[gated] DumpRtcSnapshot
```

**Post:** активен наш CR3, окно виртуальной памяти проверено, таблицы ACPI
разобраны, счётчик HPET крутится, `Stopwatch` пригоден, фреймбуфер отображён.

## Phase 4 — Probes

Самая длинная фаза: здесь и самопроверки, и поднятие драйверов, и снятие UEFI,
и размещённый рантайм. Все переключатели — `const bool` в
`OS/src/Kernel/Diagnostics/Probes.cs` (80 флагов), выключенные пробы ILC
выбрасывает целиком.

```
TerminalProbe                          ← движок XtermSharp; позже фазы 2/3,
                                         потому что нужны материализованная
                                         статика и отображённый фреймбуфер
SerialProbe, FbRenderProbe, Ps2, LineEditorProbe
PciProbe, UsbProbe                     ← xHCI, HID, накопители
GC: GcHeapSmoke, GcStaticsSummary, GcStress, CoffGcInfoDump,
    GcInfoResolverSmoke, GcContextSpillSmoke, KernelGcPreciseSmoke
NativeAotProbe                         ← язык и BCL: дженерики, делегаты,
                                         LINQ, PeNet, PE-загрузчик, перечисления
XmlProbe                               ← TurboXml + разбор манифеста
CctorProbe, EhProbe                    ← EH: throw/catch/finally/фильтры,
                                         HW-fault, трассы, collided unwind
Threading: TebFacade, Atomics, ThreadPingPong, ThreadGcRoots, Sleep,
           Event, Semaphore, AllocStress, ProcessSpawn
NativeAotProbe.RunLate
HandleTable.Init, AddressWait.Init
[gated] CoreClrProbe.Run               ← размещённый CoreCLR на 16 МиБ стеке
[gated] ExitBootServicesProbe.Run      ← взаимоисключающи с предыдущим
[gated] IdtPanic / ExceptionThrow      ← не возвращаются
```

**Снятие UEFI живёт внутри `ExitBootServicesProbe`**, а не отдельной фазой, и
там же поднимается всё, что становится возможным только после него: локальный
APIC с периодическим тиком (`LocalApic.StartPeriodic`), вытеснение
(`Preemption.Enable`) и поток-насос ввода (`InputPump.Start`).

## Phase 5 — Apps

```
LauncherBoot.Run                       ← FS init + обход \apps + запуск
                                         \apps\LAUNCHER.EXE
DemoApp.Run
```

До step171 класс назывался `ElfValidation` — с тех пор, когда эта фаза гоняла ELF-приложения.

## Жёсткие предусловия

| Что | Требует чего |
|---|---|
| `Idt.Install` | `bootInfo.IdtExecBuffer` (UEFI `EfiLoaderCode`) |
| `AtomicBackendInstaller` | `bootInfo.AsmExecBuffer` |
| `KernelHeap.Init` | `PhysicalMemory.Init` |
| Спиллы и патчеры | `bootInfo.ExecStubBuffer` |
| `GcHeap.Init` | `KernelHeap` |
| `NativeAotModuleInit` | доступ к секциям RTR (якорный MT) |
| `GcStaticsMaterializer` | `NativeAotModuleInit` + `GcHeap` |
| `CoffRuntimeFunctionTable` | образ PE в памяти (`.pdata`) |
| `Pager.Init` | `PhysicalMemory` + `KernelHeap` |
| `VirtualMemory.SelfTest` | активированный корень пейджера |
| `Acpi.Init` | `bootInfo.SystemTable` |
| `HpetTimer.Init` | `Acpi` (адрес HPET) |
| `Rtc.TryRead` | port-I/O шеллкод |
| `TerminalConsole` | материализованная статика + фреймбуфер |
| `LocalApic.StartPeriodic` | `Acpi` (MADT) + снятый UEFI |
| Вытеснение | тик APIC + `Scheduler` |

## Что доступно с какого момента

- **Строковые литералы в `Console.Write`** — с самого начала (frozen objects).
- **`Console.WriteUInt`** — нужен `KernelHeap`; до него уходит в `*Raw`-варианты
  на `stackalloc`.
- **`new SomeClass()`** — нужен `GcHeap.Init`; до него `RhpNewFast` останавливает
  машину.
- **`static readonly T x = new T()`** — после `GcStaticsMaterializer`.
- **Диспетчеризация интерфейсов из shared-generic** — после моста и
  `NativeAotModuleInit`.
- **`throw` / `catch`** — после патча EH-стабов (фаза 2). В фазе 1 бросок даёт
  панику «stub not patched».
- **Port-I/O** — после `InstallPortIoShellcode`.
- **Вытеснение** — только после снятия UEFI, внутри пробы EBS.

## Изменение порядка

1. **Обновить этот файл** — он единственное место, где границы фаз описаны
   словами.
2. Прогнать батарею: `tools/probe_report.ps1` по `last_build.log`; лаунчер
   должен стартовать, ценз дойти до конца.
3. Проверить предусловия: путь X использует Y — готов ли Y к моменту X?
4. Перенос «раньше» опаснее переноса «позже»: подсистема может опереться на
   ещё не поднятую.
