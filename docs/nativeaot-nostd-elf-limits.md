# NativeAOT + NoStdLib (ELF-app tier): карта ограничений

Живой документ. Перечень того, что **доступно / не доступно** для **ELF-приложений** в SharpOS: managed C# код в `apps/`, скомпилированный NativeAOT-ом в self-contained ELF (`HELLO.ELF`, `HELLOCS.ELF`, `ABIINFO.ELF`, `MARKER.ELF`, `FETCH.ELF`).

**Область применения:** только `apps/` дерево. Если ищешь ограничения **самого ядра** — см. [`nativeaot-nostd-kernel-limits.md`](nativeaot-nostd-kernel-limits.md). Если про **stock CoreCLR-hosted** managed-код через форк-runtime — см. [`coreclr-hosted-limits.md`](coreclr-hosted-limits.md). Общая компаративная таблица — в [`README.md`](../README.md).

ELF-приложения наследуют **все** ограничения kernel tier'а (тот же NativeAOT 7.0.20 + `NoStdLib=true`, тот же `MinimalRuntime`, та же отсутствующая полная BCL), **плюс** дополнительные — потому что они **не имеют прямого доступа к ядру**. Всё взаимодействие с ОС идёт через **`AppServiceTable`** — указатели функций ядра, передаваемые приложению при старте через `AppStartupBlock`. Это узкая, версионированная, явно объявленная поверхность.

**Конвенция** (синхронизирована с [`README.md`](../README.md) "Поверхности исполнения"):

- ✅ **Работает** — доказано прогоном launcher'а.
- 🟡 **Через AppService API** — функционал есть, но только через сервисную таблицу (не прямой kernel-side доступ).
- ⏳ **Отложено** — функционал есть в ядре, но не экспонирован в AppService и/или ABI ещё не подняли. Делать когда понадобится.
- 🔴 **Пока что отсутствует** — код не написан или сломан, но архитектурно достижимо.
- 🚫 **Архитектурно невозможно** — by-design ограничение NativeAOT-tier'а или unikernel модели; для функционала нужен другой execution tier.

---

## 0. Стартовый контракт

ELF получает `AppStartupBlock` через первый аргумент entry function (Win64 ABI: RCX). Layout:

```csharp
struct AppStartupBlock {
    uint  AbiVersion;          // requested by app
    uint  Flags;
    ulong EntryPoint;
    ulong StackBase;
    ulong StackTop;
    ulong MarkerAddress;       // optional sentinel (validate kernel didn't mistake apps)
    ulong ServiceTableAddress; // → AppServiceTable*
    int   ExitCode;            // app writes its result here (alt path)
    int   Reserved;
}
```

Приложение через `AppRuntime.Init(startup)` парсит блок, прячет указатели в статиках, потом все вызовы — через `apps/sdk/AppHost.cs::AppHost.*`.

**Версионирование:** `AppServiceTable.AbiVersion` = `V1=1`, `V2=2`, `CurrentAbiVersion=2`. V2 добавил: `FileExists`, `ReadFile`, `ReadDirEntry`, `TryReadKey`, `RunApp`, `WriteChar`, `WriteBuildId`. Приложение объявляет ABI version в build-time `.abi` файле (см. `apps/HELLO.ELF.abi` → `SABI`), kernel валидирует совпадение перед запуском. Если приложение запрашивает версию старше чем в kernel — `AppServiceStatus.AbiVersionMismatch`.

---

## 1. I/O и логирование

| Функция | Статус | API |
|---|---|---|
| Write string to console | 🟡 AppService | `AppHost.WriteString(string text)` |
| Write integer (decimal) | 🟡 AppService | `AppHost.WriteUInt(uint)` / `WriteInt(int)` |
| Write integer (hex) | 🟡 AppService | `AppHost.WriteHex(ulong)` |
| Write single char | 🟡 AppService (V2) | `AppHost.WriteChar(char)` |
| Write build-id banner | 🟡 AppService (V2) | `AppHost.WriteBuildId()` |
| `Console.Read*` blocking input | 🔴 | пока нет shim'а; используй `TryReadKey` loop |

