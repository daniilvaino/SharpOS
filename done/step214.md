# step 214 — вывод программ навсегда уходил в COM1; недописанная строка при выходе; PROCTEST под `> f`; текст отказа двух писателей; куча приложения собирает перед ростом

## 1. Терминал терял вывод программ (находка тестеров)

**Симптом.** После `synth 1000 | pipespy a | kql 'extend …' | pipespy b |
kql 'take 10' | pipespy c | convert --to externalpipe | convert --from
externalpipe | pipespy d` весь дальнейший вывод — программ, оболочки, кадры
лаунчера — шёл на последовательный порт хоста, а не в эмулятор терминала.
Разово тот же механизм: первая строка `BENCHAOT | KQL "take 3"` попала в
журнал ядра (COM1), а не на экран.

**Механизм.** `Platform.WriteChar` при `s_writing != 0` (повторный вход:
сбой или прерывание внутри записи) пишет знак только в сырой порт COM1 —
мимо терминала и журнала на диске.

**Детектор.** Счётчик таких обходов для каналов программ с `s_writing`,
флагом разметки GC, каналом, держателем записи и текущим потоком — в COM1,
первый и каждый 1000-й. Цепочка тестера дважды: первый обход —
`writing=1 holder=0x1f thread=0x22` (пишет другой поток, пока 0x1f внутри
записи), дальше — `writing=ffffffffffffffff` навсегда.

**Корень.** `finally { Preemption.Allow(); s_writing--; }`. `Allow` при
отложенном тике (step196) тут же делает `Scheduler.Yield`: писатель уходил
с процессора со счётчиком 1, и все, кто писал в это время, шли в COM1. Если
в это время уходил поток процесса (`take` закрыл трубу, стадии выше
кончаются), `LeaveApp` → `AbandonWrite` обнулял счётчик под чужой записью;
писатель возвращался и делал −1. Ненулевой навсегда.

**Исправление.** Сначала `s_writing--`, потом `Allow()`. Тот же порядок
проверен в остальных `Suppress`/`Allow` ядра: после `Allow` там только
локальные присваивания.

**Проверка.** Цепочка тестера дважды подряд: обходов 0, `echo after-chain-1`
и `-2`, `PIPEGEN 3` — в журнале программ (до правки — в COM1), PIPESPY d
получил 10 записей.

## 2. Недописанная строка терялась при выходе программы

`Console.Write` в трубу копит строку до перевода строки (`PipeTextWriter`); точки
выхода у SDK не было, и хвост без перевода строки пропадал вместе с
процессом.

- `AppRuntime.Finish(code)` — сбрасывает `Console.Out`, `Console.Error`,
  stdtext (`SystemConsole.FlushAtExit`, `Pipe.FlushText`) и возвращает код;
  отказ трубы (читатель ушёл) кода не меняет. Вызывается из
  `Environment.Exit` и на возврате `SharpAppEntry` всех 32 приложений.
- **Находка по дороге: `SharpAppBootstrap` — мёртвый код.** В `link.rsp`
  два `/ENTRY`: `SharpAppBootstrap` из `FreestandingPe.props` и
  `SharpAppEntry` от `EntryPointSymbol`; последний побеждает. Первая
  версия крючка стояла в бутстрапе и не вызывалась ни разу — показал
  детектор (`[finish-detector]` не напечатался ни у LUA, ни у JS).
  `RuntimeImports.ManagedStartup()` из бутстрапа тоже не выполняется —
  долг в donext.
- LUA: stdout Lua буферизован, как stdio C, и lua.c сбрасывает его при
  выходе силами CRT; здесь `io.write` без `io.flush` терялся. Точка входа
  LUA сбрасывает его перед `Finish`.

## 3. PROCTEST под `> f` и `| …`: 80/5 вместо 85/0

Проверки 3, 12, 16, 17 и общая сверка сравнивали счётчики ядра всей
машины, а строки отчёта PROCTEST — обменные блоки, пока следующая стадия
их не освободит; освобождает она когда успеет, внутри окна замера.
`StableStats()` ждёт, пока число блоков перестанет меняться (внутри окна
PROCTEST не печатает, дети пишут в терминал). С читателем, держащим блоки
до конца входа (KQL копит пакет), общая сверка может разойтись по-прежнему.

## 4. Отказ двух писателей одной трубы

Второй писатель с другим типом получал текст для пары писатель — читатель:
«writer sends X, reader expects Y». `TypeCheck.CompareWriters` — «the first
writer declared X, this one Y»; строка программы — «types do not match»
вместо «the input is of another type» (отказ бывает и у выхода).

## 5. Текст объектов: массив, многоточие, цвета, замер KQL

- **`{ ? }` у объектов в массиве** (`StorageFacts[1] { StorageFacts { ? } }`):
  две причины. Массив съедал уровень глубины печати (`View.Append`), и объект
  внутри выходил свёрнутым `{ … }`; а `…` в шрифте терминала нет — всё
  незнакомое рисуется `?`. Элементы-объекты теперь печатаются на глубине
  массива (массив в массиве уровень тратит — иначе массив сам в себе не
  кончится); у шрифта — свой глиф многоточия (`FontCp437.Ellipsis`, номер
  256, три точки размера `.`), таблица MOOS не тронута.
