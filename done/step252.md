# step 252 — CoreLib как в стоке: std — сборка System.Private.CoreLib; имена перечислений из метаданных

Раздел релиза «CoreLib как в стоке» (`release.md`). Повод — step251: ILC пишет
метаданные отражения только когда системная сборка называется
`System.Private.CoreLib` (`ILCompiler/Program.cs:96`). Раньше std компилировался
в каждый образ, системной сборкой был сам образ, и метаданных не было —
`Enum.ToString` давал `"(object)"`. Теперь устройство как в стоке, ILC стоковый,
`Enum` — по именам.

## 1. Сборки

```
Ядро:        System.Private.CoreLib (CoreLib.Kernel) ← OS
Приложение:  System.Private.CoreLib (CoreLib.App) ← SharpOS.AppSdk ← приложение
                                                  ← библиотеки (Json, Regex, Xml, CodePages, SharpCompress)
```

- **Две CoreLib из одного дерева** — как сток собирает CoreLib под платформы
  (`*.Windows.cs`/`*.Unix.cs`): `std-no-runtime/CoreLib.Kernel` = `Std.props` +
  `Std.Kernel.props`, `std-no-runtime/CoreLib.App` = `Std.props` + `Std.App.props`
  + рантаймная половина SDK (`apps_native/sdk/SdkRuntime.props`: таблица служб,
  запуск, `File`/`Path`/`Environment`/`Console`/`Process` поверх неё — Interop и
  StartupCodeHelpers стока). `MinimalRuntime`, `ThrowHelpers`, `LdTokenHelpers`
  переехали в std (`Runtime/*.Kernel.cs`, `*.App.cs`).
- **Ядро (`OS`) и программы** ссылаются на свою CoreLib; `IlcSystemModule =
  System.Private.CoreLib`, `UnmanagedEntryPointsAssembly` — CoreLib, `IlcReference`
  — наши сборки вместо стоковых CoreLib и фреймворка, которые targets клали в
  `-r:` (`UseSharpOsCoreLib`). Ссылки фреймворка снимаются до разрешения
  (`DropFrameworkReferences`, иначе MSB3243 с нашими Json/Regex).
- **`SharpOS.AppSdk`** — библиотека над CoreLib: то, на что CoreLib не
  опирается (`UiLatency`, `AppHeapCensus`, `StringAlgorithms`). Остальной SDK
  тянется из `File`/`Console`/запуска и живёт в CoreLib.
- **Порты BCL — своими сборками, как в стоке** (`std-no-runtime/Libraries`,
  `Library.props`): `System.Text.RegularExpressions`, `System.Text.Json` (+
  Encodings.Web, Pipelines), `System.Private.Xml`, `System.Text.Encoding.CodePages`,
  `SharpCompress` (+ `System.IO.Compression`). Компилируются раз на все
  приложения; `Regex.props`/`Json.props`/… — ссылка на сборку и генератор для кода
  приложения. У каждой свой `SR` (недостающие ключи — `SR.*.Shared.cs`, копии из
  `SR` CoreLib), `[assembly: CLSCompliant(false)]` (`LibraryAssembly.cs`).
- **Кто видит `internal` CoreLib** (`Runtime/CoreLibFriends.*.cs`): ядро, SDK,
  порты-библиотеки, белые батареи (AotTests, DataTest), системные программы,
  работающие с трубами ниже типизированного API (Shell, Utils, KQL, CONVERT, Js,
  Lua, PipePerf, PipeSpy, Synth, WriteApp) — до публичного низкоуровневого API
  труб (donext). ConvertApp — ещё и STJ (позиция ошибки в `JsonReaderState`).
- **Публичным стало** то, что в BCL публично (`StructLayoutAttribute`,
  `LayoutKind`, `DllImportAttribute`, атрибуты `System.Diagnostics.CodeAnalysis`,
  `IsExternalInit`, `SkipLocalsInit`, `RequiredMember`, `SetsRequiredMembers`,
  `FeatureSwitchDefinition`, новые `FixedBufferAttribute`/`UnsafeValueTypeAttribute`
  — без них другая сборка не видит фиксированный буфер), и API SDK, которым
  пользуются приложения (`AppRuntime`, `AppHost`, `AppGC`, `AppThreads`,
  `AppConsole`, `KeyInfo`, `TerminalKeys`, ABI-структуры таблицы служб…). Новые
  публичные точки вместо внутренностей: `AppGC.EnableStress`,
  `AppGC.AllocCount`…, `AppRuntime.ImageBase`.

## 2. Ядерный ярус → ядро

CoreLib не может назвать типы ядра — обращения развёрнуты, как стоковая CoreLib
к ОС через Interop:

