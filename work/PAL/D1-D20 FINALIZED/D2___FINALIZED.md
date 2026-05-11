## D2 — FINALIZED

### Решение

Использовать стандартный механизм C++11 `thread_local` для хранения thread-local state в `pal/sharpos/`. Под этим контрактом строится отдельная инфраструктурная подзадача **Phase 5.5 — Native TLS bring-up**, которая обеспечивает реальную работу `thread_local` на голом железе.

PAL пишется один раз против стабильного контракта `thread_local`. Подкладка постепенно расширяется со временем без изменения PAL.

### Архитектура трёх стадий

```
┌──────────────────────────────────────────────────────────────────┐
│ Phase 2 — Spike (Windows-hosted TARGET_SHARPOS)                 │
│                                                                  │
│ pal/sharpos/  uses  thread_local PalThreadState                 │
│       ↓                                                          │
│ MSVC CRT + PE TLS infrastructure (TEB-based, auto)              │
│                                                                  │
│ ✓ Работает out of the box (compiler/CRT/PE TLS)                 │
│ ✓ Audit only — verify thread_local resolves корректно           │
│ ✓ Per D5 — single-thread mode, threading deferred к Phase 6.2   │
└──────────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────────┐
│ Phase 5.5 — Native TLS bring-up (1-2 недели)                    │
│                                                                  │
│ Минимальная TLS infrastructure для main thread на голом железе. │
│ Substrate target-format-dependent:                              │
│                                                                  │
│ Если final CoreCLR archive это COFF/win-x64:                    │
│   • TEB-based TLS (Windows PE convention)                       │
│   • GS register на x86-64 Windows ABI                           │
│   • _tls_index, TLS Directory в PE image                        │
│                                                                  │
│ Если final CoreCLR archive это ELF/SysV (alternative):          │
│   • FS register convention из System V x86_64 ABI               │
│   • TLS image из ELF .tdata/.tbss секций                        │
│                                                                  │
│ Format decision открыто до тех пор пока Phase 2 spike не        │
│ покажет what toolchain/format CoreCLR fork builds стабильно.    │
│                                                                  │
│ В обоих случаях:                                                │
│ • RhpGetThreadStaticBase* helpers реализованы                   │
│ • [ThreadStatic] в C# работает (single thread)                  │
│ • thread_local в C++ работает (single thread)                   │
│ • Никакого scheduler'а, никакого Thread.Start, ничего из Phase 3│
└──────────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────────┐
│ Phase 6 — CoreCLR PAL implementation                            │
│                                                                  │
│ pal/sharpos/  uses  thread_local PalThreadState                 │
│       ↓                                                          │
│ Native TLS infrastructure (готова с Phase 5.5)                  │
│                                                                  │
│ ✓ Работает на bare metal                                        │
│ ✓ Один и тот же PAL код что в spike — никакой переделки         │
└──────────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────────┐
│ Phase 3 — Full threading session                                │
│                                                                  │
│ Расширяет существующую подкладку:                               │
│ • При создании потока — выделяется новая TLS область            │
│ • При context switch — обновляется TLS register (FS/GS)         │
│ • Multi-thread автоматически работает на стабильном контракте   │
│                                                                  │
│ PAL код НЕ трогается. Тот же thread_local работает.             │
└──────────────────────────────────────────────────────────────────┘
```

### Ключевой принцип

> **Контракт стабильный, реализация расширяется со временем.**

Это применение принципа D1 «steal interfaces, implement bodies» к собственной инфраструктуре. Контракт `thread_local` украден из C++11 (стандартный, переносимый, понятный любому разработчику). Реализация контракта расширяется постепенно: pthread на Linux → один поток на bare metal → много потоков на bare metal.

PAL пишется один раз против контракта. Никаких #ifdef, никаких macros, никакой dual mode логики.

### Что закладывается в Phase 5.5

**Использовать стандартный аппаратный механизм процессора с самого начала.**

Указатель на TLS область кладётся в TLS-register процессора. Выбор register convention зависит от final CoreCLR archive format:

- **Windows ABI (PE/COFF, win-x64)**: GS register convention — TEB-based TLS
- **System V ABI (ELF, Linux convention)**: FS register convention

Format decision **открыто** до тех пор пока Phase 2 spike не покажет what toolchain/format CoreCLR fork builds стабильно на TARGET_SHARPOS path. По умолчанию ожидается PE/COFF (Windows-hosted spike + Phase 1 SharpOS уже PE-flavored), но это не зафиксировано в D2 — решение деferred до empirical confirmation.

Механизм одинаков для одного потока и для тысячи независимо от выбранного register:

- Один поток: положили указатель в TLS register один раз, всё.
- Много потоков: при context switch обновляем TLS register на область текущего потока.

