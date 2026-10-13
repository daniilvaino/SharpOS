# step 207 — окружение процесса, раскладка образа, плитки, `.env`, сборка без тестов, hosted из приложения

Первый пункт порядка ведущего агента (ответы на выкладку 13b): окружение в ядре, std и PAL, разведка запуска pwsh. До этого — сверка, которую он попросил по шагам 205–206. В шаг же вошла правка экрана с фото пользователя (step206: `FF` в конце CBOR).

## 1. Сверка по шагам 205–206

- **PROCTEST 15 под стрессом:** порог 1/10 вместо 1/4. Проверка ловит голодание (≈ 0 %), а под WHPX и стрессом доля 20–21 %.
- **externalpipe: версии и контрольная сумма.**
  - Заголовок: `"SharpOS externalpipe\n"`, u32 версия формата (1), u32 версия раскладки блока, ключ корня, описание.
  - Перед каждым блоком: длина и CRC-32C (`BitOperations.Crc32C` из порта BCL).
  - `Region.LayoutVersion = 1`: записи, ключи, смещения, строки, массивы, таблица и корзины Expando. Её меняют вместе с раскладкой.
  - Чужая версия — отказ по имени, а не неверное чтение. Испорченный блок — отказ там, где он есть.
  - `WRITE --append` различает «не externalpipe» и «externalpipe другой версии».
- **Образцовые файлы.**
  - `apps_native/DataTest/esp/sample-record.pipe` и `sample-expando.pipe` записаны этой сборкой (`DATATEST --write-samples`) и сняты с образа (`mcopy`).
  - `run_build.ps1` кладёт `apps_native/<прил>/esp/*` рядом с программами.
  - `*.pipe` в `.gitattributes` — двоичные.
  - DATATEST 18: оба образца читаются как записаны; повреждённый блок и чужая версия раскладки — `READ` с кодом 1.
- **Полный проход перед правкой — замер** (`[perf207]`, WHPX): на Expando из 108 полей (15 КБ) проход 5,5 мкс, правка 1 мкс. Проход заметен. Перенос его «только перед сжатием» требует проверить каждую правку на чтение сырых адресов мимо `ViewScope.Record` — в `donext`, не в этом шаге.
- **`run_build.ps1 -AutorunScript`** — свой сценарий вместо батареи.

## 2. Экран и `FF` (с фото пользователя)

`ScreenText` придерживал конец `byte[]`, похожий на начало символа UTF-8, и брал за ведущий любой байт `≥ F0`. Сообщение `FF` (конец массива CBOR) уходило в запас, а запас в конце выбрасывался.

Исправлено:
- ведущие только `C2..F4`;
- невостребованный запас печатается байтами;
- конец вывода на экран его сбрасывает (`ScreenText.End` в `PipeWriter.Dispose`, `WriteTo`, `ExternalPipe.Replay`).

## 3. Окружение процесса

- **Ядро.**
  - `AppProcess.Environment` — массив `ИМЯ=значение`, не меняется на месте. Потомок получает копию от `StartProcess` или то, что попросил старт.
  - Код ядра и то, что ядро запускает само, — `ProcessEnvironment.Boot`, пока пустое; заполнит файл настройки.
  - Службы процессов: 15 — прочитать блок, 16 — заменить, 17 — старт с окружением. Отдельный код, а не хвост запроса op 1: старое приложение с запросом из 6 слотов не прочтётся за его концом.
