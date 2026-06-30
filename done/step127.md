# step127 — perf round 1 (FS Stat, dir cursor, probe compile-out, TLB elision)

## TL;DR

Пять перф-фиксов по диагнозу пользователя. Главные вины: (1) `GetFileAttributes`
читал **весь файл** ради проверки существования, (2) `FindNextFile` O(N²)
из-за пересканирования каталога на каждый индекс, (3) 728 chatty C++→managed
probe-вызовов даже при Verbose=false, (4) полный CR3 reload на каждый
4 KiB commit, (5) HPET MMIO read на каждом Yield даже когда никто не ждёт.

Все пять закрыты, ожидаемая суммарная экономия: десятки секунд на bootstrap
PS, многократное падение AHCI traffic, drop NativeArena с 256 MiB до
норм. `Math.Sqrt` остался блокирован CLM (отдельная задача).

## #1 — Fs.Stat

**Корень:** `FileSystemQuery.GetFileAttributes` (нативный handler PAL'овского
`GetFileAttributesW`) для пути с расширением вызывал `Platform.TryReadFile`,
который:

1. Probes размер.
2. Allocates буфер в NativeArena.
3. Читает файл **целиком**.
4. Возвращает буфер — а вызывающая сторона его выбрасывает не освобождая.

Проверка существования `System.Net.Http.dll` (12 MiB) читала всю DLL. PS
делает сотни таких probe'ов на bootstrap → MiB NativeArena утечки, лавина
AHCI команд, физический OOM на 2 GiB VM.

