# step028

Дата: 2026-04-18  
Статус: завершён (стадия 1 SUPER-1a)

## Цель

Начать движение по `plan.md` — SUPER-1a (std BCL surface без массивов).

1. Переименовать `OS_0.1` → `OS` — избавиться от версии в имени проекта.
2. Стадия 1 SUPER-1a: вынести логику преобразования чисел в строку из inline-кода `Console.cs` в общий `std/` слой. Приложения впервые получают возможность строить строки с числами без кастомных stackalloc-буферов.

---

## Что сделано

### 1. Rename `OS_0.1` → `OS`

**Файлы/папки:**
- `OS_0.1/` → `OS/`
- `OS_0.1/OS_0.1.csproj` → `OS/OS.csproj`
- Git зафиксировал 78 файлов как rename (R), история сохранена.

**Референсы обновлены:**
- `OS.sln` — путь к проекту + имя
- `OS/OS.csproj` — `IlcSystemModule`
- `run_build.ps1` — `$efiProjectDir`, `$projectFile`, сообщение "Building OS", имя `.exe`
- `build_fetch_wsl.ps1`, `build_launcher_wsl.ps1`, `build_media_xorriso.ps1` — пути к `.qemu/esp`
- `README.md`, `std/no-runtime/README.md` — описание структуры

Исторические `done/step*.md` не трогаем — они фиксируют состояние на момент своего шага.

### 2. NumberFormatting в std/

**Создан `std/no-runtime/shared/NumberFormatting.cs`** — `public static unsafe class NumberFormatting` в namespace `SharpOS.Std.NoRuntime` с методами:

| Метод | Диапазон |
|---|---|
| `UIntToString(uint)` | 0 … 4 294 967 295 |
| `IntToString(int)` | -2 147 483 648 … 2 147 483 647 |
| `ULongToString(ulong)` | 0 … 18 446 744 073 709 551 615 |
| `LongToString(long)` | -9 223 372 036 854 775 808 … max long |
| `UIntToHex(uint, int minDigits)` | до 8 hex-цифр, padding нулями слева |
| `ULongToHex(ulong, int minDigits)` | до 16 hex-цифр, padding нулями слева |

**Edge cases:**
- `0` → литерал `"0"` (без аллокации).
- `int.MinValue` (-2147483648) → литерал — нельзя взять `(uint)(-value)` из-за overflow.
- `long.MinValue` — то же, через `unchecked((long)0x8000000000000000UL)` для сравнения.
- Защита от отказа аллокации: если `FastAllocateString(n).Length != n` → возвращаем полученную строку без заполнения.

**Реализация:**
1. Подсчитать количество цифр (decimal или hex).
2. Аллоцировать строку нужной длины через `StringRuntime.FastAllocateString(n)`.
3. Заполнить справа-налево через `fixed (char* dst = &result.GetPinnableReference())`.

### 3. Подключение в csproj

Во все три проекта добавлена строка:
```xml
<Compile Include="..\..\std\no-runtime\shared\NumberFormatting.cs" Link="..." />
```

- `OS/OS.csproj`
- `apps/HelloSharpFs/HelloSharpFs.csproj`
- `apps/FetchApp/FetchApp.csproj`

### 4. Smoke-test

В `FetchApp/Program.cs` временно добавлялся блок:
```
NumberFormatting:
  UIntToString(42)         = 42
  IntToString(-2147483648) = -2147483648
  ULongToHex(0xDEADBEEF,8) = DEADBEEF
  LongToString(-123456789) = -123456789
```

Запуск в QEMU strict-nx OVMF → все четыре значения корректные. Smoke-test убран после подтверждения.

---

## Работает / не работает

| Контекст | Статус |
|---|---|
| `NumberFormatting.*` в приложении (после `ManagedStartup` + `RhNewString`) | **работает**, подтверждено визуально |
| `NumberFormatting.*` в ядре | возвращает `string.Empty` — `StringRuntime.Fallback` в `OS.csproj` всегда возвращает Empty. Это ожидаемо до стадии 2 SUPER-1a (kernel string allocation через `KernelHeap`). |
| Console.WriteUInt/WriteULong/WriteHex в ядре | без изменений, inline stackalloc — пока не мигрируем, ждём стадию 2. |

