# step 199 — сбой под `--pipe-stress 2`: запись исключения, брошенная на большом стеке CoreCLR; AVX-состояние и FMA в ядре; SharpLibm как C-библиотека

## Контекст

Батарея step198 изредка падала на третьей строке (`AOTTESTS --pipe-stress 2`):
#GP с `RIP=0x98000000007DB865`, следом #PF в XtermSharp, #DF, паника. С
CoreCLR сбой шёл в 3 прогонах из 4, без CoreCLR не воспроизводился. Шаг —
корень этого сбоя, плюс две работы, накопленные с step198: AVX-состояние для
FMA в ядре и SharpLibm как C-библиотека.

## 1. Сбой: цепочка от корня до паники

**Корень.** Ядро запускает CoreCLR на загрузке (`NormalHello.dll`) на
отдельном стеке `BigStack` (16 МиБ). Внутри этой сессии бывают страничные
отказы, которые обрабатывает сама CoreCLR. `HwFaultBridge.DispatchTrap`
ставил свою `ExInfo` (в своём кадре) в голову цепочки исключений
(`ExInfoHead.s_head`) и отдавал отказ CoreCLR (`TryCoreClrHandleHardwareException`)
или нативному SEH (`SehDispatch.DispatchFromHwFault`). Обработчик находился,
в `DispatchTrap` управление не возвращалось — запись оставалась в голове
цепочки потока 1, указывая в мёртвый кадр на `BigStack` (всегда 0x3158 байт
от верха).

**Следствия.**

1. После сессии `BigStack` переиспользуется; по адресу записи — чужие байты.
2. Сборщик ядра обходит цепочки чужих потоков (`MarkOtherThreadStacks` →
   `MarkExceptionChain`) и проходит по «месту выброса» из этих байтов. Обычно
   там `ctx=0` — безвредно. Изредка — неканонический адрес: #GP в
   `RunFromThrowSite` посреди пометки.
3. Пометка — младший бит указателя на `MethodTable`. Сборка оборвана, все
   живые объекты остаются помеченными, и следующий виртуальный вызов читает
   слот таблицы со сдвигом на байт: `0x98…`/`0xE8…` — это слот
   `SATADevice`, прочитанный через `MethodTable+1` (сверено по байтам образа:
   слот `0x5D60B0` = `0x7D822B20`, сдвиг даёт ровно `0xE8000000007D822B`).
4. Отчёт об отказе печатается в дисковый лог → FAT → тот же `SATADevice` →
   вложенный отказ, отчёт пишется поверх недописанного в терминал → #PF в
   XtermSharp → #DF.

**Как нашли.** Сперва — ложная версия «RSP съехал на байт» (адрес из
вложенного отказа выглядел как адрес возврата); детектор сдвига RSP на тиках
не сработал ни разу. Разгадка — `RAX = SATADevice::vftable + 1` в полном
отчёте одного из прогонов. Дальше:

- без CoreCLR (`Probes.CoreClrInit=false`) мусорных записей в цепочках нет;
- проверка на выходе из `BigStack.RunOn` показала: запись остаётся там в
  каждой загрузке, а не изредка — падение случайно, утечка нет;
- аппаратная точка останова на `s_head` утечку прятала (8 загрузок из 8
  чистые); кольцевой буфер ненулевых записей `s_head` по местам-писателям
  указал на `HwFaultBridge`, вектор #PF.

Без правки: утечка в 8 загрузках из 8; с правкой — 0 из 8, и 24 загрузки
укороченной батареи (15 подряд, 9 параллельно) без единого маркера.
Отказы, которые забирает CoreCLR, в логе не видны: строка `HW fault:`
печатается после постановки записи, и из-за этого путь сперва сочли мёртвым.

**Исправления.**

- `HwFaultBridge`: запись снимается перед передачей в CoreCLR и нативный SEH
  и ставится обратно, если оба отказались. Корень.
- `BigStack.RunOn`: голова цепочки на выходе восстанавливается, как у
  `JumpStub.Run`; если пришлось — одна строка в COM1.
- `RunFromThrowSite`: форма адреса проверяется до чтения по нему — мусор в
  цепочке не роняет сборщик посреди пометки.
- `GcMark.InProgress` (от `Begin` до конца `GcSweep.Run`): пока объекты
  помечены, вывод ядра идёт только в COM1 (`Platform.WriteChar`). Туда же —
  вложенный вывод (отказ внутри терминала, дискового лога или порта).
  `JumpStub.LeaveApp` сбрасывает счётчик (`Platform.AbandonWrite`).
- `HwFaultBridge`: отказ во время отчёта о другом сначала печатает внешний
  отказ в COM1 (`[nested fault]`) — раньше его строка обрывалась.
- `Serial.WriteHex` — для отчётов, которым консоль нельзя.

