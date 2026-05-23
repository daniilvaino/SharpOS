# Step 104 — BootStackSwitch (.bss-resident boot stack)

## Корень

UEFI выдаёт boot stack где-то в `EfiLoaderData` региoне. `PhysicalMemory.Init`
сканит UEFI memory map → считает эту область "Usable" → выдаёт её как GC
heap segment. Продолжающиеся `push/pop` со всё ещё активного boot stack'а
перетирают `m_pMT`-поля свежеаллоцированных object'ов в этом же регионе.
Симптом: `#GP` с `RAX = 0x0072006F00740052` (UTF-16 "Rtor") треатится как
`MethodTable*` → fault. Детектор `[SO-SUSPECT] RSP is INSIDE a GC heap
segment` — **literal**, не false positive.

## Фикс

`BootStackPool` — 4 MiB `.bss`-resident struct field, ILC кладёт в PE
binary вне UEFI memory map. `PhysicalMemory` его не видит и не может отдать
под heap.

- `BootStackPool.cs` — `[StructLayout(Size=4MiB)] struct Pool + static Pool s_pool` (value-struct, никакого cctor)
- `BootStackSwitchStub.cs` — `[RuntimeExport]` managed wrapper, body = `Panic.Fail` fallback
- `BootStackSwitchPatcher.cs` — 14-байтный inline shellcode (`mov rsp,rcx; mov rbp,0; push 0; jmp rdx`), байты inline а не `static readonly byte[]` (CLAUDE.md §1)
- `BootStackSwitch.cs` — `Activate()` align'ит Top к 16, ставит `s_done=true` ДО switch'а, вызывает шеллкод, никогда не возвращается
- `EfiEntry.cs` — `KernelMain.Start(...)` заменён на `BootStackSwitch.Activate()`
- `Scheduler.cs` — wrapped boot thread теперь имеет StackBase/Top/Bytes
- `HwFaultBridge.cs` — расширенная диагностика на `#GP`: `[STK]` + `[GC-SEG]`. Также консультирует `BigStack.TryGetActiveBounds` чтобы не давать false-positive `[SO-SUSPECT]` для CoreCLR-hosted runs (RSP внутри BigStack буфера by design)

## Эффект

Boot thread теперь физически за пределами managed heap. `[STK] StackBase=0x1CCB...`
в .bss kernel image'а — проверяется на каждом fault'е.

## Что НЕ исправил

CoreCLR-hosted код выполняется на **BigStack** (step72), не на boot stack.
BigStack ещё аллоцируется через `GcHeap.AllocateRaw(16 MiB)` и лежит внутри
managed heap segment. Это **другая** проблема — закрыта в step105.

См. [BigStack.cs](../OS/src/Kernel/Memory/BigStack.cs) header (step72 comment).
