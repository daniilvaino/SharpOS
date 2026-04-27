# Boot order

Текущая последовательность инициализации в `KernelMain.Start`. Порядок принципиален — каждый шаг зависит от готовности предыдущих, и нарушение порядка обычно даёт unresolved helpers / `#PF` / silent hang.

## Текущая последовательность

```
1.  Panic.Mode = Shutdown
2.  Idt.Install(bootInfo)                  ← step 35: IDT + signal-dispatch
3.  SystemBanner.Print
4.  Memory map summary
5.  PhysicalMemory.Init                    ← page allocator
6.  KernelHeap.Init                        ← block allocator
7.  Heap smoke test
8.  X64PageTable.SetExecBuffer
9.  GcStackSpill.TryInitialize             ← shellcode trampolines
10. InstallInterfaceDispatchBridge
11. InstallByRefAssignRefShellcode
12. SetJumpStubBuffer
13. RunGcHeapNoNewTest                     ← GcHeap.Init() inside
14. GcStressTest.Run
15. NativeAotProbe.Run                     ← lazy NativeAotModuleInit triggered
                                              на первом shared-generic iface dispatch
16. InitializePager                        ← x86_64 4-level page tables
17. RunPagerValidation
18. InitializeAcpi(bootInfo)               ← step 38: RSDP/XSDT/MADT/HPET/MCFG
19. InitializeHpet                         ← step 39: counter + Stopwatch
20. GcStaticsMaterializer.Materialize      ← step 41: walks GCStaticRegion,
                                              materializes preinit'd refs
21. CctorProbe.Run                         ← canonical static readonly T x = new T()
22. RunIdtPanicProbe                       ← gated, manual flip
23. RunExceptionThrowProbe                 ← gated, manual flip
24. RunElfValidation                       ← FS init, ELF apps, launcher
25. DemoApp.Run                            ← Fib + heap test
```

## Зависимости

### Hard prerequisites

| Что | Требует чего |
|---|---|
| Idt.Install | bootInfo.IdtExecBuffer (UEFI EfiLoaderCode) |
| KernelHeap.Init | PhysicalMemory.Init |
| GcStackSpill / shellcode patchers | bootInfo.ExecStubBuffer |
| GcHeap.Init | KernelHeap |
| NativeAotModuleInit (lazy) | RTR section access (any kernel MT pointer) |
| GcStaticsMaterializer | NativeAotModuleInit (TypeManager) + GcHeap (AllocateRaw) |
| Pager.Init | PhysicalMemory + KernelHeap |
| Acpi.Init | bootInfo.SystemTable |
| Hpet.Init | Acpi (HPET base address) |

### Что использует что когда

- **Console.Write строковых литералов** — frozen objects, работают с самого начала boot'а.
- **Console.WriteUInt / NumberFormatting** — нужен `KernelHeap` (для allocate string'ов через FastAllocateString). Если зовётся раньше — фолбэчит на `*Raw` варианты (stackalloc only).
- **`new SomeClass()` в managed коде** — нужен `GcHeap.Init()`. До этого ILC-emitted `RhpNewFast` зовёт нашу C# реализацию которая halt'ит если GcHeap не готов.
- **`static readonly T x = new T()` через canonical pattern** — нужен `GcStaticsMaterializer.Materialize()`. До неё ILC's preinit'd descriptor cells содержат tagged tags, не реальные object refs — `__GetGCStaticBase_*` возвращает sentinel, любое чтение `x.field` → `#GP`.
- **Shared-generic interface dispatch** — нужен `InstallInterfaceDispatchBridge` + `NativeAotModuleInit`. Без них halt.

## Известные ограничения порядка

### Materialization поздно

Сейчас `GcStaticsMaterializer.Materialize()` стоит на шаге 20 — **после** ACPI/HPET. Это значит **canonical static-readonly-ref pattern недоступен в шагах 1-19**. Код там должен использовать `""` literal вместо `string.Empty` field, factory properties вместо lazy reference fields, и так далее.

**План на step 42:** переместить materialization сразу после шага 13 (`GcHeap.Init`), force-инициализируя `NativeAotModuleInit` явно (вместо lazy). После этого canonical pattern доступен с шага 14 и далее → можно глобально мигрировать `""` → `string.Empty`, dropping factory properties etc.

Зависимость: NativeAotModuleInit needs an "anchor MT" — любой `MethodTable*` из нашего бинаря для signature scan'а. Можно использовать `EETypePtr.EETypePtrOf<object>()` или подобный intrinsic, без ожидания первого interface dispatch'а.

### IDT первым

IDT install **должна** идти первой. До неё любой `#PF/#GP/#UD` (например, баг в KernelHeap.Init) даёт triple-fault → ребут OVMF → потеря контекста. После — все exceptions попадают в `PanicDump` с RIP/CR2/registers.

### NativeAotModuleInit — сейчас lazy

`NativeAotModuleInit` ищет ReadyToRunHeader в бинаре сканированием от anchor MT. Сейчас инициализируется при первом `RhpInitialDynamicInterfaceDispatch` call'е (то есть в `NativeAotProbe.Run` в shared-generic iface call probe). Это работает потому что materialization сейчас стоит ещё позже.

После step 42 переместим NativeAotModuleInit init в early boot (сразу после KernelHeap.Init), чтобы materialization могла произойти до probes.

## Изменение порядка

При любом изменении порядка boot'а:

1. **Обновить этот файл.** Серьёзно — это primary source of truth.
2. **Прокачать через QEMU** — clean boot должен показать все probe'ы зелёными + launcher работает.
3. **Проверить что нет stale-инициализаций**: code path X использует Y? Y готов когда X запускается?
4. Особое внимание: если переносим что-то "раньше" — оно может пытаться использовать ещё-не-готовые подсистемы.

## Будущие фазы (plan.md)

- **Phase 3 scheduler** — добавит APIC timer setup в boot (после Acpi). Контекст-switch infrastructure.
- **Phase 4 ExitBootServices** — boot pipeline разделится на pre-EBS (с UEFI services) и post-EBS (со своими драйверами). Оба порядка надо документировать.
- **Phase 6 CoreCLR fork** — добавит инициализацию hosted-tier runtime после kernel-tier ready. Скорее всего как отдельная фаза в конце boot'а.
