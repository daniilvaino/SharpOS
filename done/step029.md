# step029

Дата: 2026-04-19
Статус: в работе

## Контекст

Продолжение SUPER-2 после step 28. На момент старта шага есть:
- `GcHeap` с bump allocator через сегменты
- `[RuntimeExport]` stubs (RhpNewFast/NewArray/RhNewString/AssignRef) — работают через `GcHeap`
- Исключение `Runtime.WorkstationGC.lib` из линковки ядра
- Mark phase (iterative DFS через `GcDescSeries`)
- Sweep phase (free-object markers)

В рамках step 29 закрываем оставшиеся фазы SUPER-2. Детали плана — в `plan.md` и `gc-experiment/PLAN.md`.

---

## Фаза 3.4: RootSource

Добавлен `std/no-runtime/shared/GC/GcRoots.cs` — registry статических корней + consenservative stack scan:

- `GcRootsStorage` — фиксированный буфер на 256 слотов (каждый хранит `nint*` — адрес статического поля с managed ref), размещён в `.bss` через `[StructLayout(Size=Capacity*8)]`.
- `CaptureStackTop()` — снимок `&local` как верхняя граница для будущего stack scan (вызывается из самого верхнего фрейма, который нужно сканировать — обычно в начале kernel main).
- `Register(ref object field)` — фиксирует АДРЕС поля (не значение), чтобы последующие записи в поле видны при следующем GC.
- `ScanStack(rspLower)` — консервативный проход по 8-байтным словам от `rspLower` до `StackTop`. Каждое непустое слово передаётся в `GcMark.MarkFromRoot`; ложные срабатывания отсекаются в `FindSegmentContaining`.
- `MarkAll()` — прогон по registry + stack scan с `&marker` локала как нижняя граница.

Kernel-интеграция: в `RunGcHeapNoNewTest` добавлены `CaptureStackTop()` и `Register(ref s_keep1)`; `GcMark.MarkFromRoot` заменён на единый вызов `GcRoots.MarkAll()`.

Логи smoke-теста:
```
count=2 bytes=80
roots: registered=1 stackTop=0x000000000FE974EC
mark: marked=1
sweep: kept=1 swept=1
```

Registry работает: `s_keep1` помечен и пережил sweep; `int[5]` (незарегистрированный) заметён.

**Ограничение.** Conservative scan находит только те ссылки, которые ILC уже вылил (spill) на стек. Live refs в callee-saved регистрах (RBX, RDI, RSI, R12-R15) сканом не видны. Для их покрытия нужен register-spill trampoline (фаза 3.5+). Попытки принудительно положить ref на стек через `stackalloc nint[1] + GetObjectAddress` упали на ILC `Code generation failed` — отложено.

Файлы:
- `std/no-runtime/shared/GC/GcRoots.cs` (новый)
- `OS/OS.csproj`, `apps/FetchApp/FetchApp.csproj`, `apps/HelloSharpFs/HelloSharpFs.csproj` — подключение `GcRoots.cs`
- `OS/src/Kernel/Kernel.cs` — вызов `CaptureStackTop`, `Register`, `MarkAll`
