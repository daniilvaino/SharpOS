# Boot order

Текущая структура kernel boot'а. Single-source-of-truth — `OS/src/Boot/BootSequence.cs`. Этот документ — high-level разбивка по фазам с зависимостями между подсистемами; за конкретный shape вызовов ходим в код.

## Фазы

```
Phase 0  Critical    panic mode + IDT (любой fault → читаемый PanicDump)
Phase 1  Memory      PhysicalMemory.Init + KernelHeap.Init
Phase 2  Runtime     exec stubs + GcHeap + NativeAotModuleInit + GC-statics
Phase 3  Platform    Pager + ACPI + HPET + RTC
Phase 4  Probes      smoke tests + diag (gated через Probes.*)
Phase 5  Apps        ELF validation, launcher, DemoApp
```

В коде каждая фаза имеет коммент-блок с явными `Pre:` / `Post:` условиями.

## Phase-by-phase

### Phase 0 — Critical

```
Panic.Mode = Shutdown
Idt.Install(bootInfo)                  ← step 35: 256 IDT entries
SystemBanner.Print
[gated] InputDiagnostics.Run           ← Probes.KeyboardInput
```

**Post:** любой kernel-side fault → читаемый PanicDump с RIP/CR2/registers.

### Phase 1 — Memory

```
PhysicalMemory.Init                    ← page allocator из UEFI memory map
PhysicalMemory.AllocPage × 3           ← sanity probe
KernelHeap.Init                        ← block allocator поверх Physical
[gated] KernelHeapSmokeTest.Run        ← Probes.KernelHeapSmoke
```

**Post:** `KernelHeap.Alloc/Free` работает. `NumberFormatting` может allocate (KernelHeap-backed string allocator).

### Phase 2 — Runtime

```
X64PageTable.SetExecBuffer
GcStackSpill.TryInitialize             ← shellcode trampoline (offset 64)
InstallInterfaceDispatchBridge         ← shellcode (offset 128)
InstallByRefAssignRefShellcode         ← patch own [RuntimeExport] body
InstallPortIoShellcode                 ← step 42: patch PortIoStub.Inb/Outb
SetJumpStubBuffer
GcHeap.Init
NativeAotModuleInit.TryInitialize      ← step 41: forced (вместо lazy)
GcStaticsMaterializer.Materialize      ← step 41: canonical static readonly
```

**Post:** `new T()` / `new T[n]` / `new string(...)` работают. Shared-generic interface dispatch резолвит. Canonical `static readonly T x = new T()` возвращает реальные object refs. Port-I/O (`PortIo.In8/Out8`) работает.

### Phase 3 — Platform

```
Pager.Init                             ← x86_64 4-level page tables
PagingValidation.Run
Acpi.Init                              ← step 38: RSDP/XSDT/MADT/HPET/MCFG
HpetTimer.Init                         ← step 39: counter + Stopwatch
[gated] DumpRtcSnapshot                ← step 42: CMOS wall-clock dump (Probes.RtcSnapshot)
```

**Post:** Pager готов (но CR3 пока firmware). ACPI tables разобраны. HPET counter крутится, `Stopwatch.StartNew/Elapsed*` работает. Wall-clock доступен через `Rtc.TryRead`.

### Phase 4 — Probes

```
[gated] GcHeapSmokeTest.Run            ← Probes.GcHeapSmoke
[gated] GcStaticsMaterializer.DumpMaterializedSummary  ← Probes.GcStaticsSummary
[gated] GcStressTest.Run               ← Probes.GcStress
[gated] NativeAotProbe.Run             ← Probes.NativeAotFeatures
[gated] CctorProbe.Run                 ← Probes.Cctor
[gated] IdtProbe.TriggerNullDeref      ← Probes.IdtPanic (never returns)
[gated] ExceptionProbe.TriggerThrow    ← Probes.ExceptionThrow (never returns)
```

Все toggle'ы — `const bool` в `OS/src/Kernel/Diagnostics/Probes.cs`. Disabled probes elim'ятся ILC'ом.

### Phase 5 — Apps

```
ElfValidation.Run                      ← FS init + walk \EFI\BOOT
DemoApp.Run                            ← Fib + heap test
```

## Hard prerequisites

| Что | Требует чего |
|---|---|
| Idt.Install | `bootInfo.IdtExecBuffer` (UEFI `EfiLoaderCode`) |
| KernelHeap.Init | `PhysicalMemory.Init` |
| GcStackSpill / patchers | `bootInfo.ExecStubBuffer` |
| GcHeap.Init | KernelHeap (для backing allocations) |
| NativeAotModuleInit | RTR section access (anchor MT pointer) |
| GcStaticsMaterializer | NativeAotModuleInit + GcHeap |
| Pager.Init | PhysicalMemory + KernelHeap |
| Acpi.Init | `bootInfo.SystemTable` |
| HpetTimer.Init | Acpi (HPET base address) |
| Rtc.TryRead | PortIoPatcher.TryInstall (port I/O shellcode) |

## Что использует что когда

- **`Console.Write` строковых литералов** — frozen objects, работают с самого начала boot'а.
- **`Console.WriteUInt`** — нужен KernelHeap (для FastAllocateString). До этого фолбэчит на `*Raw` варианты (stackalloc only).
- **`new SomeClass()`** — нужен `GcHeap.Init()`. До этого `RhpNewFast` halt'ит.
- **`static readonly T x = new T()`** — нужен `GcStaticsMaterializer.Materialize()`. После Phase 2 доступно везде.
- **Shared-generic interface dispatch** — нужен `InstallInterfaceDispatchBridge` + `NativeAotModuleInit`. После Phase 2.
- **Port I/O (RTC, PIC, future drivers)** — нужен `PortIoPatcher.TryInstall`. После Phase 2.

## Изменение порядка

При любом изменении порядка boot'а:

1. **Обновить этот файл.** Primary source of truth для phase boundaries.
2. **Прокачать через QEMU** — clean boot должен показать все probe'ы зелёными + launcher работает.
3. Проверить что нет stale-инициализаций: code path X использует Y? Y готов когда X запускается?
4. Особое внимание: если переносим что-то «раньше» — оно может пытаться использовать ещё-не-готовые подсистемы.

## Будущие фазы (plan.md)

- **Phase 1 (продолжение)** — managed try/catch/finally (`System.Exception`, personality function, stack unwinding). Самый долгий открытый пункт.
- **Phase 3 scheduler** — добавит APIC timer + context-switch в boot (после Acpi).
- **Phase 4 ExitBootServices** — boot pipeline разделится на pre-EBS / post-EBS. Patcher'ам потребуется alias-map путь после EBS (W^X на real HW).
- **Phase 6 CoreCLR fork** — добавит инициализацию hosted-tier runtime отдельной фазой в конце boot'а.