---

## Архитектурные замечания

- **Дублирование логики:** `Console.WriteUInt` в ядре и `NumberFormatting.UIntToString` в std/ сейчас содержат одинаковый алгоритм подсчёта цифр и заполнения. Дублирование временное — снимется на стадии 2 SUPER-1a, когда ядро получит реальную аллокацию строк и сможет использовать `NumberFormatting` напрямую.
- **std/ shared-инвариант:** файл `NumberFormatting.cs` подключается во все проекты одинаково. В ядре работа через `FastAllocateString` возвращает Empty, но код совместим — компилируется и не крашит.
- **Rename:** проект теперь называется просто `OS`, без версии. Версионирование ушло в `SystemBanner.BuildId` и `step*.md`.

---

---

## Стадия 2 SUPER-1a: kernel string allocation

### Цель

Научить ядро реально аллоцировать строки — чтобы `NumberFormatting` работал в ядре (а не возвращал `Empty` из-за `StringRuntime.Fallback`). Сделать ядро полноценным потребителем общего `std/`.

### Что сделано

**1. Расширение kernel `MinimalRuntime.cs`**

Добавлены типы, отсутствующие в ядре:
- `Internal.Runtime.MethodTable` — пустой struct-маркер для MT указателей
- `System.EETypePtr` — wrapper для MethodTable указателя, с `[Intrinsic] EETypePtrOf<T>()` (не используется в финальном решении, но нужен для полноты контракта)
- `System.AttributeTargets.Method/Constructor` — значения для использования в attribute declarations
- `System.Runtime.CompilerServices.MethodImplOptions`, `MethodImplAttribute`, `IntrinsicAttribute` — инфраструктура для `[Intrinsic]`/`[MethodImpl]`

**2. `std/no-runtime/shared/StringRuntime.KernelHeap.cs`**

Новая реализация `FastAllocateString` для kernel pipeline:
```
Layout строки:
  offset 0  : MethodTable* (8 bytes)
  offset 8  : int Length (4 bytes)
  offset 12 : char[] (2 * (Length+1) bytes, null-terminated)

Total: 12 + 2*(length+1) bytes, выделяется через KernelHeap.Alloc.
```

**Ключевое решение:** MethodTable указатель извлекается из существующего литерала `string.Empty`:
```
fixed (char* emptyChars = string.Empty)
{
    methodTable = *(void**)((byte*)emptyChars - 12);
}
```
Это надёжнее чем `[Intrinsic] EETypePtr.EETypePtrOf<string>()`, поведение которого в kernel win-x64 EFI_APPLICATION target непредсказуемо. Литерал `""` всегда есть в бинаре с корректным MT (frozen string в rdata).

**Guard `!KernelHeap.IsInitialized` → return `string.Empty`** — для ранне-boot сценария, когда `Console.WriteUInt` может быть вызван до `KernelHeap.Init`.

Линкуется в `OS.csproj` вместо `StringRuntime.Fallback.cs`.

**3. Smoke-test (временный, удалён после подтверждения)**

В `Kernel.cs` после `KernelHeap.Init` проверяли:
- `FastAllocateString(5).Length == 5`
- `fixed (char* p = s) { ... }` работает
- `Console.Write(s)` печатает записанные символы
- `NumberFormatting.UIntToString(42)` и `ULongToHex(0xDEADBEEF, 8)` возвращают корректные строки

Запуск в QEMU strict-nx OVMF — все тесты прошли.

**4. Миграция `Console.cs` + `HeapDiagnostics.cs`**

*Первая попытка:* заменил `Console.WriteUInt/ULong/Hex` на вызов `NumberFormatting`. Сломалось — `HeapDiagnostics.DumpBlocks` ушёл в бесконечную итерацию.

Причина: `DumpBlocks` проходит по linked-list блоков (`block → block.Next → ...`), а каждая печать числа теперь аллоцирует через `FastAllocateString → KernelHeap.Alloc`, создавая новый блок в том же list. Итерация никогда не останавливается.