- **LESS показывал ESC буквально** (`^[[36m`): как `less -R`, SGR
  (`ESC [ … m`) проходит целиком и не занимает столбца; при сдвиге вбок
  цвета до края сохраняются, в конце строки сброс. Подсветка поиска в
  цветных строках не рисуется. Прочие ESC — по-прежнему `^[`.
- **`[kql] start ms`** шёл в поток ошибок — на экран после каждого запроса и
  в `2>`. Теперь `AppHost.WriteDiagnostic`: журнал и COM1, не экран.

## 6. OOM на больших входах: куча приложения держала бюджет мусором

Прогон по списку тестеров: `take 45` на кусках READ по 64 КиБ — 45;
CONVERT после ушедшего `take` уходит с 141; LESS держит 100 000 строк.
Падал только `SYNTH 100000 | KQL "summarize count() by Level"`:
`[oom] request=524312 free=48846240 in 34971 blocks largest=516000`.

- `summarize` BabyKusto копирует все столбцы всех строк по группам
  (upstream TODO), списки растут удвоением — в неперемещающей куче это
  дробление; новый сегмент на 1 МиБ не влез в бюджет 64 МиБ, хотя 48 МБ
  свободны.
- Бюджет 64 МиБ — от старого пула в образе (до step196). С ленивой кучей
  у приложений осталась политика «собирать, только когда расти некуда»: каждая
  стадия конвейера набирала весь бюджет физических страниц мусором до первой
  сборки (в прогоне KQL — две сборки по миллиону объектов).
- Теперь приложения собирают перед ростом, как ядро
  (`GcHeap.CollectBeforeGrowing`, куча удвоилась с прошлой сборки); бюджет —
  весь диапазон кучи (640 МиБ); `HeapCommit` отказывает, когда у машины
  остаётся меньше 64 МиБ (`AppProcesses.KernelReserveBytes`).

Итог: `summarize` на 100 000 и на 300 000 (`count(), avg(Weight)`), две KQL
подряд, `summarize … by tolong(Level)` над JSON Lines из файла. По полю
Expando без `tolong` — отказ KQL «dynamic is not allowed», это правило языка.

## Проверка

Рабочая копия, WHPX, `-SkipCoreClr`. Батарея с добавками:
`[sh] done ran=47 failed=1` (после §6, с его строками) (единственный — `SYSINFO 3>&1`, ожидаемый код 1);
AOTTESTS 319/319 ×3 и 196/196, PROCTEST 85/0 (и 80/0 в `--gc-stress`),
DATATEST 216 и 213, PIPEPERF 20, JSONTEST 64, GVMTEST 10, KQLTEST 7,
KQLBABY 9; цепочка тестера — 10 записей в PIPESPY d, `echo after-chain` в
журнале программ. Отдельно:
- `LUA … io.write('lua-partial') io.flush() > p.txt` → в файле; `lua-full`
  без flush → в файле; JS → в файле;
- `PROCTEST > /home/proctest.txt` → в файле `passed 85, failed 0` (тестер:
  `cannot start \bin\`, было 80/5); `PROCTEST | PIPECNT` → код 0;
- `SYSINFO 3>&1 | PIPECNT` → 1, «types do not match: … the first writer
  declared …SystemInfo, this one System.String»;
- `SYSINFO 3>&- | CONVERT --to lines` → `StorageFacts[1] { StorageFacts {
  Kind = "ahci", Mounted = true, FileSystem = "FAT32" } }`; `[kql] start ms` —
  только в журнале ядра. LESS с цветом и глиф `…` на экране — глазами.

## Уроки

- `Allow()` может переключить поток: состояние критической секции
  (счётчики, флаги) снимать до него.
- Крючок, который «не срабатывает», — сначала проверить, что код вообще
  живой: здесь был мёртв весь бутстрап.
- `proctest > f` у тестера (`cannot start \bin\`) после переделки `> f`
  в step212 не воспроизвёлся.

## Файлы

- `OS/src/Hal/Platform.cs` — порядок в `WriteChar`.
- `apps_native/sdk/AppRuntime.cs` — `Finish`, текст несовпадения типов.
- `apps_native/sdk/SystemConsole.cs`, `std-no-runtime/Pipes/PipeEnds.cs` — сброс при выходе.
- `apps_native/sdk/Environment.AppHost.cs` — `Exit` через `Finish`.
- `apps_native/*/Program.cs` (32) — возврат `SharpAppEntry` через `Finish`.
- `apps_native/LuaApp/Program.cs` — сброс stdout Lua.
- `apps_native/ProcTest/Program.cs` — `StableStats`.
- `std-no-runtime/Pipes/View.cs` — глубина элементов массива.
- `OS/src/Hal/FontCp437.cs`, `FontCp437.Glyphs.cs` — глиф многоточия.
- `apps_native/LessApp/Program.cs` — SGR как `less -R`.
- `apps_native/KqlApp/Program.cs` — замер старта в диагностику.
- `std-no-runtime/GC/GcHeap.cs`, `apps_native/sdk/AppRuntime.cs` — сборка перед ростом у приложений.
- `OS/src/Kernel/Process/AppProcess.cs`, `AppServiceBuilder.Processes.cs`, `std-no-runtime/GC/GcMemorySource.AppStatic.cs` — бюджет кучи и резерв ядра.
- `std-no-runtime/Pipes/TypeCheck.cs`, `OS/src/Kernel/Pipes/KernelPipes.cs` — текст двух писателей.