Записи идут через kernel-side `UiText.Write*` → пост-EBS subsystem (own UART + FbTty). Pre-EBS — через UEFI ConsoleOut (актуально только если пробовать ELF до `ExitBootServices`, что не canonical path).

---

## 2. Файловая система

| Функция | Статус | API |
|---|---|---|
| File exists check | 🟡 AppService | `AppHost.FileExists(path)` / `FileExistsEx(path)` |
| Read entire file into buffer | 🟡 AppService | `AppHost.TryReadFile(path, buffer, capacity, out bytesRead)` |
| Directory listing entry | 🟡 AppService (V2) | `AppHost.TryReadDirEntry(dirPath, index, nameBuffer, ...)` |
| Open / Close / Seek (random access) | 🔴 | модель «read-entire-file»; descriptor API не сделан |
| Write to file | 🔴 | RO-FAT32; нужен RW driver |
| File create / delete | 🔴 | то же |
| Streams / pipes | 🔴 | shim над AppSDK достижим |
| `System.IO.File.*` | 🔴 | BCL `File` не реализован; используй `AppHost.*` |

Путь — FAT-style (`\EFI\BOOT\HELLO.ELF`, `\SHARPOS\TEXTS\GREETING.TXT`). Регистронезависимый, разделитель `\`.

---

## 3. Запуск дочерних приложений

| Функция | Статус | API |
|---|---|---|
| Run child app (synchronous) | 🟡 AppService (V2) | `AppHost.TryRunApp(path, out exitCode)` |
| Run child + check ABI | 🟡 | `TryRunApp(path, appAbiVersion, serviceAbi, out exitCode)` |
| Run child asynchronously | 🔴 | требует kernel.Thread + per-process CR3 для concurrent ELF |
| Spawn без ожидания | 🔴 | то же |

Семантика: вызывающий приостанавливается, child грузится через kernel `ElfValidation.RunApp`, после `Exit` управление возвращается с exit code. Mapping window'а вызывающего временно снимается на время child (см. `ProcessManager.TrySuspendCurrentForNested`).

**Концурентность ELF'ов — НЕ ДОСТУПНА.** Все ELF'ы слинкованы с одним VA `0x400000`. Два одновременных — не уживутся в едином AS без per-process CR3. Кооперативный scheduler (`OS.Kernel.Threading.Scheduler`) есть в ядре, но ELF в нём не участвует. См. также §6 ниже.

---

## 4. Memory management

| Функция | Статус | API |
|---|---|---|
| `new object()` / `new T[]` | ✅ | kernel re-exports `RhpNewFast` / `RhpNewArray` / `RhNewString` — общий managed GC heap с ядром |
| `string` allocation | ✅ | через тот же `RhNewString` re-export |
| `Span<T>` / `Memory<T>` | ✅ | работает (managed slices) |
| `stackalloc T[N]` | ✅ | C# intrinsic |
| `Marshal.AllocHGlobal` / native heap | 🔴 | может быть прокинут через AppService при необходимости |
| Direct VA mapping / `Pager` / `VirtualAlloc` | 🚫 | ELF guest tier: by-design нет прямого paging-доступа |
| Free / dispose | ✅ | GC mark-and-sweep (общий с kernel'ным) |
| `IDisposable.Dispose` pattern | ✅ | regular C#, ничего не сломалось |

**Важно:** ELF делит managed heap с ядром. Если ELF аллоцирует много, это съедает kernel heap. Long-running ELF с утечками положит ядро. Acceptable для текущих демо-app'ов (`HELLO.ELF`, `FETCH.ELF` — суммарно килобайты).

---

## 5. Exceptions

| Функция | Статус | Notes |
|---|---|---|
| `throw new Exception(...)` | ✅ | через kernel EH (CallCatchFuncletShellcode, ExInfo chain) |
| `try/catch/finally` | ✅ | те же L1..L17 EH gates проверены kernel-side; для ELF — те же binary helpers |
| `throw;` (rethrow) | ✅ | gate L9 |
| Catch with filter `when` | ✅ | gate L11 |
| Hardware-fault → managed (`#PF` → `NullReferenceException`) | ✅ | gate L13 |
| `Exception.StackTrace` | ✅ | gate L14 |
| Multi-frame finally / stack trace | ✅ | gates L16/L17 |
| Cross-process exception propagation | 🔴 | child crash возвращает `AppServiceStatus`, не marshalled Exception |