*Финальное решение:*
- `Console.WriteInt/WriteUInt/WriteULong/WriteHex` — managed путь через `NumberFormatting`. При failed-allocation (ранний boot) автоматический fallback на `*Raw` версии.
- `Console.WriteUIntRaw/WriteULongRaw/WriteHexRaw/WriteIntRaw` — публичные stackalloc-only версии. Для кода, который обязан избегать heap аллокаций во время своей работы.
- `HeapDiagnostics.DumpSummary/DumpBlocks` — используют `*Raw` варианты явно, с комментарием почему.

### Работает / не работает

| Контекст | Статус |
|---|---|
| `NumberFormatting.*` в ядре после `KernelHeap.Init` | **работает**, подтверждено |
| `Console.WriteUInt` в раннем boot (до heap init) | **работает** через Raw fallback |
| `HeapDiagnostics.DumpBlocks` на заполненной heap | **работает**, итерация корректно завершается |
| `new string(char, n)`, `PadLeft/PadRight`, `Concat` в ядре | **работает** (через `FastAllocateString`) |
| Полный boot в QEMU strict-nx → лаунчер → HELLO/ABIINFO/HELLOCS | **работает**, регрессий нет |

### Архитектурные наблюдения

**Разделение "managed default vs stackalloc opt-out".**
Console публично даёт два API:
- `Write*` — managed, через std, использовать по умолчанию
- `Write*Raw` — stackalloc, opt-in для кода который не может аллоцировать

Это осознанная модель "ядро по умолчанию использует общий std, но low-level диагностика остаётся на ручном контроле". Специфическая проблема `HeapDiagnostics` (iterator invalidation при аллокации в цикле) решается на стороне потребителя, а не Console.

**MT из литерала `string.Empty` — надёжнее intrinsic'ов.**
`[Intrinsic] EETypePtrOf<T>()` требует поддержки NativeAOT компилятора, и в нестандартных target'ах (win-x64 EFI_APPLICATION) может не сработать. Литерал `""` — обычный frozen string в rdata-секции, NativeAOT всегда генерирует его MethodTable. Извлечение MT через `fixed (char* p = ""); *(void**)(p-12)` — детерминированно, не требует magic compiler behavior.

**Fallback через `string.Length > 0`.**
Вместо прямого `KernelHeap.IsInitialized` check в Console (что ломает HAL-слой layering), используется косвенный: если `NumberFormatting.UIntToString` вернул пустую строку — значит allocation не прошла, fallback на Raw. Эвристика надёжная: value==0 возвращает литерал "0" (length 1), любое другое корректно форматируется в непустую строку, Empty означает только failed allocation.

### Проверка

QEMU strict-nx OVMF: `.\run_build.ps1`:
- `memory regions: 98` — через Raw (heap не поднят)
- `heap blocks: 9 … heap blocks dump end` — Raw iteration, не зацикливается
- `pager table pages/spare: 1039/1` — managed путь, heap поднят
- HELLO, ABIINFO, HELLOCS запускаются, лаунчер работает

---

## Стадии 3+4 SUPER-1a: queries, char helpers, transforms

### Цель

Закрыть "без массивов" часть SUPER-1a: обычный C# код со строками (IndexOf, Contains, Trim, Substring, Replace, ToUpper/Lower) работает одинаково в приложениях и ядре.

### Что сделано

**1. `std/no-runtime/shared/CharHelpers.cs`** — static helpers:
- `IsDigit`, `IsLetter`, `IsLetterOrDigit`, `IsWhiteSpace`
- `ToUpperInvariant`, `ToLowerInvariant` — ASCII-only, non-ASCII проходит без изменений

Делать `char.IsDigit(c)` через partial struct Char в `System` не стал — потребовало бы переделать примитивные structs в обоих MinimalRuntime.cs на partial. Доступно как `CharHelpers.IsDigit(c)`, BCL-обёртка — задача следующего PR.

**2. `std/no-runtime/shared/StringQueries.cs`** — без аллокации:
- `IndexOf(char)`, `IndexOf(char, startIndex)`, `IndexOf(string)`, `IndexOf(string, startIndex)`
- `LastIndexOf(char)`, `LastIndexOf(string)`
- `Contains(char)`, `Contains(string)`
- `StartsWith(string)`, `EndsWith(string)`
- `IsNullOrEmpty`, `IsNullOrWhiteSpace`

