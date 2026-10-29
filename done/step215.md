# step 215 — пути через `/`, как в Unix; `PATH` через `;`; CoreCLR не тронут

## Почему

Оболочка — bash, и `\` в ней экранирует следующий знак. А система везде
печатала пути с `\` (`cannot start \bin\`, `INIT=\bin\SHELL.EXE`, листинг,
`pwd`): скопированный из вывода путь превращался в другой. Справка оболочки
прямо предупреждала («a backslash is an escape here»). Решение — не учить
оболочку исключениям, а перевести систему на `/`.

## Что было

FAT (`Fat32.Resolve`, `EnumDirLocked`, `TrySplitParent`) и так принимал оба
разделителя; ядро путей не нормализует. `\` насаждали слои выше:

- SDK: `Process.ResolvePath` переводил каждый `/` в `\`, `AppFile.ToAscii` — тоже;
- std `System.IO.Path` — Windows-срез: `\`, буквы дисков, «C:\sharpos\» как
  текущий каталог;
- оболочка, лаунчер, константы ядра, `run_build.ps1` (`system.env`).

## Что стало

- **std `System.IO.Path` — поведение Unix** (по `Path.cs` + `Path.Unix.cs`
  BCL, не порт файла): `/`, `PathSeparator = ';'` (как у CoreCLR, не `:` Unix), корень один, `GetFullPath`
  от настоящего текущего каталога процесса, `GetDirectoryName("/a") == "/"`,
  `("/") == null`, `Combine` на `null` бросает, как в BCL.
- **SDK:** `ResolvePath`/`NormalizePath` выдают `/`; `\` на входе — тоже
  разделитель (FAT не допускает его в именах; старые скрипты и настройки),
  на выходе не бывает. `FindProgram` и дополнение по Tab делят `PATH` по `;`
  и по `:` (`Process.SplitPathList`). В bash `;` разделяет команды:
  `export PATH="$PATH;/more"` — в кавычках. `Directory.GetFiles`,
  `FullName`, `DriveInfo` — `/`.
- **Ядро:** `/config/system.env`, `INIT=/bin/LAUNCHER.EXE`,
  `PATH=/bin;/tests;/games`, `HOME=/home`, текущий каталог по умолчанию `/`.
  Ключи кэша программ и журнала загрузки — одно написание
  (`Fat32.PathKey`): `\bin\x.exe` и `/BIN/X.EXE` были двумя записями кэша, а
  журнал загрузки хранился без ведущего разделителя.
- **Оболочка:** `pwd`, приглашение, `ls` (`dir/`), `df`, `cp`/`mv`, `which` —
  `/`; `\` — экранирование, как в bash; справка без предупреждения.
- **Лаунчер, KQL, Lua:** дерево файлов, плитки, истории `/sh_history`,
  `/kql_history`; Lua `?.lua;/bin/lua/?.lua` (`;` — правило самой Lua).

## CoreCLR

Не тронут и не проверялся — по решению: пересадить ярус на модель Unix целиком
позже, а не переводить пути на лету. Одно вынужденное место: PAL
(`SharpOSHost_GetFullPathName`) звал std `Path.GetFullPath` с Windows-
семантикой; эта реализация перенесена дословно в `PAL/SharpOSHost/WindowsPath.cs`,
CoreCLR получает те же ответы. `.DLL` из оболочки и лаунчера теперь приходят
с `/` — долг в donext.

## Проверка

Рабочая копия, WHPX, `-SkipCoreClr`. Батарея с добавками: AOTTESTS 319/319 ×3
и 196/196, PROCTEST 85/0 и 80/0, PIPEPERF 20, JSONTEST 64, GVMTEST 10, KQLTEST 7,
KQLBABY 9; DATATEST 222/0 и 219/0 под `--gc-stress` (после правки собственной
проверки: `\bin\..\t24` при относительном `t24` — не тот каталог). Новые проверки
DATATEST: `Path` как в Unix, `\` через API к тому же файлу и `/` на выходе,
`cd '\tests'`, `which` с `/`, `PATH` с `;` и `:` вперемешку. В оболочке:
`pwd` → `/home`, `which PIPEGEN` → `/bin/PIPEGEN.EXE`, `ls /`, `system.env`,
Lua `package.path`, `cat '\home\p215.txt'` читает файл, `cat \home\p215.txt` —
bash съел `\` (`/home/homep215.txt`), как и задумано. Глазами: лаунчер
(`9 entries in /`), приглашение `/home $`, DOOM из оболочки нашёл
`DOOM1.WAD` через `Path.GetDirectoryName(MainModule.FileName)`.

## Уроки

- Рабочая копия без `payloads/` (в `.gitignore`): DOOM без WAD выходит с 1 —
  не путать с регрессией.

## Файлы

- `std-no-runtime/Bcl/Path.cs` — Unix; `EnvironmentStore.*.cs`, `Threading.cs` — `/` по умолчанию.
- `OS/src/PAL/SharpOSHost/WindowsPath.cs` (новый), `CrtHeapStubs.cs` — Windows-модель для CoreCLR.
- `apps_native/sdk/Process.cs`, `AppFile.cs`, `FileSystem.AppHost.cs`, `FileSystemInfo.AppHost.cs`, `ProcessStub.cs`.
- `OS/src/Kernel/Process/BootConfig.cs`, `AppProcess.cs`, `AppServiceBuilder.Processes.cs`, `LauncherBoot.cs`, `ProgramCache.cs`; `OS/src/Hal/Fat32.Lock.cs` (`PathKey`); `Diagnostics/DiskYieldProbe.cs`.
- `apps_native/Shell/Executor.cs`, `Completion.cs`, `LineReader.cs`, `autorun.sh`.
- `apps_native/Launcher/FileTree.cs`, `Tiles.cs`, `Program.cs`; `KqlApp`, `LuaApp`.
- Тесты: `AotTests`, `ProcTest/Children.cs`, `DataTest/FileApi.cs`, `Names.cs`, `Grinder.cs`, `ProgramCacheTest.cs`, `ShellCommands.cs`.
- `run_build.ps1` (`system.env`), README, `docs/test-rig.md`, limits, donext.
