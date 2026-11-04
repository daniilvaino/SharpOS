# step 251 — [ModuleInitializer], широкий символ, summarize, xHCI; почему нет имён перечислений

Раздел релиза §9 «Ошибки (не CoreCLR)». FAT16, обрыв COM3 и кириллица — не в 0.1
(решение пользователя), остались в donext. Имена перечислений не сделаны: разбор
показал, что правильный путь — CoreLib отдельной сборкой (§6, новый раздел в
`release.md`).

## 1. `[ModuleInitializer]`

Roslyn собирает инициализаторы в `<Module>..cctor`, ILC перечисляет такие
конструкторы в секции RTR `ModuleInitializerList` (213; генерируется всегда), а
вызывает их сток из `__managed__Main` — мимо неё входят и ядро, и приложения.
Формат записей — как у GC static region: на RTR 9 4-байтовые относительные.
Ядро — `NativeAotModuleInit.RunModuleInitializers()` в конце Phase 2 (статики,
куча, `s_canWait` готовы); приложение — `GcStaticsInit.RunModuleInitializers()`
в конце `AppRuntime.Initialize`, перед `Main`. Проба ядра заведена в
`probe_report` (`ModuleInitializer`), в AotTests — своя.

## 2. Вторая ячейка широкого символа (XtermSharp)

`[term] FAIL wide`: заглушка после CJK была `CharData.Null` шириной 1, у xterm.js
— 0. Правка в `InputHandler.Print`: заглушка `new CharData(attr, 0, 0, 0)`;
перенесён из xterm.js сброс осиротевшей заглушки на месте курсора (левая половина
перезаписана → ширина 1, иначе ячейка не рисуется); `TranslateToString`
пропускает заглушки (иначе в тексте `\0`). Отрисовщик ядра ширину 0 уже
пропускал.

## 3. `summarize` BabyKusto

Каждая группа копила все столбцы входа (upstream TODO). Теперь `Init` обходит IR
агрегатов и отмечает столбцы из `IRRowScopeNameReferenceNode` — единственный
путь, которым выражение читает столбец куска. Группа (`Bucket`, класс вместо
кортежа) копит только их и счётчик строк; остальные столбцы — `UnreadColumn`:
тип и число строк, данные — только если кто-то спросит. `count()` берёт число
строк куска у первого столбца, поэтому счётчик нужен и при нуле столбцов.

## 4. xHCI ноутбука (4:0.0)

`Address Device` → код 4 (Transaction Error) на каждой загрузке. Добавлено: пауза
восстановления 50 мс после сброса порта (USB 2.0 TRSTRCY 10 мс, Linux 10 + 40) и
при коде 4 — второй сброс и повтор на том же слоте; в отчёте `addr=… (retried)`.
**На стенде не прогонялось** — следующее действие в donext.

## 5. Имена перечислений — сначала генератор, потом отказ

Сделан и прогнан (прогон 328, зелёный) генератор таблиц имён по образу, с
портом BCL-форматирования поверх. Отказались: в обычном NativeAOT имена даёт
ILC, второй механизм рядом со стоковым — не путь. Откачен целиком.

Почему ILC не кладёт имён у нас (ILC 8.0.27, исходники в
`gc-experiment/dotnet-runtime-8.0`):

- `ILCompiler/Program.cs:96`:
  `supportsReflection = reflectionData != "none" && systemModuleName == "System.Private.CoreLib"`.
  Локальная, присваивается один раз; читается в строке 108 (переходники
  делегатов `DelegateFeature.All`: `DynamicInvoke`, open-instance) и в строке 374
  (политика метаданных). Других проверок имени системной сборки нет.
- `systemModuleName` — сборка, где `System.Object`. У стокового приложения это
  отдельная CoreLib, у нас — сам образ (`--systemmodule:AotTests`, `OS`).
- Политика: `true` → `NoMetadataBlockingPolicy`, `false` →
  `FullyBlockedMetadataBlockingPolicy` (`IsBlocked` — всегда `true`). Менеджер
  всегда `UsageBasedMetadataManager`, меняется только политика.
