# step 226 — BCL `Encoding` целиком, кодовые страницы

## Почему

Релиз 0.1, раздел 2, последний пункт — кодовые страницы
(`System.Text.Encoding.CodePages`): имена в CP437 и прочих страницах в zip, вырез
`SHARPOS_CODEPAGES` в SharpCompress. Библиотека наследует от BCL-иерархии
`Encoding` и переопределяет её указательные методы, `Encoder`/`Decoder` с
состоянием и fallback-буферы; в std был свой частичный `Encoding` (step133) без
всего этого. По решению пользователя — BCL `Encoding` целиком на обоих ярусах,
CodePages поверх как есть. Источник — локальный v8.0.27
(`gc-experiment/dotnet-runtime-8.0`): векторы и интринсики std — CoreLib 8.

## Что сделано

- **Иерархия кодировок BCL 8** в `std-no-runtime/Text` (оба яруса):
  `Encoding` (+`Internal`, `EncodingData`, `EncodingTable`, `EncodingInfo`,
  `EncodingProvider`, `CodePageDataItem`), `UTF8`/`ASCII`/`Latin1`/`Unicode`/
  `UTF32`/`UTF7`, `Encoder`/`Decoder`/`EncoderNLS`/`DecoderNLS`, fallback-классы
  (замена, исключение, best-fit Latin1), `TranscodingStream`, `Ascii.Utility`/
  `Transcoding`, `Latin1Utility`, `Unicode/Utf8Utility*`, `Utf16Utility*`, `Utf8`.
  Своя иерархия std (`Encoding.cs` прежний, `FallbackExceptions.cs`,
  `Encoding.CodePage.cs`, `UTF32Encoding.cs`) заменена. Вырезана только
  сериализация.
- **Публичный `Ascii`** — прежний частичный порт std (`IsValid`,
  `To*InPlace`) плюс BCL `FromUtf16`/`ToUtf16`: `Ascii.Equality`,
  `CaseConversion`, `Trimming` построены на обобщённой математике
  (`INumberBase<T>`), её в std нет (donext).
- **CodePages v8.0.27** — `std-no-runtime/Text/CodePages`, строкой
  `CodePages.props` (её тянет `SharpCompress.props`), `codepages.nlp` 612 КБ
  ресурсом. Правки — `README.md` там же: свои копии `DecoderNLS`/`EncoderNLS`/
  `EncodingTable`/`EncodingCharBuffer`/`EncodingByteBuffer` переименованы
  `CodePages…` (в апстриме — отдельная сборка, у нас они столкнулись бы с
  внутренними типами CoreLib), `ReaderWriterLockSlim` → `CodePagesCacheLock`
  над одним `Monitor` (у RWLS состояние в `[ThreadStatic]`).
- **Потребители — к апстриму:** SharpCompress `ArchiveEncodingExtensions`
  (байт в байт с 0.50.7), Lua `LuaByteEncoding`, `Utf8Reader`, `LuaStream`
  (файлы закреплённого коммита `a97aac88`; оставлен только вырез
  `LuaStreamDefaults` — про методы интерфейса по умолчанию),
  Encodings.Web `AllowedBmpCodePointsBitmap` (вырез `Vector<T>`),
  `TextWriter.Write(StringBuilder)` через `GetChunks`.
- **std под это** (BCL 8): `System.Numerics.Vector<T>` целиком (и снят вырез
  его преобразований в `Vector128/256/512`), `Marshal.AllocHGlobal`/`FreeHGlobal`/
  `ReAllocHGlobal`/`*CoTaskMem` (тела Unix поверх `NativeMemory`), `SafeBuffer`,
  `SafeHandleZeroOrMinusOneIsInvalid`, `UnmanagedMemoryStream`,
  `Object.MemberwiseClone` (оба `MinimalRuntime`), `string(char*)` и
  `(char*, int, int)`, `string.CreateStringFromEncoding`/`CompareOrdinal(span, span)`,
  `StringComparer : IComparer, IEqualityComparer`, `MemoryMarshal.GetNonNullPinnableReference`,
  `ArgumentNullException.ThrowIfNull(void*)`, `HResults`, `NotImplemented`,
  `Enum.TryFormatUnconstrained` (числом), члены `ThrowHelper`, ключи SR.
- **Ядро:** `g_cpuFeatures` — `[CoffDataSymbol]` в `CrtAndEhStubs.cs`, ноль
  (базовые наборы): BCL `Ascii.Utility` спрашивает оптимистичные наборы, и
  компоновка ядра падала с `undefined symbol: g_cpuFeatures`.
- `BootAsm.Generator`: CS0675 (`(ulong)ordinal` → `(ulong)(uint)ordinal`) —
  предупреждение всплывало, когда генератор пересобирался.
- **Проверки:** AOTTESTS — потоковый декодер на разрезанной UTF-8, U+FFFD,
  `DecoderFallbackException`, ASCII с исключением и заменой, Latin1, UTF-32,
  преамбула UTF-16 BE, `GetEncoding` по имени, 1251 без провайдера —
  `NotSupportedException`, `TranscodingStream`. COMPRESSTEST — 1251, 866, 437,
  932, 54936 туда и обратно, windows-1252 `€`, koi8-r по имени, zip с именем в
  CP437, записанный `ZipArchive` и прочитанный SharpCompress.

## Находки