EH stack полностью наследуется от kernel — никаких ELF-specific ограничений.

---

## 6. Threading

| Функция | Статус | Notes |
|---|---|---|
| `System.Threading.Thread.Start()` | ⏳ E8 | std/no-runtime `Thread` — single-thread stub; AppSDK bridge deferred |
| `Task.Run` / async-await | ⏳ | Roslyn yield (state machine) работает; полноценный async ещё не делали |
| `Interlocked.CompareExchange` | 🟡 | naive single-thread impl (см. `std/no-runtime/shared/Threading.cs`) |
| `lock { }` (Monitor) | ⏳ | нужны kernel hooks через AppService — отложено |
| `Mutex` / `Semaphore` / `Event` | ⏳ | в ядре есть (E5), в AppSDK не выставлено (E8 deferred) |
| `Sleep(ms)` | ⏳ | в ядре есть (`Scheduler.Sleep`), в AppSDK не выставлено |
| `Yield()` | ⏳ | то же |
| `Parallel.For` | 🔴 | будет работать поверх Threading API когда E8 landed |
| `PLINQ` | 🚫 | внутрення опирается на reflection (выпилено NativeAOT'ом) |
| `GetCurrentThreadId` | ⏳ | можно прокинуть через AppService но смысла мало (1 thread на ELF сегодня) |

**Phase E8 (ELF threading через AppSDK) — deferred per user direction** (NativeAOT-приложения могут не быть в production — основной prod tier это CoreCLR-hosted). Kernel-side инфраструктура готова (`kernel.Thread`, `Process`, `Scheduler`, `Event`, `Semaphore` — E1..E7 landed). Если ELF threading понадобится, AppSDK обзаведётся:

```csharp
AppHost.CreateThread(entry, arg) → threadId
AppHost.JoinThread(threadId, timeoutMs) → exitCode
AppHost.YieldThread()
AppHost.SleepThread(ms)
AppHost.CreateEvent(manualReset) → handle
AppHost.SetEvent(handle)
AppHost.WaitEvent(handle, timeoutMs)
```

См. план в `docs/threading-architecture.md §11`.

---

## 7. Reflection / Runtime introspection

| Функция | Статус | Notes |
|---|---|---|
| `typeof(T)` | ✅ | работает в NativeAOT |
| `T.GetType()` / `object.GetType()` | 🟡 | возвращает MethodTable*-derived информацию; не полный `Type` объект |
| `Type.GetMethod` / `Type.GetMembers` | 🚫 | metadata stripped at AOT build; fundamental |
| `Reflection.Emit` / dynamic IL | 🚫 | требует JIT (по определению нет в AOT) |
| `Activator.CreateInstance(Type)` | 🚫 | то же — нужен runtime constructor lookup через metadata |
| `dynamic` keyword / DLR / `Expression<T>.Compile()` | 🚫 | DLR строится поверх Reflection.Emit |
| `Type.GetType("Some.Class.Name")` | 🚫 | string→Type требует metadata table |
| `Type.IsAssignableFrom` | 🟡 | для конкретных типов работает (compile-time known); generic-cases — нет |
| `RuntimeHelpers.GetUninitializedObject` | 🚫 | нужна reflection для Type lookup |
| Attributes (declaration only) | ✅ | атрибуты компилируются |
| Reading attributes at runtime | 🚫 | metadata stripped at AOT build |

Reflection — главное ограничение NativeAOT. Если нужна runtime-introspection — иди в CoreCLR-hosted tier.

---

## 8. Generics / collections / LINQ

Полностью наследует kernel-tier'ные ограничения из `nativeaot-nostd-kernel-limits.md`. Кратко:

| Функция | Статус |
|---|---|
| `List<T>`, `Dictionary<K,V>`, `Queue<T>`, `Stack<T>` | ✅ ported в `std/no-runtime/shared/` |
| `IEnumerable<T>` / `IEnumerator<T>` (`yield return`) | ✅ (через Roslyn state machine; см. memory `roslyn-iterator-needs-`) |
| `IComparable<T>`, `IEquatable<T>` для primitives | ✅ |
| LINQ extension methods (`Where`, `Select`, `ToArray`) | 🟡 partial — то что не зовёт `IEnumerable<T>` через reflection |
| `string.Split` / `string.Replace` / `string.Contains` | ✅ (в `std/no-runtime/shared/StringAlgorithms.cs`) |
| `StringBuilder` | ✅ |
| `Encoding.UTF8.GetBytes/GetString` | 🟡 partial |

---

## 9. Input

| Функция | Статус | API |
|---|---|---|
| Read keystroke (non-blocking) | 🟡 AppService (V2) | `AppHost.TryReadKey(out KeyInfo)` |
| Read line (blocking) | 🔴 | trivial wrapper над `TryReadKey` loop + LineEditor pattern |
| Mouse / touch | 🔴 | нужны драйверы |
| Network input | 🔴 | нет NIC stack целиком |

`KeyInfo` содержит scan code + Unicode char + modifier flags.

---

## 10. Math / numerics

| Функция | Статус |
|---|---|
| `int`, `uint`, `long`, `ulong`, `double` арифметика | ✅ |
| `Math.Sqrt`, `Math.Abs` | ✅ (RyuJIT SSE intrinsics) |
| `Math.Pow`, `Math.Sin`, `Math.Cos`, `Math.Log`, `Math.Exp` | 🔴 trap-stub; нужен libm port (software impl или порт MS libm) |
| `BigInteger`, `Decimal` | 🔴 portable BCL impls достижимы |
| `Vector<T>`, `Vector128/256/512` | ⏳ XCR0=0x3, AVX off; SSE-only вектора возможны но не тестировались |
| Random number generator | 🟡 наивный custom (LCG/xorshift); `System.Random` не работает |

---

## 11. ABI стабильность и обратная совместимость

`AppServiceTable.AbiVersion` — главный stability контракт. Правила:

- **V1** — minimal surface (Write, Exit, GetAbiVersion).
- **V2** — V1 + file operations + key input + child process.
- Будущие версии — only **append**, не reorder, не remove существующих полей.
- Apps декларируют требуемую версию в `*.ELF.abi` файле (магия `SABI` + version uint).
- Kernel при запуске проверяет `request.AppAbiVersion <= AppServiceTable.CurrentAbiVersion`. Если новее — `AbiVersionMismatch` error.
- Backward-compat: apps требующие V1 запускаются на V2 kernel без проблем (kernel игнорирует unused V2 fields со своей стороны).

Add-only invariant прописан в `apps/sdk/AppServiceTable.cs` комментариях.

---

## Cross-references

- [`nativeaot-nostd-kernel-limits.md`](nativeaot-nostd-kernel-limits.md) — kernel tier ограничения (применимы И к ELF).
- [`coreclr-hosted-limits.md`](coreclr-hosted-limits.md) — третий tier (stock CoreCLR на форк-runtime).
- [`threading-architecture.md`](threading-architecture.md) — план Phase E, §11 про ELF threading.
- [`README.md`](../README.md) — общая компаративная таблица фичей по tier'ам.
- `apps/sdk/AppHost.cs` — фактический API.
- `apps/sdk/AppServiceTable.cs` — ABI structure + version constants.
- `apps/HELLO/Program.cs` etc. — примеры идиоматического использования.
