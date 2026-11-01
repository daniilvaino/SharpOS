# Предупреждения сборки

Ядро и приложения собираются без предупреждений — ядро и с тестами, и
`-NoTests`, где выключенных веток больше всего (с релиза 0.1). Что подавлено —
здесь, с причиной. Подавление всегда точечное: прагма в файле или вокруг поля,
`.editorconfig` по пути чужого кода. `NoWarn` на весь проект не ставим — он
прячет и новые случаи.

## Наш код

| Код | Где | Почему не устраняется |
|---|---|---|
| CS0162 | файлы с `Probes.*`, `Trace`, `TraceUnwind`, `ShowTiming`, `Enabled` | Переключатели — `const` намеренно: компилятор вырезает выключенную ветку ещё в IL, и ILC не тянет в образ код проб. Свойство убрало бы предупреждение и вернуло бы этот код в образ. |
| CS0649 | `AppServiceTable`, `AppStartupBlock`, `UefiGop`, поля-заполнители (`Pad`, `Reserved`, `B0..B3`) | Заполняет другая сторона ABI (ядро, прошивка), не C#. |
| CS0649 | поля std, которые задаёт один ярус (`GcHeap.s_preemption*`, `GcRoots.ExtraRoots`, `Type.ModuleBase`, …) | std компилируется и в ядро, и в приложения; один ярус поле задаёт, другой оставляет пустым. |
| CS0649 | `DllImportAttribute` (sdk) | Поля задаются именованными аргументами там, где атрибут стоит. |
| CS0649 | тестовые объекты (`Probe2`, `TwoFields`) | Тесту нужна форма объекта, не значение поля. |
| CS0649 | `ExceptionHooks.*Handler`, `SehDispatch.FrameChainAvailable` | Точка подключения, которую пока никто не занимает; флаг ставит `CoreClrProbe`, которого нет при `-SkipCoreClr`. |
| CS0169 | статические структуры с `fixed`-буфером (`BootLog`, `PerfCounters`, `SehUnwind`, …) | Обращение только через `fixed (… = s.Buf)` Roslyn использованием не считает. |
| CS0169, CS0414, CS0649 | поля оригинала BCL (`CultureData`, `DateTimeFormatInfo`, `TimeZoneInfo`, `Exception`) | Порт сохраняет поля оригинала; читающий их код вырезан. |
| CS8500 | GC, рантайм, EH | Рантайм берёт указатели на управляемые типы — это его работа. |
| CS9216 | `Threading.Lock` | `Lock` построен на `Monitor`, взятом на самом `Lock`. |
| CS1058 | AOTTESTS, проба аппаратного сбоя | Общий `catch` после `catch (Exception)` и есть предмет проверки. |

## Устаревшее

| Код | Что | Куда переходить |
|---|---|---|
| SHARPOS0001 | `AppHost.WriteString`, `WriteChar`, `WriteUInt`, `WriteHex` | Пишут мимо стандартного вывода: труба и перенаправление их не видят. Вывод программы — `Console.Write`; рисование на терминале (строка ввода, приглашение, полноэкранный интерфейс, отчёт без аллокатора) — `AppHost.Terminal`. |

## Чужой код

Чужой код не правим ради тишины: дифф против апстрима дороже. Коды глушит
`.editorconfig` рядом с ним, только для его путей.

| Где | Коды | Причина |
|---|---|---|
| `apps_native/Fami/Fami/`, `apps_native/TriCNES/TriCNES/` | CS0162, CS0168, CS0169, CS0414, CS0675 | Подмодули апстрима; `.editorconfig` уровнем выше. |
| `vendor/XtermSharp/` | CS0162, CS0168, CS0169, CS0414 | Апстрим. |
| `vendor/SharpCompress/` | CS0162 | Апстрим: ветки для `\` при `Path.DirectorySeparatorChar` = `'/'` недостижимы. |
| `vendor/Terminal.Gui/Terminal.Gui/` | CS0649 | Апстрим; `SharpOS/` — наш и не заглушён. |
| `generators/Json.Generator` (`NoWarn` в csproj) | CS1574, RS2008, RS1036, nullable | Генератор System.Text.Json из форка: ссылки в XML-доках и правила анализаторов Roslyn — как в сборке dotnet/runtime, где они отключены; netstandard2.0 без аннотаций BCL. |
| `vendor/BabyKusto/src/opensource/unmodified/` | CS8604 | Скопировано из dotnet/runtime без изменений. |
| `JsApp`, `LuaApp` (`NoWarn` в csproj) | CS0436, CS8632, CS0649 | Исторически; кандидат на перенос в `.editorconfig`. |