- Цепочка: MethodTable в образе → `GetDependenciesDueToEETypePresence` →
  `GetMetadataCategory` (`!IsReflectionBlocked` → `Description`) →
  `TypeMetadataNode.GetMetadataDependencies` (`CanGenerateMetadata`) →
  `TypeMetadataNode` для перечисления добавляет `FieldMetadata` всех литеральных
  полей (`TypeMetadataNode.cs:50`) → `ComputeMetadata` кладёт отмеченное в 313
  (`EmbeddedMetadata`), соответствие MethodTable → тип в 301 (`TypeMap`).
  Анализа использования `ToString` нет — достаточно MethodTable.
- В наших образах 313 есть (~100 КБ), но в нём только то, что нужно трассам:
  `ConsoleKey` есть, `LeftArrow` нет.

Проверено: ILC, собранный из исходников 8.0.27 (`dotnet build` с
`LangVersion=12`, `jitinterface`/`clrjit` из пакета), с условием строки 374 по
одному `reflectionData` — AotTests собирается, в 313 появились `Hue`, `HueSet`,
`Green`, приватное `CastTint`, `LeftArrow`; 313 — 109 → 204 КБ, образ +150 КБ.
Правка откачена.

Пустая `System.Private.CoreLib` не годится (ILC берёт из системного модуля
`Object` и прочие известные типы), с переадресациями — ломает тихо: больше
десятка проверок «модуль типа == системный модуль» (`isMethodDefinedInCoreLib`,
`TypePreinit` для `ReadOnlySpan`/`CreateSpan`, `RuntimeHelpersIntrinsics`,
`InteropTypes`, `EETypeBuilderHelpers`…).

Решение — std отдельной сборкой `System.Private.CoreLib`, ярусы — рантайм рядом
(`[RuntimeImport]` в CoreLib, `[RuntimeExport]` в ядре и SDK), как сток. План —
`release.md`, «CoreLib как в стоке».

## Проверка

Прогон 328 (WHPX, `-SkipCoreClr`, все приложения пересобраны — `run_build`
пересобирает только ядро, перед прогоном нужен `./build.ps1`):

- ядро: `[term] self-test PASS`, `[ModuleInitializer]: ok`; `probe_report` до
  отказа от генератора — FAIL 1 (Usb SKIP, в прогоне нет xHCI), было 3 (прогон
  323); после отката `EnumToString` снова красная;
- AotTests: `[ModuleInitializer] ran before Main` — ok во всех запусках;
- `SAMPLE_PIPEGEN 200000 | KQL 'summarize n = count() by Level'` — 5 групп по
  40 000;
- батарея 68, упала 1 — PROCTEST №15 (SYM-008, загрузка хоста; на стенде 49.5 %).

Прогон 329 — после отката генератора, та же батарея: приложения 35/0,
`[term] PASS`, `[ModuleInitializer]: ok` (ядро и AotTests), `enum.toString: FAIL`
(ожидаемо), `probe_report` FAIL 2 (`EnumToString`, Usb SKIP), `summarize`
200 000 — 5 групп по 40 000, батарея 67/68 (PROCTEST №15).

## Файлы

- `OS/src/Kernel/Memory/NativeAotModuleInit.cs`, `OS/src/Boot/BootSequence.cs`,
  `std-no-runtime/GC/GcStaticsInit.cs`, `apps_native/sdk/AppRuntime.cs`
- `vendor/XtermSharp/XtermSharp/InputHandlers/InputHandler.cs`, `BufferLine.cs`
- `vendor/BabyKusto/src/Evaluation/TreeEvaluator.QueryOperators.Summarize.cs`
- `OS/src/Hal/Usb/Xhci.cs`, `XhciRings.cs`, `XhciDevice.cs`, `UsbBus.cs`,
  `OS/src/Kernel/Diagnostics/UsbProbe.cs`
- пробы: `apps_native/AotTests/Program.cs`, `tools/probe_report.ps1`
- документы: limits (§ModuleInitializer, строка `Enum`), `limits.md`, README,
  PROVENANCE XtermSharp и BabyKusto, `release.md` (§1, §9 закрыты, новый раздел
  «CoreLib как в стоке»), `donext.md`, `docs/open-symptoms.md` (SYM-008)

## Дальше

- Раздел «CoreLib как в стоке».
- xHCI 4:0.0 — загрузка на ноутбуке с диагностикой; код 4 снова — BSR=1.
