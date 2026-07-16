# Step 141 — ManagedDoom компилится на app-std: апгрейд app-рантайма до full-std

## Контекст

Цель — DOOM на SharpOS. Вендорен ManagedDoom (GPL-2.0, изолирован в
`apps_native/GPL_AHEAD_WARNING_DOOM_managed/`; Silk/GL/DrippyAL-платформа
вырезана, ядро + software renderer + Null-аудио/инпут оставлены). P1-проба
(step 140 хвост) дала карту гэпов: **104 ошибки компиляции**, из них ~70 — «файл
уже есть в std, но app-props его не включает».

Корневая асимметрия: ядро (OS.csproj) компилит весь std/no-runtime/shared, а
app-SDK (FreestandingPe.props, step 138) — только минимальный срез, и app-копия
MinimalRuntime несла стабы (`Delegate { }`, `Array` без partial, пустые
IntPtr/Single/Double/Nullable). DOOM-у нужен почти полный std.

## Что сделано

### Раунд 1 — app-рантайм до kernel-парности (104 → 19 ошибок)

- **`apps_native/sdk/MinimalRuntime.cs`** доведён до зеркала kernel-версии:
  полные `IntPtr`/`UIntPtr` (операторы/конверсии), `Single`/`Double` с
  рекурсивным `_value` (box-size, та же мина что step 131), полный
  `Nullable<T>`, `EETypePtr` с операторами, `Object.GetEETypePtr()`,
  pointer-slot'ы в Runtime*Handle, полный `AttributeTargets`,
  `RuntimeHelpers.CreateSpan` + `RuntimeFeature.ByRefFields`,
  `MethodImplOptions.NoInlining`, `RuntimeImports` → partial. Стабы
  `Delegate`/`MulticastDelegate` **удалены** — приходят из std.
- **`FreestandingPe.props`** — std-набор теперь зеркалит OS.csproj: Runtime/
  (Delegate, MulticastDelegate, ActionFunc, Span/ReadOnlySpan/Unsafe/Buffer/
  MemoryMarshal/MemoryExtensions, Array, GC, Math, ClassConstructorRunner,
  FunctionPointerOps, ExceptionIDs, атрибуты), Text/ (StringBuilder, Encoding),
  Linq/Enumerable, полный Bcl/ (Comparer, HashSet, Stack, Queue, LinkedList,
  ReadOnly*, ArraySegment, SortedList, Guid, Path, DebuggerAttributes),
  string-семейство целиком, Threading. Tier-swaps остались:
  GcMemorySource.AppStatic, StringRuntime.RhNewString, ThrowHelpers inline.
- **Diagnostics split**: std `Debug` больше не тянет OS.Hal напрямую — вывод
  через `DebugOutput` backend (`Diagnostics.Output.KernelConsole.cs` для ядра,
  `sdk/DebugOutput.AppHost.cs` для апп; паттерн GcMemorySource).

### Раунд 2 — System.IO

- std: `IO/Stream.cs` (порт v8.0.27-шейпа: Read/Write/Seek/ReadExactly +
  SeekOrigin/FileMode/FileAccess), `IO/IOException.cs`, `IO/MemoryStream.cs`
  (RO-надбуферный), `IO/StreamReader.cs` (ReadLine, Latin-1).
- sdk: `FileSystem.AppHost.cs` — `FileStream` (чтение = whole-file load через
  `AppHost.TryReadFile` c grow-retry на BufferTooSmall — у сервиса нет
  offset-протокола; запись = in-memory discard, write-сервиса нет),
  `File.{Exists,ReadAllBytes,ReadAllLines,ReadLines}`, `Directory`,
  `StreamWriter`-заглушка.

### Раунд 3 + превентивная волна 2

Roslyn давит каскадные CS0103 в файлах с битым `using` — вторая волна снята
до прогона API-цензом по компилируемому срезу: `Tuple<T1,T2>` (+Create),
`ExceptionDispatchInfo` (без сохранения стека — задокументировано),
`TimeSpan` (минимальный), `Numerics/Vector3`, `Console`→AppHost,
Process/Stopwatch/DateTime-стабы (sdk), `BitConverter.ToInt16/32/64`,
`Path.GetFileNameWithoutExtension`, `string.Join(IEnumerable<string>)`,
`ToLower/ToUpper`, `int.Parse/TryParse` (`NumberParsing.cs`), **ToString у всех
примитивов** (иначе `"lump " + n` печатает пусто — constrained-call на
Object.ToString → null), **managed double-Math** (`Runtime/Math.Double.cs`).

### Managed double-Math (без libm)

