# step 211 — Monitor отдаёт слоты, сведения о системе (SystemInfo), LESS/HIST, окно консоли, -SkipCoreClr без hosted-полезной нагрузки, разбор отчётов тестеров

## Контекст

Шаг собран из нескольких дел между step210 и разбором трёх отчётов тестеров
(`work/ostest/`, чёрный ящик через QMP: `send-key` и `screendump`). Отчёты
сведены в donext («Находки тестеров»), часть находок разобрана до корня здесь.

## 1. Monitor: слот возвращается (`std-no-runtime/Threading.Monitor.cs`)

Находка тестера 2: интерактивный KQL «иногда пишет, что все мониторы заняты».
Текст на деле: `Monitor: no free lock slots. The table is a fixed 64 entries`.

Корень: у объекта нет места под замок в заголовке, поэтому `lock (x)` связывает
объект со слотом боковой таблицы на 64 записи. Слот занимался в `SlotFor` и не
освобождался никогда: `Exit` снимал владельца, объект оставался в `s_owners`.
64 разных объекта, хоть раз бывших в `lock`, исчерпывали таблицу на всю жизнь
процесса и держались ею живыми. Kusto.Language берёт `lock (this)` в
`KustoCodeService` и `MostRecentlyUsedCache` на каждое дополнение.

Воспроизведение (набор третьего тестера, скопирован в `work/ostest/ostest4`):
`pipegen 100 | kql`, затем по одному `wh` + Tab + Ctrl+U — свежий процесс KQL
падал на 14-м дополнении, стабильно.

Исправление:

- `s_users` — претенденты на слот: владелец, входящие и ждущие. Последний
  `Exit` без ждущих возвращает слот; на его месте метка «удалён», чтобы цепочки
  линейного поиска не рвались. Повторный вход отдаёт лишнюю претензию сразу.
- Занятие и освобождение — под коротким замком таблицы (`s_tableLock`, ожидание
  через `WaitWhile`), никогда не во время ожидания самого замка.
- Хеш — по идентичности (`RuntimeHelpers.GetHashCode`), не `obj.GetHashCode()`.
- `TryEnter(timeout)` держит одну претензию на всё ожидание.

64 — теперь предел одновременно взятых замков. Проверено в госте (образ
`1025-0156`): 100 дополнений в одном процессе, затем `where Level > 3 | count`
→ `Count = 20`. Загрузочная стресс-проба замков в этом образе не шла (собран
без тестов).

## 2. Сведения о системе: SystemInfo

Механизм первых шагов `PlatformCapabilities` — маска из 7 флагов, заполняемая
до ExitBootServices: что умела прошивка, а не система (`ExternalElf` назван по
ELF, которых нет с step137; `Graphics`, `MonotonicTimer` не выставляются).

- `std-no-runtime/SystemInfo.cs`, `SharpOS.Std.SystemInformation`: запись
  `SystemInfo` из `[Message]`-записей Build, Firmware, Cpu, Memory, Storage[],
  Input[], Display, Timers, Bus, Power. Ядро и приложения компилируют один
  исходник — запись проходит по трубе как есть; читатель с меньшим набором
  полей читает видом по имени. Пространство имён не `…Platform`: внутри
  `SharpOS.Std.*` голое `Platform` (класс `OS.Hal.Platform`) нашло бы его.
- `OS/src/Kernel/SystemFacts.cs`: факты собираются из подсистем в момент
  запроса (`BootDisk`, `Fat32`, `Framebuffer`, `Hpet`, `LocalApic`, `Pci`, `Xhci`,
  `DeviceIrq`, `Power`, `CpuFeatures`, `Madt`, `CoreClrHost`, `Probes.Tests`).
  Вендор прошивки и объём памяти копируются в фазе 2, пока память UEFI её.
  `CpuFeatures.AvxState` запоминает состояние AVX.
- Служба процесса, операция 21: ядро пишет одну `SystemInfo` в трубу с именем
  от программы. SDK: `Process.GetSystemInfo()` (труба `sysinfo.<id>`).
- Программа SYSINFO (`apps_native/SysInfoApp`): на экран — по разделам, в трубу
  — запись.

