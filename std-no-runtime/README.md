# std-no-runtime

Замена стандартной библиотеки и рантайм-контрактов для профиля `NoStdLib`.
Одна на оба яруса: ядро (`OS/`) и приложения (`apps_native/`).

Здесь живёт всё, что не должно быть зашито в слой ОС: порты BCL, строки,
коллекции, сборщик мусора, задачи, форматы.

## Сборки

С step252 std — отдельная сборка `System.Private.CoreLib`, как в стоке; по одной
на ярус, из одного дерева (так сток собирает CoreLib под каждую платформу):

| проект | что в нём | над ней |
|---|---|---|
| `CoreLib.Kernel/` | `Std.props` + `Std.Kernel.props` | ядро (`OS/OS.csproj`) |
| `CoreLib.App/` | `Std.props` + `Std.App.props` + рантаймная половина SDK (`apps_native/sdk/SdkRuntime.props`: таблица служб, запуск, `File`/`Path`/`Environment`/`Console`/`Process` поверх неё) | `SharpOS.AppSdk`, библиотеки, приложения |
| `Libraries/*` (`Library.props`) | порты BCL отдельными сборками, как в стоке: `System.Text.RegularExpressions`, `System.Text.Json`, `System.Private.Xml`, `System.Text.Encoding.CodePages`, `SharpCompress` | приложения (через `Regex.props`, `Json.props`… рядом с исходниками) |

Почему это важно: ILC пишет метаданные (имена типов, поля перечислений) только
когда системная сборка называется `System.Private.CoreLib` — `Enum.ToString`,
`Runtime/Metadata/ImageMetadata.cs`.

Кто видит `internal` CoreLib — `Runtime/CoreLibFriends.{Kernel,App}.cs`: ядро,
SDK, библиотеки-порты, тестовые батареи и системные программы, работающие с
трубами ниже типизированного API.

Генераторы (`Generators.props`) подключают и CoreLib, и каждая программа: каждая
сборка регистрирует своё в таблицах CoreLib через `[ModuleInitializer]`.

## Три списка

Файлы перечислены в `.props`:

| список | кто берёт | что в нём |
|---|---|---|
| `Std.props` | **оба** CoreLib | всё общее |
| `Std.Kernel.props` | `CoreLib.Kernel` | ярусная реализация для ядра |
| `Std.App.props` | `CoreLib.App` | ярусная реализация для приложений |

**Ярусный** — значит: одно и то же, сделанное по-разному, потому что ярусы
разные. Не «ядру это не понадобилось». Сегодня пары такие:

| ядро | приложения | что различается |
|---|---|---|
| `GcMemorySource.KernelHeap` | `GcMemorySource.AppStatic` | откуда сборщик берёт страницы |
| `StringRuntime.KernelHeap` | `StringRuntime.RhNewString` | как рождается строка |
| `Diagnostics.Output.KernelConsole` | `Diagnostics.Output.AppHost` | куда идёт `Debug` |
| `Pipes/PipeTransport.KernelPipes` | `Pipes/PipeTransport.AppServices` | транспорт труб: бэкенд ядра или служба |
| `EnvironmentStore.Kernel` | `EnvironmentStore.AppServices` | где окружение и рабочая папка: загрузочное окружение ядра или запись процесса в ядре (step207) |
| `Runtime/Array.Empty.Kernel` | `Runtime/Array.Empty.App` | `Array.Empty<T>()`: новый массив (GC-статики ядра — с Phase 2) или один на тип, как в BCL (step225) |
| `Runtime/MinimalRuntime.Kernel` | `Runtime/MinimalRuntime.App` | базовые типы и помощники, которые ILC ищет по имени в системном модуле |

Плюс у ядра — `Runtime/KernelServices` и `ThrowHelpers.Kernel`, у приложений —
`GcStaticsInit` и `System.Formats.Tar`.

Ядерный ярус к ядру обращается только через то, что ядро ставит при загрузке:
`KernelServices` (куча до управляемой, консоль, остановка) и
`PipeTransport.Backend` — как стоковая CoreLib обращается к ОС через Interop.
Прикладной ярус говорит с ядром через таблицу служб.

## Правило

**Новый файл здесь попадает в ровно один из трёх списков.** Не в два, не в ноль.

До 2026-09-30 списка было два и они жили порознь — один внутри `OS.csproj`
среди двух сотен файлов ядра, другой внутри `FreestandingPe.props` вперемешку с
SDK. Они разошлись на 49 файлов, и каждое расхождение всплывало одинаково:
ошибкой компилятора «пространства имён не существует» про файл, который лежит в
дереве и прекрасно собирается соседним ярусом. За один вечер это стоило трёх
полных пересборок подряд.

Если файл нужен только одному ярусу — он ярусный, и это надо **назвать**: имя
файла с суффиксом яруса и строка в таблице выше. Если назвать нечем, значит
файл общий.
