# step 186 — подготовка к трубам: обменная куча, учёт ресурсов, аргументы, генератор, BCL-форматирование

Продолжение step185 по `pipe_plan.md`: закрыты оставшиеся пункты «Починить»
(6–8) и весь раздел «Подготовить под трубы» (1–7). Каждый пункт проверен
прогоном QEMU `-UsbOnly -SkipCoreClr -Autorun` (`SHARPOS_GUI=1`), сборки и
прогоны в этом шаге запускались из сессии по разрешению пользователя.

Правило шага (сказано пользователем по ходу): под каноническим именем `System.*`
— только совместимая реализация, недостающее в std добивается переносом из
BCL, источник — **`gc-experiment/dotnet-runtime-8.0`** (v8.0.27), тесты — и в
ядро, не только в `AotTests`.

## «Починить» 6–8

**6. Приведение массивов.** `RhTypeCast_*` ходили по `GetBaseType()`, а у
массива это тип ЭЛЕМЕНТА: `string[] is string`, `int[] is ValueType` — «да»,
ни один массив не был `Array`, `int[]`→`uint[]`, `Derived[]`→`Base[]` — «нет».
Перенесён `TypeCast.AreTypesAssignableInternalUncached` (8.0): ковариантность
ссылочных элементов, целые одного размера и enum, массив — это `Object`/`Array`
(`IsSystemObject`/`IsValidArrayBaseType` по коду типа, не по указателю — верно и
для объекта чужого образа). `RhpLdelemaRef` — null, границы, точный тип.
Добавлены `RhTypeCast_CheckCastInterface/CheckCastClass` (их попросил ILC для
обработчика интерполяции).

**7. Запись файлов в приложениях** (`FileStream` на запись,
`File.WriteAllText/WriteAllBytes`, `StreamWriter`) бросает `IOException`:
службы записи нет, а раньше данные молча выбрасывались. Следствие: сохранение
игры в DOOM и состояния в Fami (без `try`) завершает программу с 134 вместо
ложного «сохранено» — пользователь: «не главное сейчас».

**8. ABI.** `AppStartupBlock.CurrentAbiVersion` в SDK стоял на V2 →
`AppServiceTable.CurrentAbiVersion`; `AppRuntime.Initialize` сверяет версию
таблицы и при несовпадении выходит с сообщением.

## «Подготовить под трубы» 1–7

1. **Обменная куча** `OS/src/Kernel/Memory/ExchangeHeap.cs` — память вне куч
   всех сборщиков: куски по 64 КиБ из `PhysicalMemory`, тождественно
   отображённые, только ниже 4 ГиБ (с `0x1_0000_0000` — окно образов
   приложений); классы 64 Б–32 КиБ с пулами, крупные — сериями страниц;
   заголовок с владельцем; чужой указатель отказывается без записи.
2. **Учёт ресурсов процесса** `ProcessResources` по поколению: `OnAppStarted`
   при `EnterApp`, `OnAppEnded` в `EndAppRun` (блоки владельца — обратно, состояние
   `Exited`), `MarkFailed` из ловушки step185.
3. **Данные при старте** `StartupData`: блок записей «вид, длина, байты» под
   стартовым блоком на стеке ребёнка, адрес в `AppServiceTable.StartupDataAddress`;
   вид 1 — аргумент, концы труб станут видом 2. `RunApp` читает аргументы из
   запроса (`FlagHasArguments`) пока родитель отображён. `AppHost.Arguments`,
   `TryRunApp(path, string[])`; оболочка передаёт аргументы `.EXE`.
4. **Генератор для всех образов**: `generators/SharpOS.Generators` через
   `Std.props`; первый генератор — `SharpOS.Generated.ImageInfo`. Приложения на
   C# 14.0, как ядро.
5. **`BinaryWriter`/`BinaryReader`** — перенос из 8.0, включая посимвольное
   чтение (без `Decoder`: длина символа по кодировке); `ReadString` берёт байты
   кусками и не верит длине из данных. `Stream.Read(Span)/Write(ReadOnlySpan)`,
   `Stream.Null`, `BinaryWriter.Null`, `BinaryPrimitives.Read/Write{Single,Double}`.
   Копии в Fami/TriCNES удалены.
6. **Выделение памяти из прерывания** — детектор в аллокаторе
   (`GcHeap.s_allocationAllowed`), в итоге пакета: 0.
7. **Форматирование** — из 8.0: `Number.Formatting` (обобщённый по `TChar`,
   UTF-16 и UTF-8), Dragon4, Grisu3, `NumberFormatInfo`, `BitOperations`,
   `ISpanFormattable`, `IUtf8SpanFormattable`; примитивы реализуют их (общий
   `std-no-runtime/Number/Primitives.Formatting.cs`, `partial` в обоих
   `MinimalRuntime`); `DefaultInterpolatedStringHandler`, `ICustomFormatter`,
   `string.Create(provider, …)`, `new string(ReadOnlySpan<char>)`, `System.Index`.
   Давняя красная проба `string.Format` — зелёная. Вырезано: `decimal`, `Half`,
   `Int128`, аппаратные ветки `BitOperations`, обобщённая арифметика
   (`INumberBase`), `Rune`, ветка enum в обработчике.

