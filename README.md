# SharpOS

Экспериментальная операционная система, целиком написанная на C#: загрузка, ядро, приложения и пользовательское окружение. Не на C# только [форк CoreCLR](https://github.com/daniilvaino/dotnet-runtime-sharpos/tree/sharpos/coreclr-port). Собирается обычным `dotnet publish -r win-x64`.

На SharpOS уже запускаются стоковый [**PowerShell 7.6.5**](#powershell) и играбельный [**DOOM**](#doom).

[![SharpOS launcher](media/screenshot.png)](media/screenshot.png)

## Как запустить

### Окружение

Зависимости ставит [mise](https://mise.jdx.dev) по [`mise.toml`](mise.toml), так что начать нужно с него:

```bash
winget install jdx.mise        # Windows
brew install mise              # macOS
curl https://mise.run | sh     # Linux
```

Дальше репозиторий и зависимости:

```bash
git clone --recurse-submodules https://github.com/daniilvaino/SharpOS.git && cd SharpOS

# по желанию: форк CoreCLR для hosted-яруса; без него сборка идёт с -SkipCoreClr
git clone -b sharpos/coreclr-port https://github.com/daniilvaino/dotnet-runtime-sharpos.git

mise trust && mise bootstrap   # Linux со старым индексом apt: mise bootstrap --update
```

Чтобы инструменты сами попадали в PATH внутри репозитория, mise нужно один раз включить в оболочке:

```bash
echo 'eval "$(mise activate bash --shims)"' >> ~/.bashrc   # bash
echo 'eval "$(mise activate zsh --shims)"'  >> ~/.zshrc    # zsh
```

```powershell
Add-Content $PROFILE 'mise activate pwsh --shims | Out-String | Invoke-Expression'
```

### NixOS

На NixOS (и любом Linux с nix) mise не нужен: его установку и `mise bootstrap` заменяет [`flake.nix`](flake.nix) с двумя оболочками. Нужны включённые флейки (`nix-command flakes`), репозиторий клонируется так же, как выше.

Форк CoreCLR (по желанию) собирается в своей оболочке — его Arcade качает собственный SDK, поэтому ей нужен FHS. Первый вход требует splat MSVC; команду печатает сама оболочка, делается один раз:

```bash
nix develop .#fork
xwin --accept-license --cache-dir .xwin-cache --manifest-version 17 \
     --sdk-version 10.0.26100 --crt-version 14.44.17.14 --arch x86_64 \
     splat --preserve-ms-arch-notation --include-debug-libs --output .xwin-cache/splat
cd dotnet-runtime-sharpos && pwsh ./build_clr_sharpos.ps1 -Clean && cd .. && exit
```

Ядро, приложения и запуск — во второй оболочке:

```bash
nix develop
pwsh ./build_launcher.ps1
SHARPOS_GUI=1 pwsh ./run_build.ps1 -UsbOnly    # без форка добавить -SkipCoreClr
```

### Сборка и запуск

Payloads необязательны: `payloads/DOOM1.WAD`, картриджи `.nes` и PowerShell для самой SharpOS в `payloads/pwsh/PowerShell-7.6.5-win-x64/`. Что куда класть, написано в [`payloads/README.md`](payloads/README.md).

Форк CoreCLR необязателен: он нужен только для [hosted-яруса](#три-яруса-исполнения), то есть для стоковых .NET-программ вроде PowerShell. Без него ядро собирается с флагом `-SkipCoreClr`:

```powershell
./build_launcher.ps1
$env:SHARPOS_GUI=1; ./run_build.ps1 -UsbOnly -SkipCoreClr
```

С форком сначала собирается он сам, а `-SkipCoreClr` не нужен:

```powershell
cd dotnet-runtime-sharpos; ./build_clr_sharpos.ps1 -Clean; cd ..
./build_launcher.ps1
$env:SHARPOS_GUI=1; ./run_build.ps1 -UsbOnly
```

`run_build.ps1` собирает ядро, делает образ и запускает QEMU. Остальные приложения собираются так же, как лаунчер: `build_fetch` / `aottests` / `benchaot` / `doom` / `shell` / `tricnes` / `fami`. Из bash и zsh скрипты запускаются через `pwsh`: `SHARPOS_GUI=1 pwsh ./run_build.ps1 -UsbOnly -SkipCoreClr`.

Лог ядра (COM1) пишется в `last_build.log`, вывод программ (COM3) в `last_app.log`, их ошибки (COM4) в `last_err.log`.

## Архитектурные инварианты

### 1. Весь исходный код на C#

В дереве нет ни одного файла `.c`, `.cpp`, `.h`, `.asm` или `.s`. MSBuild и PowerShell только оркестрируют сборку, логики системы в них нет. Всё низкоуровневое (обработчики прерываний, сохранение callee-saved регистров, write barrier'ы, трамплины interface dispatch) делается одним из трёх способов:

1. **Средства самого C#:** `[RuntimeExport]`, `[UnmanagedCallersOnly]`, `delegate* unmanaged`, `fixed`, арифметика указателей.
2. **Машинный код из C#.** Ассемблер [Iced](https://github.com/icedland/iced) пишет его в исполняемый буфер:
   - *При сборке* source generator `BootAsm.Generator` заранее превращает 16 стабов раннего старта (interface dispatch, трамплины IDT, EH-фанклеты, GC stack spill, порты ввода-вывода) в байтовые шаблоны. При загрузке они копируются в буфер, и в них подставляются адреса managed-колбэков.
   - *В рантайме*, когда куча, GC и исключения уже работают, Iced вызывается напрямую: `new Assembler(64); a.mov(rax, rcx); a.Assemble(writer, rip);`.
3. **Нативные символы данных из атрибута.** Если линкеру нужен символ, который ILC не эмитит (например, `__security_cookie`), MSBuild-задача `CoffStub.Generator` находит атрибут в коде и сама создаёт `.obj`:

   ```csharp
   [BootAsm.CoffDataSymbol("__security_cookie", Section = ".data", Alignment = 8)]
   public static ulong SecurityCookie = 0x2B992DDFA232UL;
   ```

   Через ту же задачу приложения из `apps_native/` собираются как freestanding win-x64 PE.

Любая новая низкоуровневая задача решается одним из этих способов. Если кажется, что не решается, значит, она неверно поставлена. Так уже сделаны GC stack spill, чтение и запись CR3, interface dispatch с резолвером для shared generics, инициализация модулей NativeAOT без линкерных сентинелов.

Насколько нам известно, других ОС с таким инвариантом нет.

### 2. Имена из .NET только для совместимых реализаций

Реализация получает каноническое имя в `System.*` или `System.Collections.Generic.*`, только если полностью соблюдает публичный контракт BCL (с оговорками из [`docs/nativeaot-nostd-kernel-limits.md`](docs/nativeaot-nostd-kernel-limits.md)). Частичные и нестандартные живут в своих пространствах имён: `SharpOS.Std.*`, `OS.Kernel.*`. Так LINQ и другой код BCL со временем можно будет брать из dotnet/runtime как есть.

## Три яруса исполнения

| Ярус | Что на нём работает | Где лежит | Чем собирается |
|---|---|---|---|
| **Kernel-AOT** | ядро, загрузка, драйверы, планировщик | `OS/` | NativeAOT + NoStdLib + свой MinimalRuntime |
| **PE-app** | приложения: `LAUNCHER.EXE`, `AOTTESTS.EXE`, `DOOM.EXE` и другие | `apps_native/` | NativeAOT + NoStdLib + общий SDK из `apps_native/sdk/` |
| **CoreCLR-hosted** | стоковые .NET DLL байт-в-байт | `\sharpos\*.dll` на FAT | форк CoreCLR, статически слинкованный с ядром |

Ограничения описаны в двух документах: [`docs/nativeaot-nostd-kernel-limits.md`](docs/nativeaot-nostd-kernel-limits.md) для первых двух ярусов (std у них общая) и [`docs/coreclr-hosted-limits.md`](docs/coreclr-hosted-limits.md) для третьего.

## Что работает

- ✅ работает, подтверждено прогоном: пробы в [`OS/src/Kernel/Diagnostics/`](OS/src/Kernel/Diagnostics/), гейт в [`tools/probe_report.ps1`](tools/probe_report.ps1)
- 🟡 частично
- ⏳ запланировано, см. [`plan.md`](plan.md)
- 🔴 пока нет или сломано, но достижимо
- 🚫 невозможно или неприменимо по архитектуре

### Язык и рантайм

| Возможность | Kernel-AOT | PE-app | CoreCLR-hosted | Комментарий |
|---|:-:|:-:|:-:|---|
| `new T()`, управляемая куча | ✅ | ✅ | ✅ | |
| `string`, примитивы, структуры | ✅ | ✅ | ✅ | |
| Boxing / unboxing | ✅ | ✅ | ✅ | |
| `try` / `catch` / `finally` / `throw;` / фильтры `when` | ✅ | ✅ | ✅ | |
| Аппаратный сбой → managed-исключение (`#PF` → `NullReferenceException`) | ✅ | ✅ | ✅ | |
| `Exception.StackTrace` | 🟡 | 🟡 | 🟡 | hosted: пуст для исключений из C++-кода CLR; AOT: после `throw;` теряются имена кадров |
| Исключение в статическом конструкторе → `TypeInitializationException` | ✅ | ✅ | 🟡 | в hosted пробрасывается как есть, без обёртки |
| `[ModuleInitializer]` | 🔴 | ⏳ | ✅ | атрибут в std есть, но инициализатор не вызывается |
| `yield return` | ✅ | ✅ | ✅ | |
| `async` / `await` | ✅ | ✅ | ✅ | в std без захвата контекста синхронизации |
| Делегаты и лямбды | ✅ | ✅ | ✅ | в std порт из dotnet/runtime без рефлексии, GVM, open-instance и вариантных приведений |
| Generic sharing (`__Canon`) | ✅ | ✅ | ✅ | |
| Виртуальные вызовы и interface dispatch | ✅ | ✅ | ✅ | |
| Generic `as T` / `(T)x` с `where T : class` | 🟡 | 🟡 | ✅ | в AOT не работает вариантное приведение к интерфейсу |
| Ковариантность массивов (`stelem.ref`) | 🟡 | 🟡 | ✅ | в AOT проверок нет: запись чужого типа даёт тихое UB вместо `ArrayTypeMismatchException` |
| Многомерные массивы (`int[,]`) | 🟡 | 🟡 | ✅ | без ненулевых нижних границ и `int[*]` |
| `typeof(T)` / `System.Type` | 🔴 | 🟡 | ✅ | в приложениях только сравнение через `==`: ни `Name`, ни членов |
| `record`, `init`-аксессоры | 🔴 | ✅ | ✅ | записям нужен `typeof`, а в ядре его нет |
| Рефлексия: `System.Reflection`, `Activator.CreateInstance(Type)`, `Type.GetType(string)` | 🔴 | 🔴 | ✅ | в AOT нет метаданных |
| `Reflection.Emit`, `dynamic` / DLR, `Expression<T>.Compile()` | 🚫 | 🚫 | ✅ | нужен JIT |
| `AssemblyLoadContext` (несколько ALC) | 🚫 | 🚫 | ⏳ | нужен JIT |

### Сборка мусора

| Возможность | Kernel-AOT | PE-app | CoreCLR-hosted | Комментарий |
|---|:-:|:-:|:-:|---|
| GC (mark-sweep, точное сканирование стека) | ✅ | ✅ | ✅ | hosted: свой GC через PAL; у каждого PE-приложения свой сборщик и своя куча |
| `GC.Collect` | ✅ | ✅ | ✅ | `GC.WaitForPendingFinalizers`, вероятно, зависает (не перепроверяли) |
| Write barrier (`RhpAssignRef`, `RhpStelemRef`) | ✅ | ✅ | ✅ | в AOT пустышка: сборщик без поколений |
| `WeakReference` / `GCHandle` | 🔴 | 🔴 | ✅ | в наших сборщиках нет слабых дескрипторов |
| `ConditionalWeakTable<K,V>` | 🔴 | 🟡 | ✅ | ссылки сильные: запись живёт, пока жива таблица |

### Стандартная библиотека

| Возможность | Kernel-AOT | PE-app | CoreCLR-hosted | Комментарий |
|---|:-:|:-:|:-:|---|
| Коллекции (`List<T>`, `Dictionary<K,V>` и т. д.) | ✅ | ✅ | ✅ | порты из BCL |
| `SortedDictionary` | 🟡 | 🟡 | ✅ | внутри сортированный массив: вставка линейная |
| LINQ | ✅ | ✅ | ✅ | своя мини-реализация `System.Linq.Enumerable` |
| `string.Format` / `StringBuilder.AppendFormat` | 🟡 | 🟡 | ✅ | покрытие частичное |
| `System.Enum` | 🟡 | 🟡 | ✅ | нет `ToString`, `Parse`, `GetNames` |
| `Enum.IsDefined` | 🔴 | 🟡 | ✅ | в приложениях всегда `true`: метаданных перечислений нет |
| `DateTime` / `TimeSpan` | ✅ | ✅ | ✅ | `Now` берётся из CMOS; без источника времени вернёт начало эпохи (см. `IsRealClock`) |
| `Array.Copy` с перекрытием (семантика memmove) | ✅ | ✅ | ✅ | |
| `Math.Abs`, `Math.Sqrt` | ✅ | ✅ | ✅ | |
| `Math.Floor` / `Ceiling` / `Truncate` / `Round` | ✅ | ✅ | ✅ | в AOT только для \|x\| < 2^63 |
| `Math.Sin` / `Cos` / `Exp` / `Log` / `Pow` | 🟡 | 🟡 | 🟡 | приближения: ~1e-9 в AOT, грубее в hosted; в AOT нет `Tan`, `Atan`, `Asin`, `Acos` и гиперболических |
| `Vector128<T>` (SSE) | ✅ | ✅ | ✅ | `Vector256` объявлен, но не ускорен |
| Разбор XML | ✅ | ⏳ | ✅ | TurboXml; ядро читает им манифесты приложений |
| Коллекции `Concurrent.*` и `Immutable.*`, `SortedSet`, `BitArray`, `KeyedCollection`, `Array.BinarySearch`, `Regex`, `ValueTuple`, `DateTimeOffset` | 🔴 | 🔴 | ✅ | пока не портированы; `Tuple<T1,T2>` есть |

### Потоки и синхронизация

| Возможность | Kernel-AOT | PE-app | CoreCLR-hosted | Комментарий |
|---|:-:|:-:|:-:|---|
| `Thread.Start()` | ✅ | 🟡 | ✅ | в приложениях вместо `Thread` есть `AppThreads.Spawn` и `Task.Run` |
| `Task.Run`, `Task.Delay` | ✅ | ✅ | ✅ | на пуле потоков; потоки приложения умирают вместе с ним |
| `ThreadPool.QueueUserWorkItem` | ⏳ | ⏳ | ✅ | |
| `lock` (`Monitor.Enter` / `Exit`) | ✅ | ✅ | ✅ | `Pulse` / `Wait` намеренно не реализованы |
| `Interlocked.CompareExchange` | ✅ | 🟡 | ✅ | в std это заглушка без `LOCK`, верна только для одного потока; ядро вызывает настоящий `LOCK CMPXCHG` напрямую |
| `Event` / `Semaphore` / `Mutex` | ✅ | ⏳ | ✅ | |
| Кооперативные `Yield()` / `Sleep(ms)` | ✅ | ✅ | ✅ | в приложениях через `AppThreads.Sleep` |
| Многопоточные процессы | ✅ | ✅ | ✅ | |
| Вытеснение потоков | 🟡 | ⏳ | 🟡 | пока включается только вокруг проб, остальное ядро кооперативное; в hosted вытесняется и JIT-код |
| SMP | ⏳ | ⏳ | ⏳ | |

### Железо и ввод-вывод

| Возможность | Kernel-AOT | PE-app | CoreCLR-hosted | Комментарий |
|---|:-:|:-:|:-:|---|
| Прямой доступ к железу (CR3, PCI, MMIO, IDT) | ✅ | 🚫 | 🚫 | |
| Ассемблер x64 на Iced (при сборке и в рантайме) | ✅ | 🚫 | 🚫 | пока без EVEX |
| Свои аппаратные прерывания (local APIC, тик 100 Гц) | 🟡 | 🚫 | 🚫 | устройства опрашиваются, IO-APIC не поднят |
| AVX / AVX-512 | 🔴 | 🔴 | 🔴 | в XCR0 включены только x87 и SSE |
| USB (xHCI) | 🟡 | 🚫 | 🚫 | свой стек, проверен на железе: HID-клавиатура, флешки (BOT + SCSI), лог в CDC-ACM. Без прерываний, хабов и мыши |
| Чтение файлов | ✅ | ✅ | ✅ | свой FAT, работает и после ExitBootServices |
| Запись файлов | 🟡 | 🔴 | 🔴 | FAT32: перезапись на месте и создание файлов (8.3). Нет удаления, роста файлов и каталогов, LFN |
| Сеть | 🔴 | 🔴 | 🔴 | нет драйвера сетевой карты |
| Ввод с клавиатуры в консоли | ✅ | ✅ | ✅ | |

### Приложения и процессы

| Возможность | Kernel-AOT | PE-app | CoreCLR-hosted | Комментарий |
|---|:-:|:-:|:-:|---|
| Код возврата процесса | ✅ | ✅ | ⏳ | |
| Вложенные запуски приложений | ✅ | ✅ | 🚫 | до 4 уровней |
| Terminal.Gui | 🚫 | ✅ | ⏳ | на нашей std, свой драйвер поверх терминала ядра; мыши нет |
| Оболочка с синтаксисом bash | 🚫 | 🟡 | 🚫 | `&&` `\|\|` `;`, встроенные `cd` `pwd` `ls` `cat` `echo` `expect` `exit`, запуск `.EXE` и `.DLL`; без каналов, перенаправлений и аргументов программам |
| Изоляция процессов через MMU | 🚫 | 🚫 | 🚫 | это unikernel |
| Параллельное исполнение по одному виртуальному адресу | 🚫 | 🚫 | 🟡 | потоки в одном ALC работают, несколько ALC в планах |

Всё, что сломано, виснет или ждёт доработки, собрано в [`limits.md`](limits.md).

## Структура репозитория

- `OS/src/` - ядро и его слои: `Boot`, `Hal`, `Kernel`, `PAL`.
- `apps_native/` - приложения (лаунчер, оболочка, батарея AotTests, DOOM) и общий SDK в `apps_native/sdk/`.
- `apps_managed/` - стоковые .NET-программы для яруса CoreCLR-hosted.
- `std/no-runtime/` - замена стандартной библиотеки: порты BCL и runtime-хелперы. Общая для ядра и приложений.
- `vendor/` - сторонние библиотеки, у каждой свои `LICENSE` и `PROVENANCE.md`.
- `done/` - хроника разработки: пошаговые разборы с архитектурой, трассами и решениями.

Всё, что касается std и рантайма, развивается в `std/`, а не в слоях ОС: новая строковая или утилитная операция сначала появляется там, а уже потом используется из ядра и SDK. Текущая цель: расширять `std/no-runtime/` и постепенно заменять unsafe-код управляемым. `unsafe` остаётся только на границах ABI и там, где без прямого доступа к железу не обойтись.

## DOOM

[![DOOM на SharpOS](media/doom_small.gif)](media/doom_small.gif)

[ManagedDoom](https://github.com/sinshu/managed-doom) собран как PE-приложение поверх нашей std: WAD читается с FAT32, картинка выводится через GOP с увеличением 2× на весь экран, управление с PS/2-клавиатуры, 35 Гц по HPET. [Полная гифка (41 МБ)](media/doom.gif)

## PowerShell

[![PowerShell 7.6.5 на SharpOS](media/pwsh.png)](media/pwsh.png)

Стоковый PowerShell 7.6.5 грузится с FAT32 на голом железе до интерактивного приглашения и выполняет настоящие командлеты: `Get-ChildItem`, `Get-Content`, конвейеры, переменные, `[DateTime]::Now`. Это самый требовательный тест всего стека сразу: TPL, исключения, рефлексия, GC, FAT32, ANSI-консоль.

PSReadLine работает полностью: цвета, Tab-дополнение, история по стрелкам, редактирование строки. Всё это поверх нашей framebuffer-консоли на XtermSharp. Ctrl+C прерывает выполняющуюся команду.

Ограничения: режим ConstrainedLanguage; история не сохраняется между запусками (в hosted FAT32 только на чтение).

## Сторонние приложения

- **[ManagedDoom](https://github.com/sinshu/managed-doom)** (sinshu, GPL-2.0) - играбелен. GPL-код изолирован в отдельном приложении и в ядро не линкуется.
- **[TriCNES](https://github.com/100thCoin/TriCNES)** (Chris Siebert, MIT, подмодуль) - эмулятор NES, точный: 141/141 на [AccuracyCoin](https://github.com/100thCoin/AccuracyCoin), но для игр бывает медленноват.
- **[Fami](https://github.com/RupertAvery/Fami)** (David Khristepher Santos, MIT, подмодуль) - эмулятор NES, играбелен, не идеален.

## Вендоринг и библиотеки

Чужой код, который лежит в дереве и попадает в образ. Лицензии этих проектов нас обязывают. Копии живут в `vendor/<имя>/`, рядом `LICENSE` и `PROVENANCE.md`: что взято и что вырезано.

- **[dotnet/runtime](https://github.com/dotnet/runtime) + [runtimelab](https://github.com/dotnet/runtimelab)** (Microsoft, MIT) - toolchain NativeAOT, форк CoreCLR в `dotnet-runtime-sharpos/` и сотни портов BCL в нашу std.
- **[Iced](https://github.com/icedland/iced)** (icedland, MIT) - кодировщик x86-64. Им пишется весь ассемблер проекта: и при сборке, и на лету.
- **[PeNet](https://github.com/secana/PeNet)** (Stefan Hausotte, Apache-2.0) - разбор PE в загрузчике приложений.
- **[Terminal.Gui](https://github.com/gui-cs/Terminal.Gui)** (Miguel de Icaza и участники, MIT) - библиотека текстового интерфейса. На ней написан лаунчер.
- **[XtermSharp](https://github.com/migueldeicaza/XtermSharp)** (Miguel de Icaza, MIT) - движок эмулятора терминала: ANSI/VT, сетка ячеек, прокрутка.
- **[TurboXml](https://github.com/xoofx/TurboXml)** (Alexandre Mutel, BSD-2-Clause) - разбор XML без аллокаций. Читает манифест приложения из ресурсов PE.
- **[ShellSyntaxTree](https://github.com/Aaronontheweb/ShellSyntaxTree)** (Aaron Stannard, Apache-2.0) - разбор командной строки bash в дерево. На нём стоит оболочка; половина для PowerShell не компилируется.
- **[MOOS](https://github.com/nifanfa/MOOS)** (nifanfa, Unlicense) - драйверы `AHCI`, `Disk`, `PCI(Express)` и глифы CP437. Адаптированы под наш HAL, лежат в `OS/src/`.
- **[Font 8x8](https://github.com/dhepper/font8x8)** (Daniel Hepper, Public Domain) - глифы framebuffer-консоли.

## Отдельное спасибо

Проекты, на которых SharpOS учился, по убыванию вклада.

- **[zerosharp](https://github.com/MichalStrehovsky/zerosharp)** (Michal Strehovský) - отправная точка: UEFI hello world на NativeAOT, с которого SharpOS начался.
- **[ManagedDotnetGC](https://github.com/kevingosse/ManagedDotnetGC)** (Kevin Gosse, MIT) - образец mark-sweep-сборщика.
- **[UpsilonGC](https://github.com/kkokosa/UpsilonGC)** (Konrad Kokosa, GPL-3) - пример собственного GC для .NET.
- **[DiscUtils](https://github.com/DiscUtils/DiscUtils)** (Kenneth Bell, MIT) - структура FAT и GPT.
- **[ChaN FatFs](https://elm-chan.org/fsw/ff/)** (BSD-1-clause) - второй ориентир по FAT.
- **[Cosmos](https://github.com/CosmosOS/Cosmos)** (BSD-3) - сама идея managed-ОС.
- **[shitty](https://github.com/pg83/shitty)** (Anton Samokhvalov, MIT + GPL-3) - тесты для эмулятора терминала.

## Лицензия

[CC0 1.0 Universal](LICENSE) - общественное достояние. Используй, изменяй и распространяй в любых целях, в том числе коммерческих.
