# step029

Дата: 2026-04-18  
Статус: в работе

## Цель

Завершить SUPER-2 (Serial GC в ядре). После step 28 у нас есть:
- GcHeap с bump allocator через сегменты
- `[RuntimeExport]` stubs для `new` — работают через GcHeap
- Исключение NativeAOT's `Runtime.WorkstationGC.lib` из линковки
- Mark phase (iterative DFS через GcDescSeries)
- Sweep phase (free-object markers)

Остаётся:
- **Фаза 3.4** — RootSource (автоматический поиск корней для Mark)
- **Фаза 3.5** — `GC.Collect()` API + freelist reuse + автоматический trigger
- **Фаза 4** — миграция apps (убираем C-стаб из WSL-сборки, apps получают unified runtime)
- **Фаза 5** — стабилизация, stress-тесты, интеграция с std collections

---

## Фаза 3.4 — RootSource

### Источники корней

**1. Conservative kernel-stack scan**
- Взять текущий RSP и kernel stack top
- Идти word-by-word (8 байт), проверять каждое значение через `GcHeap.FindSegmentContaining(value) != null`
- Если попадает в сегмент — `MarkFromRoot(value)`

**Conservative** — не знаем точно pointer это или случайное число. Мусорные попадания безвредны (просто "живёт" объект дольше положенного), пропуски — опасны (объект освобождён, но на него есть ссылка). Поэтому лучше перестраховаться.

**2. Static roots registry**
- `GcRoots.Register(ref object field)` — явная регистрация static полей ядра
- При `MarkAll()` проходим по registry, dereference, пушим

### Детали реализации

RSP получаем через helper с `[UnmanagedCallersOnly]` или asm-intrinsic через `System.Runtime.CompilerServices.RuntimeHelpers`.

Kernel stack top — из `BootInfo` (если был передан) или через UEFI system table.

[в процессе]

---

## Фаза 3.5 — GC.Collect + freelist

`GC.Collect()` — публичный API. Делает:
1. Unmark всего (на случай если прошлый GC не зачистил — defensive)
2. RootSource.MarkAll (добавляет из всех источников)
3. Sweep

Freelist reuse в GcHeap — при аллокации искать free-object блоки подходящего размера, использовать их вместо bump. Coalesce смежные free blocks.

Автотриггер — когда `AllocBytes > threshold` (например 75% от segment), вызываем `GC.Collect()`.

[в процессе]

---

## Фаза 4 — миграция apps

Apps сейчас используют `runtime_stubs.c` в WSL-сборке, который даёт `RhNewString`, `sharp_alloc` (bump), `memset/memcpy/memmove`.

Наши `[RuntimeExport]` stubs в `std/no-runtime/shared/GC/GcRuntimeExports.cs` уже могут заменить их все. Нужно:
1. Убрать `runtime_stubs.c` из `build_launcher_wsl.ps1` и `build_fetch_wsl.ps1`
2. Линковать `GcRuntimeExports.cs` в apps csproj
3. Линковать `GcMemorySource.AppStatic.cs` для backing storage
4. Прогонять оба apps в QEMU, убедиться что всё работает

[в процессе]

---

## Фаза 5 — стабилизация

- Stress-тест: аллокация в цикле, вызов GC периодически, проверка что heap не растёт бесконечно
- Smoke-test коллекций: когда std/ получит List<T>, использовать его над нашим GC
- Отладочные инварианты: mark bits чистые после GC, free blocks не содержат ссылок, и т.д.

[в процессе]