**В госте не проверено** — компилируется (IL ядра, SYSINFO собран). Маска
`PlatformCapabilities` пока на месте. SYSINFO по модели каналов вывода
сольётся в FETCH (donext).

## 3. AOTTESTS на образе без тестов — разобрано

На образах `-NoTests` AOTTESTS выходит 134 сразу, пересборка не помогает.
Символизация по PDB приложения: `RIP=0`, возврат в `AppEntry.Probe+0x4b`, вызов
из `Run` (строка 95, `Probe(13, …)`), `call rbx` с RBX=0. `Probe` зовёт
`PipeProbeAddress` без проверки, а `Probes.PipeProbe = Tests` — ядро без тестов
службу не публикует. Батарея собирается с тестами, поэтому поломку не было
видно. Исправление — в donext.

## 4. Ctrl+C и PowerShell — разобрано

Две проблемы (donext): ядро не хранит режим консоли и поднимает событие, когда
PSReadLine снял processed input (а 0x03 уходит в PSReadLine → нет
`MapVirtualKey` → FailFast); обработчики `SetConsoleCtrlHandler` переживают
выход pwsh. Рядом: `InputPump.DetectBreak` с потерянным отпусканием Ctrl делает
из `c` Ctrl+C — так тестер 1 упал при наборе `convert`.

## 5. Мелочи шага

- **LESS и HIST** (`apps_native/LessApp`, `HistApp`): пейджер в конце конвейера
  и ASCII-гистограмма по полю; написаны тестером 1 на нашем SDK без чтения
  чужих исходников.
- **Лаунчер:** плитка PowerShell только если на образе есть
  `\sharpos\PowerShellBootstrap.dll`; Enter на обычном файле во вкладке Files —
  `READ ФАЙЛ | LESS`.
- **`run_build.ps1 -SkipCoreClr`:** в `\sharpos` только `bootlog.txt` и
  `fswrite.bin`; hosted-полезная нагрузка (CoreLib, `fx`, pwsh, `tpa.txt`,
  NormalHello, Bench, Starling) не кладётся, остатки прошлой полной сборки
  удаляются. Блок — в `else` того же файла.
- **Окно консоли в SDK** (`SystemConsole.cs`): `WindowWidth`/`WindowHeight`,
  `BufferWidth`/`BufferHeight`, `SetCursorPosition`, `CursorVisible`, `Title`.

## 6. Решения, записанные в plan/donext

- **Каналы вывода программы:** ядро знает только номера концов; stdout
  объектный у всех, `Console.WriteLine` и потоки над stdin/stdout — те же трубы;
  stdtext — явный текст наших программ; подсказки — в описании трубы; оболочка
  по умолчанию выводит всё; терминал на две панели (лента слева, инспектор
  объектов справа) с прокруткой.
- **Пути:** у ярусов разные соглашения (`\` без дисков против `C:\`).
- **Подсветка синтаксиса для редактора** — после порта Regex; кандидат —
  движок AvaloniaEdit (`.xshd`).
- Апстрим вендоринга сверен: ShellSyntaxTree вышел 0.4.0 (наш 0.3.5),
  Kusto.Language +2 автосинка, TriCNES +12 коммитов, Fami +1; остальное без
  изменений.

## Файлы

- `std-no-runtime/Threading.Monitor.cs`, `SystemInfo.cs`, `Std.props`
- `OS/src/Kernel/SystemFacts.cs`, `OS/src/Kernel/Process/AppServiceBuilder.Processes.cs`,
  `OS/src/Boot/BootSequence.cs`, `OS/src/Hal/CpuFeatures.cs`
- `apps_native/sdk/Process.cs`, `apps_native/sdk/SystemConsole.cs`
- `apps_native/SysInfoApp/`, `apps_native/LessApp/`, `apps_native/HistApp/`
- `apps_native/Launcher/Program.cs`, `Tiles.cs`
- `run_build.ps1`
- `plan.md`, `donext.md`, `docs/nativeaot-nostd-kernel-limits.md`

## Дальше

Каналы вывода программы (donext), шаг 1: `Console` → stdout/stderr трубами,
stdtext.