- `Runtime/KernelServices.cs` — указатели, которые ядро ставит в Phase 0/1:
  куча ядра (запасной путь строк и сегменты GC), `Panic.Fail`, консоль,
  «исключение в полёте». Быстрые пути (сдвиг указателя, строки в управляемой
  куче) остались общим кодом CoreLib без указателей.
- Трубы: `KernelPipeBackend` (абстрактный, CoreLib) ← `KernelPipeBackendImpl`
  (ядро), ставится в Phase 2.
- `EnvironmentStore` хранит загрузочное окружение сам; `ProcessEnvironment.Boot`
  ядра — его фасад. `TaskBackendInstaller` переехал в ядро
  (`OS/src/Kernel/Threading`) — это ядерный код.
- `Stopwatch` — один файл для обоих ярусов (адрес счётчика HPET + защёлка);
  ядро пишет источник в `Hpet.Init`, как `AppRuntime` для приложений.

## 3. Генераторы: регистрация через [ModuleInitializer]

`Pipes`/`Dynamic` писали `partial` к классам std (`MessageCatalog`,
`CallSiteThunks`, `DynamicMembers`) — через границу сборок `partial` не
склеивается. Теперь пишут свой класс в `SharpOS.Generated` и передают
регистрацию через `[ModuleInitializer]` (step251) в `AddGenerated` таблицы
CoreLib; регистрации есть и у самой CoreLib (`[Message]`-типы std). `Dynamic.Check`
ищет `SharpOS.Generated.DynamicThunks`. `ImageInfo` для CoreLib не пишется
(CoreLib — не образ; иначе CS0436). Таблицы `dynamic` обходят и сборки-ссылки
(`Math`, `Console`, массивы живут в CoreLib), без встроенных компилятором
`[Embedded]`-типов и без операторов `nint`/`nuint` (ошибка генератора,
вскрытая обходом: `(nuint)a + (int)b` неоднозначно).

Сведение генераторов:
- `ImageInfo` — единственный источник сведений об образе: `AssemblyName`,
  `BuildId`, `ForkConfig` (`CompilerVisibleProperty` из `Generators.props`).
  Для приложений (`SharpOsImageKind=App`) он же пишет `[ModuleInitializer]`
  → `AppRuntime.SetBuildInfo`. Цели MSBuild `GenerateBuildInfo` (ядро) и
  `GenerateAppBuildInfo` (приложения) удалены — было два расходящихся пути.
- Копии генераторов форка `Json.Generator` и `Regex.Generator` удалены.
  Генераторы библиотек — стоковые, но не из пакета `TargetFramework` (net8.0,
  его держит ILC 8), а из пакета .NET, который несёт SDK (10.0.8) — той же
  версии, что библиотеки форка: `std-no-runtime/Libraries/PackGenerators.props`
  подменяет их по имени файла там, где библиотека взята. Генератор Regex из
  net8.0 не годился и так: не знает `[GeneratedRegex]` на частичном свойстве
  (CS9248 на `XmlConvert` форка и Okojo). Json-генератор узнаёт атрибуты по
  имени сборки System.Text.Json — библиотека теперь она и есть.

## 4. Делегаты

С настоящей CoreLib ILC строит переходники `InvokeObjectArrayThunk` и просит
`Internal.Runtime.CompilerHelpers.DelegateHelpers` — перенесён из NativeAOT 8.

## 5. Метаданные и Enum

- `Runtime/Metadata/` — стоковые читатели `Internal.Metadata.NativeFormat`
  (как есть, PROVENANCE.md; NativeFormat-примитивы в std были), перечисления
  атрибутов `System.Reflection` (BCL 8).
- `ImageMetadata` — стоковый `TryGetMetadataForNamedType` на один модуль: хеш
  MethodTable → `TypeMap` (301) → проверка по `CommonFixupsTable` (308) →
  `TypeDefinitionHandle` в 313. Заголовок RTR ставят `NativeAotModuleInit` и
  `AppTypeManagerInit`.
- `Enum.Names.cs` — поля как стоковый `NativeFormatEnumInfo`, кэш по MethodTable;
  форматирование и разбор — порт BCL 8 (G/D/X/F, `[Flags]`, имена через запятую,
  `ignoreCase`). Приватные вложенные перечисления — тоже (метаданные есть у всех).
  Без статического конструктора у `Enum` (замок ленивый).
- Ресурсы: с метаданными ILC сам кладёт манифестные ресурсы — `codepages.nlp`
  библиотеки CodePages читается (COMPRESSTEST), `ManifestResources.targets`
  больше не подключается.

## 6. Предупреждения

Ноль новых: CS0436 у библиотек (свой `SR` против видимого `SR` CoreLib —
задумано) — `std-no-runtime/.editorconfig`, `vendor/SharpCompress/.editorconfig`;
IL2026/IL3050/IL2104/IL3053 (анализ обрезки/AOT в ILC, идёт с отражением) —
`SuppressTrimAnalysisWarnings`/`SuppressAotAnalysisWarnings`; оба в
`docs/build-warnings.md`.