- **std.**
  - `EnvironmentBlock` (общий, ядро тоже) — поиск без учёта регистра, как в Windows, замена, блок `"A=1\0B=2\0"`, разбор с отказом на пустое имя, двойное имя или блок больше 64К символов.
  - Ярусная пара `EnvironmentStore`: в приложении через службу, без кэша; в ядре — загрузочное окружение.
  - `System.Environment`: `GetEnvironmentVariable`, `SetEnvironmentVariable` с проверками BCL. `CurrentDirectory` теперь рабочая папка процесса; раньше std всегда отвечал `\`, хотя папка есть с step197.
  - `GetEnvironmentVariables` нет: она отдаёт необобщённый `IDictionary`, которого у нас нет.
- **SDK.** `Process.WorkingDirectory` стоит на `Environment.CurrentDirectory`. `Process.Start(…, environment:)`.
- **PAL.** `SharpOSHost_GetEnvVar` сначала смотрит окружение (pwsh идёт на потоках ядра — загрузочное), затем прежние подстановки; значения в UTF-8, а не по байту на символ.
- **Проверка:** PROCTEST 20, 6 проверок:
  - имя в другом регистре;
  - копия у потомка;
  - рабочая папка у потомка;
  - явное окружение старта;
  - удаление;
  - отказ имени с `=`.

## 4. Разведка: pwsh из плитки

- PowerShell уже запускается после загрузки: путь `.DLL` лаунчера и оболочки → служба `RunManagedApp` → `CoreClrHost.TryExecute` на сессии CoreCLR, поднятой при загрузке (BigStack 16 МиБ). Вызов синхронный, консоль та же, лаунчер на это время отпускает экран (`driver.Suspend` / `Resume`).
- `coreclr_initialize` — раз за загрузку, и второй не нужен.
- Второй `execute_assembly` NormalHello работает. Второй старт pwsh падает на статике (`CommandLineParameterParser._dirty`): тот же ALC.
- Выгружаемый ALC на запуск проверен пробой (`SHARPOS_ALC_PROBE=1`), полнота выгрузки — нет. После выхода остаётся всё: куча, сборки, потоки.
- Для плитки: сначала один запуск с защитой от второго, затем ALC на запуск.

## 5. Файл настройки и раскладка образа

- **Раскладка:**
  - `\bin` — программы, демо-стадии;
  - `\tests`;
  - `\games` — с WAD и картриджем;
  - `\config`;
  - `\home`;
  - `\system` — пусто;
  - `\sharpos` — как было.

  `\apps` удаляется из дерева сборки.
- **Категория приложения** — `<SharpOsCategory>` в `.csproj`, без неё `bin`; читает `tools/Apps.ps1`. Данные приложения (`<прил>\esp\*`) — в его папку.
- **`\config\system.env`** (`BootConfig`, ядро) — его пишет `run_build.ps1`.
  - Строки `ИМЯ=значение`, `#`, `$ИМЯ` и `${ИМЯ}` из уже заданных.
  - `INIT` — программа с аргументами: лаунчер, а с `-Autorun` — `\bin\SHELL.EXE \config\autorun.sh`.
  - `BUILDINFO=on` — строка сборки у программ.
  - Остальное — загрузочное окружение.
  - Нет файла — значения по умолчанию.
- **Ядро:**
  - запускает `INIT` с аргументами;
  - рабочая папка того, что ядро запускает само, — `HOME`;
  - служба 18 — путь образа процесса.
- **SDK:**
  - `Process.FindProgram` — поиск по `PATH`;
  - `Process.Start` с голым именем ищет по `PATH`, относительный путь — от рабочей папки;
  - `Process.ImagePath`;
  - `MainModule.FileName` — настоящий путь, DOOM находит WAD рядом с собой.