Подстрочный поиск — наивный O(n·m), без KMP; для текущих нужд достаточно.

**3. `std/no-runtime/shared/StringTransforms.cs`** — через `FastAllocateString`:
- `Substring(startIndex)`, `Substring(startIndex, length)`
- `Trim`, `TrimStart`, `TrimEnd` (whitespace через `CharHelpers`)
- `Replace(char, char)` — same-length fast path, возвращает `str` если символа нет
- `Replace(string, string)` — два прохода: считаем вхождения → аллоцируем результат → заполняем
- `ToUpperInvariant`, `ToLowerInvariant` — посимвольно

Short-circuit: если результат совпадает с исходной строкой — возвращается исходная ссылка без аллокации.

**4. Пробросы в `SystemString.cs`**

Instance методы на `String`:
- Queries: `IndexOf`, `LastIndexOf`, `Contains`, `StartsWith`, `EndsWith`
- Static queries: `String.IsNullOrEmpty`, `String.IsNullOrWhiteSpace`
- Transforms: `Substring`, `Trim`, `TrimStart`, `TrimEnd`, `Replace`, `ToUpperInvariant`, `ToLowerInvariant`

Клиентский код пишется как обычный C#: `s.Trim().ToUpperInvariant()`, `if (path.StartsWith("\\EFI"))`, `var name = fileName.Substring(0, fileName.IndexOf('.'))`.

**5. Подключение в csproj**

`CharHelpers.cs`, `StringQueries.cs`, `StringTransforms.cs` добавлены в `OS.csproj`, `HelloSharpFs.csproj`, `FetchApp.csproj`.

### Smoke-test в FetchApp

Демо-блок после логотипа:
```
std demo:
  raw:        '  Hello, SharpOS!  '
  Trim:       'Hello, SharpOS!'
  Upper:      '  HELLO, SHARPOS!  '
  Lower:      '  hello, sharpos!  '
  Sub(2,5):   'Hello'
  Repl(','): '  Hello; SharpOS!  '
  Repl(Sharp->Dark): '  Hello, DarkOS!  '
  IndexOf(','):  7
  Contains('OS'): true
  StartsWith('  Hello'): true
```

Запуск в QEMU strict-nx OVMF — все значения корректные. Smoke-test оставлен как живая демонстрация.

### Работает

| API | Apps | Kernel |
|---|---|---|
| `CharHelpers.*` (IsDigit/IsLetter/IsWhiteSpace/ToUpper/ToLowerInvariant) | ✓ | ✓ |
| `s.IndexOf`, `LastIndexOf`, `Contains`, `StartsWith`, `EndsWith` | ✓ | ✓ |
| `String.IsNullOrEmpty`, `IsNullOrWhiteSpace` | ✓ | ✓ |
| `s.Substring`, `Trim`, `TrimStart`, `TrimEnd` | ✓ | ✓ (после heap init) |
| `s.Replace(char,char)`, `Replace(string,string)` | ✓ | ✓ (после heap init) |
| `s.ToUpperInvariant`, `ToLowerInvariant` | ✓ | ✓ (после heap init) |

---

## Итог step 28 / SUPER-1a

SUPER-1a полностью закрыта: std/ покрывает обычный C# код со строками и числами без массивов.

Наработанные файлы в `std/no-runtime/shared/`:
- `NumberFormatting.cs` (int/uint/long/ulong → string, hex)
- `CharHelpers.cs` (ASCII char classification + case)
- `StringQueries.cs` (IndexOf/Contains/StartsWith/...)
- `StringTransforms.cs` (Substring/Trim/Replace/ToUpper/Lower)
- `StringRuntime.KernelHeap.cs` (kernel string allocation)
- `SystemString.cs` пополнен instance-методами

Ядро стало полноценным потребителем общего std — всё что работает в приложениях, работает и в ядре (с guard на ранний boot в `Console.*`).

### Что дальше

SUPER-1b (StringBuilder, Split, Join с массивами) требует массивов → SUPER-3 (managed collections) → SUPER-2 (managed heap extended).

Альтернатива — SUPER-4 (IDT / CPU exceptions) как параллельная ветка фундамента, не пересекается с std/.