## Корни, найденные прогонами

- **Хук аллокатора до материализации статиков.** `s_allocationAllowed`
  (читает `Scheduler.Current`) поставленный рядом с хуком сборки сработал на
  выделениях самого материализатора, до статиков — и падал сам QEMU
  (`-1073741819`, дважды подряд). Перенесён за `GcStaticsMaterializer`.
- **Оболочка читала скрипт как Latin-1** (`(char)buffer[i]`): кириллица в
  аргументе приходила дважды закодированной. Строка теперь декодируется UTF-8.
- **ILC сворачивает `x is Base[]`** в точное сравнение таблицы, если наследники
  `Base` нигде не создаются (найдено дизассемблированием `AotTests.exe`). Тест
  был неудачный; в пробах кладётся настоящий объект.
- **`new string(ReadOnlySpan<char>)`** не собирался: у конструктора не было
  парного статического `Ctor` для ILC.
- **`Dragon4`** — кадр больше страницы: ядру нужен `RhpStackProbe` (заглушка,
  патчер делает `ret`, как `__chkstk`).

## Прогоны (последний)

- ядро: 51 проверка `std:` ok (приведение массивов, `BinaryWriter/Reader`,
  `Stream.Null`, интерполяция, `string.Create`, форматы целых/вещественных,
  UTF-8 `TryFormat`); 7 проверок обменной кучи ok; `string.Format … ok`;
  `[proc] generation N exited: exchange blocks returned 1` на каждом вложенном
  запуске; `kernel allocations inside interrupts: 0`; красные — прежний набор;
- `AotTests` 120/120 дважды, `expect 3 … --echo-args 'two words' третий`,
  `[sh] done ran=4 failed=0`.

## Уроки

- **Источник — проверять до переноса.** `gc-experiment/dotnet-runtime` — это
  release/7.0; часть переносов сделана оттуда с заголовком «release/8.0».
  Сверено и исправлено; 8.0 лежит в `gc-experiment/dotnet-runtime-8.0`.
- **Хук, который вызывается на каждом выделении, — ранний код.** Всё, что он
  читает, должно существовать до статиков.
- **Проба с несуществующими наследниками проверяет ILC, а не рантайм.**
- **Неполное — жёлтое.** Перед коммитом пользователь вернул ✅ на 🟡 там, где
  есть вырезы или оговорки: `string.Format`/форматирование, «HW-fault → managed»
  (`#UD`, рабочие потоки), разделы limits step185 (`stelem`, приведение массивов,
  точный обход, вытеснение — только QEMU, один процессор).

## Хвосты (канонические имена, не вполне совместимые)

`System.Type` — только идентичность (`typeof`); `MemoryMarshal.Write` принимает
значение, а не `in`; нет `ArgumentOutOfRangeException.ThrowIfNegative<T>` (нужна
обобщённая арифметика); форматирование читает статик `NumberFormatInfo` и не
годится до материализации статиков (кроме неотрицательных целых без формата).

## Файлы

- Ядро: `OS/src/Kernel/Memory/ExchangeHeap.cs`, `Process/{ProcessResources,StartupData}.cs`
  (новые); `Process/{AppServiceBuilder,AppServiceTable,LauncherBoot,ProcessStartupBuilder}.cs`,
  `Exec/JumpStub.cs`, `Boot/{BootSequence,MinimalRuntime}.cs`,
  `PAL/SharpOSHost/{ChkstkStub,ChkstkPatcher}.cs`;
  `Diagnostics/{ExchangeHeapProbe,StdSurfaceProbe}.cs` (новые), `NativeAotProbe.cs`, `Probes.cs`.
- std: `Number/` (новая папка, перенос 8.0), `Runtime/CompilerServices/` (обработчик
  интерполяции), `IO/{BinaryWriter,BinaryReader}.cs`, `Bcl/{ICustomFormatter,Index}.cs`;
  `GC/{GcHeap,GcRuntimeExports}.cs`, `IO/Stream.cs`, `Text/Encoding.cs`,
  `SystemString*.cs`, `Bcl/{BinaryPrimitives.*,BitConverter}.cs`, `Globalization*.cs`,
  `Runtime/Math.cs`, `Exceptions.Derived.cs`, `NumberFormatting.cs`, `Std.props`.
- SDK и приложения: `apps_native/sdk/{AppRuntime,AppHost,AppServiceTable,AppStartupBlock,
  FileSystem.AppHost,MinimalRuntime,FreestandingPe.props}`; `Shell/{Executor,Program}.cs`,
  `Shell/autorun.sh`; `AotTests/Program.cs`; удалены `Fami/Compat/Binary*.cs`,
  `TriCNES/Compat/BinaryReader.cs`.
- `generators/SharpOS.Generators/` (новый).
- Документы: `pipe_plan.md`, `docs/nativeaot-nostd-kernel-limits.md`, `README.md`.

## Следующий шаг

`pipe_plan.md`, «Проверить опытом»: ядро как получатель региона (в памяти
обменной кучи, перевод на месте, чтение под сборками) и печать графа по таблице
описаний на SharpOS. Затем — сама труба.