- **Оболочка:**
  - программа — по `PATH`, путь — только слово с `/` или `\`, как в sh, так что `NAME.EXE` тоже ищется;
  - сценарий — аргументом: `SHELL.EXE файл`;
  - `cd` без аргумента — в `HOME`;
  - Tab дополняет по `PATH`.
- **Эмуляторы:** `/games/GAME.NES`.
- **Тесты:**
  - DATATEST читает образцы из `/tests/`, пишет в рабочую папку `\home`;
  - PROCTEST — `\tests\AOTTESTS.EXE`;
  - `autorun.sh` — голые имена.
- **Лаунчер:**
  - вкладки «Главная» (плитки: оболочка, PowerShell, игры из `\games`) и «Файлы» (дерево всего тома);
  - стрелки ходят по плиткам, Enter и щелчок запускают;
  - PowerShell — один раз за загрузку, второй — отказ с объяснением.
- **Попутно:** в `Shell/Executor.cs` с прошлых шагов стоял байт BEL вместо `\a` в комментарии (heredoc) — переписан.
- **Документы:** README (раздел «Раскладка образа»), `test-rig.md`, `payloads/README.md`.

## 6. Разбор `.env` — `std-no-runtime/Formats/Dotenv`

- Формат `env_file` Docker Compose, написан по `compose-go` (`dotenv/parser.go`, `template/template.go`) так, чтобы принимать и отвергать те же файлы с теми же сообщениями.
- Регулярные выражения compose-go заменены сканированием с той же семантикой: в std нет `Regex`.
- Их тесты перенесены в `std-no-runtime/Formats/Dotenv.Tests` (хостовый проект): 195 проверок, все проходят. Уведомления MIT и Apache — рядом с кодом. В README — в «Отдельном спасибо».
- `\config\system.cfg` стал `system.env`: `BootConfig` читает его этим разбором; файл с ошибкой — строка в журнале и значения по умолчанию.
- В std добавлен `string.LastIndexOf(char, int, int)` из BCL.
- Стенд (`C:\android-home\rig\deploy.ps1`, `install.sh`):
  - `config/system.env` отправляется каждый раз;
  - `apps/` и `config/system.cfg` удаляются с флешки;
  - `install.sh` удаляет и папки.

## 7. Сборка без тестов — `run_build.ps1 -NoTests`

- `-p:NoTests=true` → `SHARPOS_NO_TESTS` → `Probes.Tests = false`.
- 57 переключателей-проверок стали `= Tests`:
  - EH-ворота;
  - стрессы потоков и аллокатора;
  - smoke-тесты;
  - службы ядра для AotTests;
  - перепись CoreCLR при загрузке.
- Включатели остались как были: прерывания, вытеснение, драйверы после EBS, сам CoreCLR, консоль.
- **Найдено:** `Scheduler.Init` вызывали только пробы потоков — без них планировщика не было. Теперь он поднимается явно в `BootSequence`. Самотест движка терминала отделён от включения консоли.
- **Диагностика — тоже под ключ:** `Probes.Diagnostics = Tests`.
  - Выключаются:
    - профилировщик и дампы потоков в простое;
    - перепись кучи и выборка выделений;
    - дампы USB-дескрипторов и эхо последовательного порта (2 с загрузки);
    - дампы `gcstatics` и RTC;
    - замер частоты CPU (50 мс);
    - блок `[perf] init`;
    - список `\bin`;
    - три пробные страницы.
  - Под `Tests` — вшитые в загрузку самотесты:
    - проверка страничной памяти и VM;
    - стенд EH 5.5a;
    - счётчик HPET и секундомер;
    - `VerifyTickIsMoving` (300 мс);
    - `FbRender`, `Ps2`.
  - Не трогаются: `[ebsx]` (оживляет HPET); пробы, которые и есть включение устройств (`SerialSmoke` → `Serial.Init`, `PciScan` → `Pci.Init`, `UsbScan` → `UsbHost.Discover`).
- **Загрузка с `-NoTests`:**
  - оболочка стартует на 132-й строке журнала ядра вместо 1520-й;
  - память ядра перед тестами — 24,5 тыс. страниц вместо 89,5 тыс.;
  - DATATEST 73/73;
  - PROCTEST 81/83: AotTests без тестовых служб и известная проверка ресурсов.

## 8. Hosted-сборка из приложения: TEB и корни под BigStack

PowerShell с плитки падал всегда, NormalHello из оболочки — на «new Thread + Join», а в `-NoTests` ещё раньше, потому что этот запуск там первый. Вытеснение ни при чём: опыт с `PreemptLauncher = false` дал то же самое.

- **TEB.** Отказ — в `JIT_PInvokeEnd+0x1e`: запись по `0x4` при `Thread* = null`. CoreCLR берёт свой `Thread*` из TLS через TEB, а планировщик восстанавливает GS из `Thread.Teb` при каждом переключении.
  - Загрузочному потоку TEB назначает `CoreClrProbe`, потокам рантайма — `Scheduler`. Потоку приложения, который зовёт `RunManagedApp`, — никто.
  - Первое же переключение на поток рантайма и обратно оставляло GS не там.
  - Теперь `CoreClrHost.EnsureCallerTeb` выдаёт вызывающему потоку свой TEB (`CoreClrTeb.Allocate`, границы — стек потока, как у загрузочного) и держит его до конца жизни потока.
- **Корни под BigStack.** После выхода NormalHello `[perf] run.<имя>` печатал чужую память (иероглифы).
  - Строку пути держал только кадр `RunManagedApp` — на стеке потока, ниже перехода на BigStack.
  - Точный обход сборщика ядра останавливается на шеллкоде `BigStack.RunOn` (нет размотки). Куча ядра за прогон росла, сборка до роста строку освободила.
  - Сейчас путь и имя на время прогона в статиках. Общая задача — размотка для шеллкода — в `donext`.

## 9. Стенд: пустые папки

На ноутбуке DATATEST упал (11/33): `WRITE: Unable to find the specified file.` Стенд переносит только файлы, и пустых `\home` и `\system` на флешке не было. Теперь их создают оба скрипта:
- `deploy.ps1` — все папки дерева ESP в промежуточной папке на каждый запуск;
- `install.sh` — все папки в образе до копирования файлов.

Проверено монтированием образа на телефоне только на чтение: `home`, `system` есть, `apps` нет.

## Проверка

Короткие сценарии под WHPX:
- DATATEST 73/73;
- PIPEPERF 19/19;
- PROCTEST 83, под стрессом 78.

Провалы — только «ресурсы как до тестов», когда программа идёт почти первой после загрузки (потоки 14 → 11; `donext` «Лишние живые потоки в первые секунды»).

После раскладки:
- образ: `bin`, `config`, `EFI`, `games`, `home`, `sharpos`, `system`, `tests`;
- `pwd` у оболочки — `\home`;
- `PIPEGEN 3 | KQL` прошёл;
- PROCTEST — все группы, рабочая папка потомка `\home`;
- DATATEST 73/73.

Лаунчер руками не проверен.

**Полные батареи** (сборка `ab167a7+dirty.1013-2237`):
- **QEMU под WHPX — 28/28:**
  - PROCTEST 83, под стрессом 78 (проверка 15: 10,8 % при пороге 10 %);
  - DATATEST 73 / 70;
  - PIPEPERF 19, JSONTEST 64, GVMTEST 10, KQLTEST 7, KQLBABY 9.
- **Ноутбук — 28/28** после починки стенда; там же DATATEST 73 / 70.
- Руками (пользователь), QEMU и ноутбук: плитки, PowerShell с плитки, `-NoTests`, `-NoBuild`.

## Файлы

- **ядро:**
  - новые: `Process/ProcessEnvironment.cs`, `Process/BootConfig.cs`;
  - `AppProcess` (`Environment`, `ImagePath`);
  - `AppServiceBuilder.Processes`: службы 15–18, окружение и путь образа в `StartFromPath` / `StartProcess`, рабочая папка `HOME`;
  - `AppServiceBuilder` (`RunManagedApp`: строки в статиках);
  - `LauncherBoot` (`INIT`, аргументы);
  - `Exec/CoreClrHost` (`EnsureCallerTeb`);
  - `PAL/SharpOSHost/EnvironmentPolicy`;
  - `Diagnostics/Probes` (`Tests`, `Diagnostics`), `Boot/BootSequence` (`Scheduler.Init`, самотесты под флагами), `ExitBootServicesProbe`, `CoreClrProbe`, `Memory/GcStaticsMaterializer`;
  - `OS.csproj` (`NoTests`);
- **std:**
  - новые: `EnvironmentBlock.cs`, `Environment.Variables.cs`, `EnvironmentStore.AppServices.cs`, `EnvironmentStore.Kernel.cs`, `Formats/Dotenv/` (`Parser.cs`, `Template.cs`, уведомления compose-go), `Formats/Dotenv.Tests/` (хостовые тесты);
  - `Threading.cs`: `Environment` — partial, `CurrentDirectory`;
  - `SystemString.cs`: `LastIndexOf(char, int, int)`;
  - `Exchange/Region.cs`: `LayoutVersion`;
  - `Pipes/ExternalPipe.cs`, `ScreenText.cs`, `Pipe.cs`, `PipeLoops.cs`;
  - `Std*.props`, `README.md` (шесть ярусных пар);
- **SDK:** `Process.cs` (`FindProgram`, `ImagePath`, окружение), `ProcessStub.cs` (`MainModule.FileName`), `PipeLines.cs`;
- **приложения:**
  - `Launcher`: `Tiles.cs` (новый), `Program.cs` — вкладки;
  - `Shell`: `Executor.cs` (`PATH`, `HOME`), `Program.cs` (сценарий аргументом), `Completion.cs`, `autorun.sh` (голые имена; DATATEST 73 / 70, PROCTEST 83 / 78);
  - `WriteApp`; `Fami`, `TriCNES` (`/games/GAME.NES`);
  - `DataTest`: `ExternalFiles.cs`, `Program.cs`, `esp/*.pipe`;
  - `ProcTest`: `Program.cs`, `Children.cs`;
  - `PipePerf/Edits.cs`;
  - `<SharpOsCategory>` в 13 `.csproj` (тесты, игры);
- **сборка:** `run_build.ps1` (раскладка, `system.env`, `-AutorunScript`, `-NoTests`, `-NoBuild`, данные приложений), `tools/Apps.ps1` (категория), `.gitattributes`;
- **стенд** (вне репозитория): `C:\android-home\rig\deploy.ps1`, `install.sh`;
- **документы:** README (трубы, раскладка, разборщики, `generators/`, compose-go), limits, `donext`, `build-setup.md`, `test-rig.md`, `payloads/README.md`.

## Дальше

Команды оболочки: `export`, `unset`, `env`, `which`, `$ИМЯ` (`donext`, «Файл настройки, раскладка образа, команды оболочки, плитки»). Затем PowerShell многоразовым — выгружаемый `AssemblyLoadContext` на запуск.
