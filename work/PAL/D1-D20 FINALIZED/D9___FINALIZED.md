## D9 — FINALIZED

### Решение

**Functional split по domain, как Microsoft's pal/src/. Без preinit/forward разделения.**

Структура:

```
pal/sharpos/
├── memory.cpp       ← VirtualAlloc, VirtualFree, VirtualProtect
├── thread.cpp       ← CreateThread (ABORT_FATAL), thread sync
├── module.cpp       ← LoadLibraryEx, GetProcAddress
├── file.cpp         ← CreateFile, ReadFile, WriteFile
├── errno.cpp        ← GetLastError, SetLastError (D2's thread_local)
├── pal_init.cpp     ← PAL_Initialize entry point
├── trace.cpp        ← scaffolding logging (D3)
├── policy_table.cpp ← classification table (D3)
└── ... (по domain'у)
```

**Никакого preinit/forward split.** Если static init проблема случится — fix locally в проблемной функции через assertion.

### Принципиальное направление PAL

**PAL должен быть тонкий. Functionality живёт в kernel, не в PAL.**

PAL — это **translation layer** между Win32-shape API (что CoreCLR ожидает) и SharpOS host API. Его работа:

- Принять Win32-shape вызов от vm/
- Перевести аргументы
- Вызвать SharpOSHost_*
- Перевести return value обратно в Win32-shape
- Никакой собственной логики

**Антипаттерн**: писать в PAL функционал что должен жить в kernel. Например реализовывать handle table, virtual memory tracking, thread coordination внутри pal/sharpos/. Это значит дублировать инфраструктуру kernel'а в guest коде.

**Правильный паттерн**: kernel-tier C# code предоставляет все capabilities. SharpOSHost exports их через C-ABI. PAL wraps под Win32-shape semantics.

### Конкретный пример что значит «тонкий PAL»

**Wrong (functionality в PAL)**:

cpp

```cpp
// pal/sharpos/memory.cpp
struct MemoryRegion {
    void* base;
    size_t size;
    int protection;
};

static std::vector<MemoryRegion> g_regions;  // PAL-level tracking
static std::mutex g_regionsLock;

HANDLE WINAPI VirtualAlloc(LPVOID addr, SIZE_T size, DWORD type, DWORD protect) {
    // PAL implements RESERVE/COMMIT discipline itself
    // PAL maintains region tracking
    // PAL handles concurrency
    void* result = SharpOSHost_AllocPages(size);
    {
        std::lock_guard<std::mutex> lock(g_regionsLock);
        g_regions.push_back({result, size, protect});
    }
    return result;
}
```

**Right (functionality в kernel, PAL тонкий)**:

cpp

```cpp
// pal/sharpos/memory.cpp
HANDLE WINAPI VirtualAlloc(LPVOID addr, SIZE_T size, DWORD type, DWORD protect) {
    SharpOS_AllocFlags flags = TranslateWin32Flags(type, protect);
    SharpOS_Status status = SharpOSHost_AllocPages(addr, size, flags, &result);
    if (status != SHARPOS_SUCCESS) {
        SetLastError(TranslateError(status));
        return NULL;
    }
    return result;
}
```

Kernel-tier C# code держит region tracking, concurrency, RESERVE/COMMIT discipline. PAL только translates.

### Почему это важно

**Strategic причина**: всё что в PAL — это **temporary code** (specifically для CoreCLR hosting). Всё что в kernel — это **permanent infrastructure** SharpOS, переиспользуется для:

- Phase 5 drivers
- Future hosted runtimes (если когда-нибудь второй runtime add'ится)
- User applications через kernel API directly
- Phase 7 unikernel mode (if applicable)

**Tactical причина**: PAL maintenance cost растёт linearly с size. Минимальный PAL = минимальный maintenance burden + минимальный merge conflict surface vs upstream CoreCLR.

**Invariant 1 причина**: всё что в kernel = C#. Всё что в PAL = C++ (живёт в CoreCLR fork repo). Чем больше функционал в kernel — тем меньше C++ кода в проекте.

### Decision rule для каждой PAL функции

Когда implement'аем новую PAL функцию, спрашиваем:

__Можно ли это сделать через один или несколько SharpOSHost__ calls?_*

- Yes → PAL функция = thin translation wrapper
- No → значит SharpOSHost API нужно расширить. Расширяем kernel-tier capability + добавляем new SharpOSHost_* export. Возвращаемся к PAL функции уже с ready capability в host.

**Антипаттерн что избегаем**: «давайте просто в PAL это implement'нём, kernel расширять долго». Это путь к раздутому PAL и упускаемой kernel functionality.

### Структура каталогов украдена из Microsoft

Domain split (memory.cpp, thread.cpp, module.cpp, etc.) — **точно как `dotnet/runtime/src/coreclr/pal/src/`**. Microsoft validated это organization 10+ years.

Различие с Microsoft: у них pal/src/ имеет ~52 KLOC implementation (потому что PAL делает Win32 emulation поверх POSIX). У нас pal/sharpos/ должна быть значительно меньше — большинство функций тонкие wrappers, capabilities в kernel.

**Цель размера**: pal/sharpos/ ≤ 5-10 KLOC после полной реализации. Если больше — флаг что мы implementируем в PAL что должно быть в kernel.

### Что НЕ делаем

**Нет**:

- Preinit/forward split каталогов
- Custom assertion frameworks для static init protection
- Region tracking / handle tables / state management в PAL
- Concurrency primitives в PAL
- Бизнес логика в PAL (только translation)

**Да** (если случится):

- Local assertion в одной функции при выявленной static init проблеме
- Расширение kernel-tier capabilities когда PAL функция требует чего-то нового
- Расширение SharpOSHost API surface когда нужен новый capability на C-ABI границе

### Принципы установленные D9

(дополнение к D1-D5)

19. **Тонкий PAL — толстый kernel.** Functionality живёт в kernel, не в guest translation layer. PAL только translates Win32-shape ↔ SharpOSHost. Если PAL функции нужна логика — расширяем kernel/host API, не implement'ируем в PAL.
20. **Steal structure from production когда есть.** Microsoft's pal/src/ functional split (по domain'у) validated 10+ years. Используем тот же pattern — никакого изобретения собственной структуры.
21. **Add architecture defenses when actually needed.** Не строим preinit/forward split против гипотетической static init проблемы. Если случится — fix локально. Architecture defenses оправданы только реальными проблемами что повторяются.
22. **PAL size как метрика правильности архитектуры.** Если PAL растёт большим — флаг что functionality должна быть в kernel. ~5-10 KLOC после полной реализации = healthy. ~50 KLOC = warning что implement'ируем не там.

### Связь с другими decisions

- **D1** (error codes): translation в PAL (SharpOSHost SystemError → Win32 ERROR_*) — это и есть PAL job
- **D2** (LastError): живёт в `pal/sharpos/errno.cpp` (один из domain files)
- **D3** (scaffolding): `pal/sharpos/trace.cpp`, `policy_table.cpp` — отдельные domain files в той же flat structure
- **D4** (catch-all): pattern в SharpOSHost C# side, не в PAL. PAL получает уже translated SystemError
- **D5** (CreateThread): в `pal/sharpos/thread.cpp` как ABORT_FATAL stub

### Upstream merge cost

Один patch в `vm/ceemain.cpp` (TARGET_SHARPOS conditional из D5). Plus `pal/sharpos/` как parallel directory к `pal/linux/`, `pal/macos/` — никаких изменений в Microsoft files. Merge cost минимальный.