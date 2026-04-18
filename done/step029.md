# step029

Дата: 2026-04-18
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

*(секции добавляются по мере выполнения работы)*