Контракт «TLS register указывает на TLS область текущего потока» одинаковый в обоих случаях. Это позволяет PAL не знать сколько потоков существует.

Если бы для single thread мы сделали другой механизм (например глобальная переменная) — потом Phase 3 пришлось бы переделывать. Используем правильный механизм с самого начала — переделок не будет.

### Реализация D2 в коде

cpp

```cpp
// pal/sharpos/pal_thread_state.h
#ifndef _PAL_THREAD_STATE_H
#define _PAL_THREAD_STATE_H

#include <stdint.h>

// Stolen pattern from CoreCLR's CPalThread
// (dotnet/runtime: src/coreclr/pal/src/include/pal/thread.hpp)
//
// Минимальный subset для D2. Расширяется по мере необходимости
// (signal mask, thread handle, exception state и т.д.)
struct PalThreadState {
    uint32_t lastError;  // Win32 GetLastError/SetLastError storage
    // Reserved for future extension
};

extern thread_local PalThreadState g_palThreadState;

#endif
```

cpp

```cpp
// pal/sharpos/errno.cpp
#include "pal_thread_state.h"

thread_local PalThreadState g_palThreadState = {0};

extern "C" DWORD WINAPI GetLastError() {
    return g_palThreadState.lastError;
}

extern "C" void WINAPI SetLastError(DWORD err) {
    g_palThreadState.lastError = err;
}
```

### Что украдено

|Артефакт|Источник|License|
|---|---|---|
|Pattern struct PalThreadState|`CPalThread` из `dotnet/runtime/src/coreclr/pal/src/include/pal/thread.hpp`|MIT|
|`thread_local` keyword|C++11 standard|(standard)|
|FS register convention|System V x86_64 ABI specification (alternative)|(standard)|
|GS register convention|Windows x86_64 ABI specification (default expected)|(standard)|
|TLS image layout (.tdata/.tbss)|ELF specification + Itanium TLS ABI (if ELF chosen)|(standard)|
|TLS Directory in PE image|PE/COFF specification (if PE chosen)|(standard)|
|`RhpGetThreadStaticBase*` contract|NativeAOT runtime contract|MIT|

### Что своё

- Phase 5.5 как отдельная подзадача (1-2 недели предположительно)
- Реализация `RhpGetThreadStaticBase*` helpers для kernel
- FS/GS register bootstrap при инициализации main thread (выбор зависит от final format)
- Загрузчик TLS image из object format секций при boot (.tdata/.tbss для ELF, TLS Directory для PE)

### Влияние на план проекта

**Изменение последовательности**:

```
Было:    Phase 1 → Phase 2 → Phase 3 → ... → Phase 6
Стало:   Phase 1 → Phase 2 → Phase 5.5 → Phase 6 → Phase 3
                              ↑↑↑
                              новая подзадача
```

**Phase 5.5 разблокирует**:

- D2 (LastError storage) — закрывается этим решением
- D7 (TLS implementation) — становится не decision а «уже сделано»
- Phase 6 не упирается в TLS как блокер
- Любой kernel-tier C# код может использовать `[ThreadStatic]` если нужно

**Phase 3 потом**:

- Строится на готовом фундаменте быстрее
- Полноценный scheduler, Thread.Start, async/await, синхронизация
- Не делает TLS работу заново — расширяет существующую

### Преимущества решения

1. **Risk-aware**: делаем только тот минимум инфраструктуры что нужен для разблокировки Phase 6, не делаем преждевременных вложений в полный scheduler.
2. **Контракт стабильный с начала**: PAL пишется один раз и никогда не переделывается под изменения threading модели.
3. **Стандартный механизм процессора**: используем то что spec'ит ABI (System V для ELF, Windows для PE), не изобретаем свой механизм.
4. **Маленький scope, чёткие границы**: «изоляция памяти на поток, и больше ничего». Не уходит в архитектурные дискуссии про планировщик.
5. **Tractable timeline**: 1-2 недели, не месяцы. Не блокирует общий прогресс.
6. **Phase 6 starts faster**: не ждём пока полная Phase 3 закроется.

### Принципы установленные D2

(дополнение к принципам D1)

5. **Build infrastructure до того как нужно использовать массово, не во время кризиса.** Выявленный gap (отсутствие native TLS) — лучше закрыть прицельно сейчас чем спотыкаться о него позже.
6. **Стабильный контракт — переменная реализация.** Когда выбираем механизм для будущего расширения — используем тот что уже работает в полном масштабе (стандартный аппаратный механизм процессора), а не упрощённую версию которую потом переделывать.
7. **Узкие инфраструктурные подзадачи между phases.** Если выявлен prerequisite который не входит ни в текущую ни в следующую phase — оформляется как отдельная маленькая подзадача (X.5) с чётким scope. Лучше чем растягивать current phase или отодвигать next phase.