- **Манифест-ресурсов в образах приложений не было никогда.** step212 вёл
  `Assembly.GetManifestResourceStream` через секции ReadyToRun 324/325, но ILC
  кладёт ресурсы только модулям с метаданными отражения (`ResourceDataNode`
  обходит `GetCompilationModulesWithMetadata`), а у приложений метаданных нет.
  Нашлось на CodePages (`InvalidOperationException` в
  `BaseCodePageEncoding.GetEncodingDataStream`, символизировано по образу);
  данных Intl у Okojo в образе JsApp тоже не оказалось — проверено поиском
  содержимого файлов в образе. Дешёвая гипотеза — пометить один тип метаданными
  через rd.xml — проверена сборкой и не подтвердилась. Решение —
  `std-no-runtime/ManifestResources.targets` (inline-задача MSBuild на C#):
  каждый `EmbeddedResource` становится `ReadOnlySpan<byte>` за
  `new byte[] {…}` — данными образа, — а `ImageAssembly` отдаёт его
  `UnmanagedMemoryStream` поверх них, как BCL. После этого
  `Intl.Locale('und-Cyrl').maximize()` дал `ru-Cyrl-RU` (данные `LikelySubtags`).
- **CodePages — отдельная сборка у апстрима.** Её внутренние типы повторяют
  имена внутренних типов CoreLib; в одной сборке они либо сталкиваются
  (CS0101), либо тихо подменяются: `EncodingCharBuffer` внутри наследника
  `Encoding` разрешился бы во вложенный тип базового класса.
- **`.gitignore` глотает `Win32/`** — `Microsoft/Win32/SafeHandles/SafeAllocHHandle.cs`
  не попал бы ни в рабочую копию, ни в коммит; лежит без каталогов.
- Синхронизация рабочей копии: файл, откаченный до HEAD, пропадает из
  `git status` и не копируется (в прогоне 241 это оставило старый
  `GcStaticsInit.cs`); мусорные файлы со странными именами в корне ломали `tar`
  посреди копирования — синхронизация теперь пропускает несуществующие пути.

## Проверка

Рабочая копия, WHPX, `-SkipCoreClr`, прогон 246. Ядро и все приложения
собраны, предупреждений 0. С тестами `ran=52 failed=0`: AOTTESTS ×3 с проверками
кодировок, COMPRESSTEST 53/53 (кодовые страницы, zip с именем в CP437),
REGEXTEST 93/0, JSONTEST 72/0, DATATEST 239/0 и 236/0, KQLTEST 7/0, GVMTEST 10/0,
PIPEPERF 20/0, PROCTEST 85/0 и 80/0. JS: `Intl.Locale('und-Cyrl').maximize()` →
`ru-Cyrl-RU` (ресурс `LikelySubtags`). Lua: `#'Жук'` → 3 — режим строк LuaApp
`Utf16`, умолчание апстрима. `-NoTests` 6/6. До него: прогон 242 — ядро не
компоновалось (`g_cpuFeatures`), 243 — ресурсов нет в образе, 245 — тест читал
CP437-имя без указания кодировки (у SharpCompress умолчание —
`Encoding.Default`, UTF-8, как в апстриме).

## Не сделано

- Обобщённая математика — публичные `Ascii.Equals/ToUpper/ToLower/Trim`.
- `BinaryReader`/`BinaryWriter` на `Decoder`/`Encoder` (donext).
- `Vector2`/`Vector4`/`Plane`/`Quaternion`.
- Остальной `Marshal`.

## Файлы

std: `Text/*` (иерархия кодировок BCL 8, новые и заменённые; удалены
`Encoding.CodePage.cs`, `FallbackExceptions.cs`), `Text/Unicode/*`,
`Text/CodePages/**` (новые), `Numerics/{Vector,Vector_1,VectorDebugView_1}.cs`,
`Runtime/InteropServices/{Marshal.HGlobal,SafeBuffer,SafeHandleZeroOrMinusOneIsInvalid}.cs`,
`IO/UnmanagedMemoryStream{,Wrapper}.cs`, `Runtime/HResults.cs`, `SR.Encoding.cs`,
`SR.Interop.cs`, `ManifestResources.targets` (новые); `Runtime/Reflection.Assembly.cs`,
`Std.App.props`; `Runtime/Intrinsics/Vector{128,256,512}.cs`,
`Runtime/{MemoryMarshal,Enum.Names,ThrowHelper.Tasks}.cs`, `Bcl/ThrowHelper.{CoreLib,Intrinsics}.cs`,
`Exceptions.Derived.cs`, `StringComparer.cs`, `StringComparison.cs`,
`SystemString.Span.cs`, `IO/{StringWriter,TextWriter}.cs`, `IO/Compression/SR.Compression.cs`,
`Globalization/GlobalizationMode.cs`, `Runtime/MemberwiseClone.cs`,
`Text/Encodings.Web/.../AllowedBmpCodePointsBitmap.cs`, `Text/Json/README.md`, `Std.props`.
vendor: `SharpCompress/{SharpCompress.props,PROVENANCE.md}`,
`src/Common/ArchiveEncodingExtensions.cs`; `LuaCSharp/PROVENANCE.md`,
`src/Lua/IO/{LuaByteEncoding,LuaStream}.cs`, `src/Lua/Internal/Utf8Reader.cs`.
Ядро: `OS/src/Boot/MinimalRuntime.cs`, `OS/src/PAL/SharpOSHost/CrtAndEhStubs.cs`.
SDK: `MinimalRuntime.cs`. Генератор: `BootAsm.Generator/BootAsmGenerator.cs`.
Приложения: `AotTests/FormatTests.cs`, `CompressTest/Program.cs`. Документы:
`README.md`, `release.md`, `donext.md`, `docs/nativeaot-nostd-kernel-limits.md`.