## Проверка

- Прогоны 330–333 (по ходу): ядро на новой CoreLib — пробы OK 100;
  конвейер, KQL, JSON/Regex/Xml/KQL-тесты, COMPRESSTEST 53/53 (кодовые страницы
  из ресурса библиотеки), AotTests — `dynamic` над типами CoreLib после правки
  генератора; `enum.toString: ok`, проверки перечислений в AotTests (приватные,
  `[Flags]`, `D`/`X`/`F`, `Parse`, `GetNames`, BCL-перечисления).
- Батарея 334: приложения 35/0, `probe_report` OK 102 / FAIL 1 (Usb SKIP — в
  QEMU нет xHCI), батарея 67/68 (PROCTEST №15 — SYM-008).
- Батарея 335 — после правок предупреждений и генератора: приложения 35/0,
  предупреждений сборки ноль (ядро и приложения, кроме NU1903 пакета ILC);
  `[term] PASS`, `enum.toString: ok`, `[ModuleInitializer]: ok`; `probe_report`
  OK 102 / FAIL 1 (Usb SKIP); батарея 67/68 (PROCTEST №15 — SYM-008).
- Хост: `Pipes.Generator.Tests`, `Dynamic.Generator.Tests` — ALL PASSED (заглушки
  обновлены; у `Dynamic` заодно `Run1..3`, отставшие с step193).
- Размеры: ядро 2,30 → 2,65 МБ, SHELL 1,43 → 1,50, AotTests 3,58 → 3,72 —
  метаданные: блок 313 ядра 100 → 409 КБ. Правило ILC — описание у каждого типа
  с MethodTable, у перечисления все члены (Iced `Code` — 4936); как в стоке.

## Файлы

- Сборки: `std-no-runtime/CoreLib.Kernel/`, `CoreLib.App/`, `Libraries/*`,
  `Generators.props`, `apps_native/sdk/SharpOS.AppSdk.csproj`, `SdkRuntime.props`;
  `OS/OS.csproj`, `apps_native/sdk/FreestandingPe.props`, 35 csproj (без
  `IlcSystemModule`); `Regex.props`, `Json.props`, `Xml.props`, `CodePages.props`,
  `vendor/SharpCompress/SharpCompress.props`.
- std: `Runtime/{MinimalRuntime,ThrowHelpers}.Kernel.cs`, `MinimalRuntime.App.cs`,
  `LdTokenHelpers.cs`, `Stopwatch.cs`, `KernelServices.cs`, `CoreLibFriends.*.cs`,
  `DelegateHelpers.cs`, `CompilerServices/FixedBufferAttribute.cs`,
  `Metadata/*`, `Reflection/*`, `Enum.Names.cs`, `Enum.Statics.cs`; ярусные
  `GcMemorySource.KernelHeap`, `StringRuntime.KernelHeap`,
  `Diagnostics.Output.KernelConsole`, `EnvironmentStore.Kernel`,
  `PipeTransport.KernelPipes`; `Pipes/MessageCatalog.cs`,
  `Dynamic/{CompilerSurface,DynamicMembers}.cs`; `SR.*.Shared.cs`;
  CodeAnalysis-атрибуты публичными.
- Ядро: `BootSequence.cs` (хуки), `Hpet.cs`, `ExInfo.cs`, `NativeAotModuleInit.cs`,
  `ProcessEnvironment.cs`, `Pipes/KernelPipeBackendImpl.cs`,
  `Threading/TaskBackendInstaller.cs`, проба `enum`.
- SDK и приложения: `AppRuntime.cs` (`SharpAppBootstrap` здесь, `SetBuildInfo`,
  `ImageBase`), `AppGC.cs`, видимость типов; `SharpAppBootstrap` убран из 33
  приложений; `StressArgs`, PipePerf/PipeSpy/ProcTest — на публичные точки;
  Terminal.Gui `TypeIdentity.cs` — на `GetType()`.
- Генераторы: `Pipes`, `Dynamic`, `Dynamic.Check`, `ImageInfo`; тесты;
  `Json.Generator`, `Regex.Generator` удалены, `Libraries/PackGenerators.props`;
  `SystemBanner.cs`, `SystemFacts.cs` — на `ImageInfo`.
- Документы: README, `std-no-runtime/README.md`, CLAUDE.md, limits, `limits.md`,
  `build-warnings.md`, `release.md` (раздел закрыт), donext, SBOM, PROVENANCE
  (Metadata, SharpCompress), Json и Regex README.

## Дальше

- Низкоуровневый API труб — убрать системные программы из друзей CoreLib.
- Имена кадров в трассах — метаданные есть.
- Релиз: §10 «Ctrl+C для всех».