**Уроки.** Наблюдение меняет гонку: точка останова на переменной убрала
утечку, которую должна была поймать; дешёвый кольцевой буфер со сбросом
только при утечке — нет. Граница стека (`JumpStub.Run`, `BigStack.RunOn`) —
место, где голова цепочки исключений обязана вернуться к прежней. Отказ во
время пометки сборщика — не локальная ошибка: помеченные объекты ломают
всякий виртуальный вызов, в том числе в коде отчёта.

## 2. AVX-состояние и FMA в ядре

- `BootSequence.ConfigureXcr0`: при XSAVE и AVX в CPUID (и непустом листе
  0xD) — `CR4.OSXSAVE`, XCR0 = x87|SSE|AVX, прерывания переходят на XSAVE
  (`Idt.UseXsave`, `X64Asm.UseXsave`); иначе прежний замок x87|SSE и FXSAVE.
  Проверка листа 0xD — до записи в CR4: на qemu64 с `+xsave,+avx` лист пуст,
  и `mov cr4` с OSXSAVE не завершался (зависание загрузки, снято через QMP).
- Стаб прерываний: `sub rsp,840; and rsp,-64; xsave`; возврат: `XSTATE_BV &= 7`
  и обнуление байтов 8..23 заголовка перед `xrstor` — иначе #GP (живой дамп
  показал `XSTATE_BV=0x7e4cf007`).
- Кооперативное переключение остаётся на FXSAVE: это вызов, верхние половины
  YMM в Win64 волатильны.
- `CpuFeatures`: FMA включается при FMA3 и AVX-состоянии — `LibmPatcher`
  переписывает `fma`/`fmaf` на `vfmadd`. AotTests: «libm: fma rounds once» (298).
- CoreCLR остаётся на SSE: PAL отвечает 0 на `GetEnabledXStateFeatures`.
- `-cpu max`: `XCR0 = x87|SSE|AVX, interrupts save XSAVE (832 B)`,
  `fma=yes; libm entries replaced: 12`.

## 3. SharpLibm (сабмодуль, dd70337)

Коротко: C-библиотека из того же C# (NativeAOT NoStdLib, без CRT, без
импортов, 161 экспорт, генерируемый `sharplibm.h`; `sharplibm.dll`/`.so`
родной сборкой на Windows и Linux); функции Бесселя, `erfinv`/`erfcinv` с
шагом Ньютона (≤1.33 ulp), `compoundn`/`compoundnf` (C23); исправлены
`fmaf` на субнормальных результатах и NaN в `sincosf`; `modf` через `trunc`.
Тесты — репозиторий SharpLibm-tests (glibc libm-test, Berkeley TestFloat,
LLVM libc, MPFR для специальных функций, C-проверка экспортов).

## 4. Проверка

Полная батарея, qemu64 (путь без AVX, `XCR0 lock skipped … legacy FXSAVE`):
`[sh] done ran=24 failed=2`, строки `[bigstack] … entry left` нет. Ранее на
`-cpu max` — AVX-путь и FMA (§2). Два провала —
`--gc-stress 16 4` и `--gc-stress 1 1 --pipe-stress 1` (код 134): объект
исключения аппаратного отказа приложения освобождается во время
диспетчеризации (`Exception.AppendStackFrame` читает заливку `0xCC`).
Воспроизводятся с CoreCLR и без, к этому сбою отношения не имеют — следующий
шаг.

## 5. Файлы

- `OS/src/Boot/EH/HwFaultBridge.cs` — снятие записи для CoreCLR/SEH, `[nested fault]`.
- `OS/src/Kernel/Memory/BigStack.cs` — восстановление головы цепочки.
- `OS/src/Kernel/Memory/KernelGcPreciseWalk.cs` — проверка формы до чтения.
- `std-no-runtime/GC/GcMark.cs`, `GcSweep.cs` — `GcMark.InProgress`.
- `OS/src/Hal/Platform.cs` — вывод во время сборки и вложенный вывод в COM1.
- `OS/src/Hal/Serial.cs` — `WriteHex`.
- `OS/src/Kernel/Exec/JumpStub.cs` — `AbandonWrite` в `LeaveApp`.
- `OS/src/Boot/BootSequence.cs`, `OS/src/Hal/CpuFeatures.cs`,
  `OS/src/Hal/Idt/Idt.cs`, `IdtTrampolines*.cs`, `OS/src/Hal/X64Asm*.cs` — AVX/XSAVE/FMA.
- `apps_native/AotTests/Program.cs`, `apps_native/Shell/autorun.sh` — проверка FMA, 298.
- `SharpLibm` — сабмодуль на dd70337.
- Документы: limits, README, CLAUDE.md, donext.

## 6. Дальше

1. Два провала под `--gc-stress` (объект исключения отказа приложения).
2. KQL в конвейере — фильтры и глаголы.
3. Математика по процессору — что ещё переключать по CPUID.