Round (half-to-even) / Floor / Sin / Cos (редукция + Taylor x^17, ~2e-9) /
Log (IEEE-биты + atanh-ряд) / Exp (k·ln2 + Taylor) / Pow (Exp∘Log + edge-cases)
+ MathF-шимы. Два жёстких ограничения зашиты в дизайн: **никакого `%` по
double** (ILC лоурит в DblRem-хелпер, которого у нас нет) — редукция через
truncating division; диапазон |x| < 2^63. `double.IsNaN/IsInfinity` —
бит-проверки в обоих MinimalRuntime.

### Волна 3 (после первой зелёной AOT-батареи)

- `String : IEnumerable<char>` + `CharEnumerator`
  (`SystemString.Enumerator.cs`) — LINQ-по-строке (`s.Last()`,
  `s.FirstOrDefault()`) компилится **и работает**: string не массив, интерфейс
  в DispatchMap.
- `[IndexerName("Chars")]` на индексаторе string — `foreach (char c in str)`
  лоурится в `Length`+`get_Chars` (CS0656 без него).
- `Dictionary.TryAdd`, `List.Reverse()`, `Enumerable.Max/Min(selector)`,
  `string.ToCharArray()`.
- `Int32.ToString(format)` / `Double.ToString(format)` → NumberFormatting:
  zero-pad ("00"), `D<n>`, fixed-point double ("0.0", NaN/Inf-safe — Fps при
  нулевом Stopwatch = Infinity).
- Vendor-патчи (2 шт., минимальные): non-generic `IEnumerable.GetEnumerator`
  в {Dummy,}FlatLookup делегирует в generic (BCL-паттерн; у нашего Array нет
  `GetEnumerator()`); dummy `Main()` в Program.cs (csc-требование для Exe).

### Деплой

`build_doom.ps1` — из compile-пробы в стандартный враппер над
build_launcher.ps1; run_build.ps1 стейджит `DoomApp.exe` → ESP
`\EFI\BOOT\DOOM.EXE` + `.abi` (SABI v2, ServiceAbi win64). Лаунчер
(HelloSharpFs) листит `*.EXE` динамически — DOOM появился без правок.

## Результат

- DoomApp компилится и линкуется в freestanding PE: **104 → 0 ошибок**.
- Полный цикл: лаунчер → DOOM.EXE → `runapp abi source=manifest` → PeLoader →
  `doom: P1 stub entry` → exit 0 → возврат в лаунчер. ✅
- Регрессий нет: AOT-батарея 20/20, CoreCLR-hosted exitCode 42, census
  OK=145 DEG=2 FAIL=20, EBS live.
- Ядро тронуто аддитивно (OS.csproj +4 std-файла; MinimalRuntime: ToString/
  Parse на примитивах, IsNaN/ToString(format) на Double) — прогнано в той же
  батарее.

## Уроки

1. **Roslyn давит каскадные ошибки в файлах с битым using** — карта гэпов из
   одного прогона неполна; превентивный API-ценз по срезу (grep по вызовам)
   снимает следующую волну до сборки.
2. **`foreach` по string ≠ enumerator**: жёсткое требование `get_Chars` по
   имени; string-индексатору в custom-corlib обязателен `[IndexerName]`.
3. **string vs массив для LINQ**: интерфейс на классе → DispatchMap → работает;
   у массивов NumInterfaces=0 → только компилится. Разные судьбы одного
   `IEnumerable<T>`.
4. **`%` по double — скрытый runtime-хелпер** (DblRem): в freestanding-математике
   редукция только через truncating division.
5. Дуал-копия MinimalRuntime (kernel/app) при таком апгрейде дублирует ~500
   строк — кандидат на вынос общей System-секции в shared-файл (отложено,
   обсуждено — blast radius решили не расширять в этом step'е).

## Известные мины (рантайм, следующие steps)

- **App-heap 1 MB** (GcMemorySource.AppStatic) против ~4-12 MB WAD — растить
  пул или heap-сервис от ядра.
- **Read-сервис без offset** — FileStream грузит файл целиком; для WAD нужен
  range-протокол или большой heap.
- **`static readonly`-таблицы DoomInfo** (States/MobjInfos/SwitchNames…) —
  cctor-trap §1; проверит первый реальный запуск.
- **LINQ по массивам** — компилится, в рантайме iface-resolve падает (§4);
  в DOOM такие сайты есть (`iwadNames.Contains`, `.Select` по массивам).
- Stopwatch/DateTime — нули до тик/RTC-сервиса (P3, нужен для game loop).
- Config/savegame-записи уходят в discard (нет write-сервиса).

## Next

P2: реальный вход DoomApp (CommandLineArgs → ConfigUtilities → Wad(DOOM1.WAD с
ESP)) + heap-рост + видео-blit (GOP framebuffer из сервис-таблицы), затем P3
ввод (PS/2 → DoomEvent) и тайминг.