**Фикс:** Новая cheap-метаданная цепочка `Fs.Stat` → `Fat32.Stat`. Использует
существующий `Fat32.Resolve` который уже возвращает size + isDir без чтения
контента (walks dir entries в FAT, останавливается на match'е).

```csharp
Fs:  abstract bool Stat(string path, out uint size, out bool isDir);
Fat32Fs: override → Fat32.Stat
Fat32: public static bool Stat(...) => Resolve()
Platform.TryStat: dispatches Fs.Current.Stat; fallback to UEFI exists
FileSystemQuery.GetFileAttributes: Stat instead of TryReadFile
```

Файлы: `OS/src/Hal/Fs.cs`, `OS/src/Hal/Vfs.cs`, `OS/src/Hal/Fat32.cs`,
`OS/src/Hal/Platform.cs`, `OS/src/PAL/SharpOSHost/FileSystemQuery.cs`.

## #2 — Stateful directory enumeration

**Корень:** Fork-side `FindFirstFileW`/`FindNextFileW` и `NtQueryDirectory
File` вызывали `SharpOSHost_FindDirEntry(path, index, ...)` — index растёт
0,1,…,N-1. Каждый вызов `Fat32.EnumDir` сканировал каталог с НУЛЯ и
доходил до index — O(N²) на каталог. Папка с 200 DLL = ~20k повторных
LFN-распаковок и sector reads.

**Фикс:** Single-slot resumable cursor в `Fat32`. Сохраняем (path hash,
dirCluster, nextIndex, cluster, secInRoot, si, off, lfnLen) после каждого
`EnumDir`. Если следующий вызов идёт с тем же path и индексом NextIndex,
**возобновляем** из сохранённой позиции — O(1) на entry. Иначе fallback
к full scan (но через `ScanTo` — единый walker, без дублирования
логики). Sequential enumeration теперь O(N).

**Почему single-slot:** PS обходит один каталог за раз per cmdlet.
Multi-slot LRU был бы overkill; degradation graceful (если cache miss —
ровно прежнее поведение).

Файл: `OS/src/Hal/Fat32.cs`.

## #3 — Compile-out fork-стороны диагностики

**Корень:** `[LoadTypeKey_Body]` × 27,801 строк, `[LoadExactInterfaceMap]`
× 22,310 и т.д. (диагноз пользователя). Каждая запись ≈ 40 отдельных
`SharpOSHost_DebugPrint*` вызовов. Даже при `Verbose=false` каждый вызов
сначала переходит C++ → C ABI → managed kernel и только там видит флаг.
Сотни тысяч бессмысленных native→managed transitions.

**Фикс:**
- Новый общий заголовок `inc/sharpos_probes.h`:
  ```cpp
  #if defined(TARGET_SHARPOS) && !defined(SHARPOS_VERBOSE_PROBES)
    #define SHARPOS_QUIET_PROBES 1
  #endif
  #ifdef SHARPOS_QUIET_PROBES
    #define SharpOSHost_DebugPrint(s)    ((void)0)
    #define SharpOSHost_DebugPrintHex(v) ((void)0)
  #else
    void SharpOSHost_DebugPrint(const char*);
    void SharpOSHost_DebugPrintHex(uint64_t);
  #endif
  ```
- 18 файлов CoreCLR fork'а очищены от локальных `extern "C"` объявлений
  и теперь `#include "sharpos_probes.h"`.
- TARGET_SHARPOS дефолт = quiet. Opt-in verbosity = `-DSHARPOS_VERBOSE_PROBES`
  при сборке.

Skipped 3 файла корректно:
- `crt_imp_stubs.cpp` / `winapi_shim.cpp` — содержат weak fallback
  **definitions** (нужны для линковки kernel-less сборки).
- `dllimport.cpp` — использует `DebugPrintForced` (не из этого набора).

`DebugPrintForced` сохранён — нужен для panic/forced trace, не cluttering.

Файлы: `dotnet-runtime-sharpos/src/coreclr/inc/sharpos_probes.h`,
18 файлов в `vm/`, `jit/`, `utilcode/`.

## #4 — Убрать CR3 reload с каждого commit

**Корень:** `VirtualMemory.Commit` / `TryDemandCommit` / `MapFixed` после
каждой группы page-mappings вызывали `X64PageTable.FlushTlbAll()` →
полный CR3 reload (≈ 100 циклов на QEMU). Пользователь: 5,825 commits
за PS bootstrap = 5,825 CR3 reloads.

**Фикс:** Все три hot-path функции мапают только not-present → Present
PTE (skip already-mapped через `TryQueryKernel`). x86 не кэширует
not-present entries в TLB, поэтому первый access после мап-операции
просто fetches новую PTE — flush не нужен. Удалены `FlushTlbAll()` из:

- `Commit` (5,825 calls/bootstrap)
- `TryDemandCommit` (#PF demand path)
- `MapFixed` (PE sections / MMIO)
- Commit-fail paths (тоже не нужны)

`FlushTlbAll` оставлен только в `Decommit` (Present → not-present:
надо инвалидировать) и `Protect` (изменение прав: PTE flags меняются).
INVLPG shellcode — отдельная задача, оба case'а редкие.

Файл: `OS/src/Kernel/Memory/VirtualMemory.cs`.

## #5 — Wait/yield perf

**Корень:** Каждый `Scheduler.Yield()` (а они происходят 10⁵+ за PS
bootstrap) вызывает `DrainExpiredTimers` который читает HPET MMIO (~1us
на QEMU). Если никто не parking'ует deadline, эта read бесполезна.

**Фикс:** `TimerQueue.HasPending` cheap predicate. `DrainExpiredTimers`
short-circuit'ит до HPET read когда queue пуст.

**Что НЕ сделано (отложено):**

- **Idle-spin HLT.** Текущий путь (когда current Waiting и runqueue
  пуст) — `while (next == null) { DrainExpiredTimers(); next = DequeueRunnable(); }`.
  Нужен HLT с IRQ-driven wake (HPET interrupt → ISR → DrainExpired →
  reschedule). Это новый infra (HPET в interrupt mode + IDT slot +
  ISR), отдельный шаг.
- **WaitAny event-driven.** Сейчас при waitAny infinite режиме
  опрашивает каждый handle с poll, потом Yield, повтор. Правильно —
  регистрировать calling thread в wait list **каждого** handle, на
  Set первого размотать остальные. Сложно, отложено.
- **Finite-timeout HPET throttling.** Можно читать HPET каждые 64
  Yield'а вместо каждого. Маленький выигрыш, отложен.

Файлы: `OS/src/Kernel/Threading/TimerQueue.cs`,
`OS/src/Kernel/Threading/Scheduler.cs`.

## Что отложено

- **Math.Sqrt / CLM** — отдельный шаг (см. step126.md §"не починили").
- **Idle HLT** — нужен IRQ-driven wake, новая инфраструктура.
- **WaitAny event-driven, INVLPG shellcode, finite-timeout throttle** —
  меньший выигрыш, бэклог.
- **R2R / tier promotion** — пользователь правильно отметил, что
  Linux BCL у нас без R2R, cold start действительно JIT-компилирует
  огромный объём. Это отдельный (большой) фронт.

## Next step

step128 — измерения. Воткнуть Stopwatch breakdown в boot pipeline,
зафиксировать "до" / "после" этого шага по реальным цифрам. Без
бенчмарка дальше идти вслепую.
