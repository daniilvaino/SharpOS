# step026

Дата: 2026-04-04  
Статус: завершён

## Цель

Разделить:

- SDK приложений (`apps/sdk`);
- разработку no-runtime standard library/runtime (`std/`);
- слои ОС (`OS_0.1/src/Boot|Hal|Kernel|TestApp`).

Главный критерий: часть замены отсутствующей `NoStdLib`-библиотеки должна жить отдельно и быть общей для OS и app.

## Что сделано

### 1) Зафиксировано структурное разделение контуров

Добавлены:

- `std/README.md`
- `std/no-runtime/README.md`

В `README.md` репозитория добавлен раздел **Контуры Репозитория** с явным правилом:

- `apps/sdk` = app SDK;
- `std/` = std/runtime development;
- код ОС остаётся в `OS_0.1/src/*`.

### 2) String experiments вынесены в std-контур

Перенос:

- `string experiments/` -> `std/string-experiments/`

Обновлены пути в:

- `std/string-experiments/README.md`
- `std/string-experiments/run_matrix.ps1`
- `std/string-experiments/run_matrix_values.ps1`

### 3) Вынесен общий no-runtime модуль для OS+app

Добавлен общий файл:

- `std/no-runtime/shared/MemoryPrimitives.cs`

Содержит общие реализации:

- `Memset`
- `Memcpy`
- `Memmove`

### 3.1) Вынесена общая реализация строк в std

Добавлены общие файлы:

- `std/no-runtime/shared/SystemString.cs`
- `std/no-runtime/shared/StringAlgorithms.cs`
- `std/no-runtime/shared/StringRuntime.RhNewString.cs`
- `std/no-runtime/shared/StringRuntime.Fallback.cs`
- `std/no-runtime/shared/DefaultMemberAttribute.cs`

Смысл:

- `System.String` и `Concat` больше не живут только в `apps/sdk`;
- строковая реализация теперь централизована в `std/no-runtime/shared`.
- добавлены операторы `string ==` и `string !=` в общей реализации `System.String`.

### 4) Общий no-runtime модуль подключён в обе сборки

Обновлены проекты:

- `OS_0.1/OS_0.1.csproj`  
  подключает `MemoryPrimitives.cs`, `SystemString.cs`, `StringAlgorithms.cs`, `StringRuntime.Fallback.cs`
- `apps/HelloSharpFs/HelloSharpFs.csproj`  
  подключает `MemoryPrimitives.cs`, `SystemString.cs`, `StringAlgorithms.cs`, `StringRuntime.RhNewString.cs`

### 5) Локальные runtime stubs переведены на общий модуль

Обновлены:

- `OS_0.1/src/Boot/MinimalRuntime.cs`
- `apps/sdk/MinimalRuntime.cs`
- `apps/sdk/StringAlgorithms.cs`

Изменение:

- экспортируемые `memset/memcpy/memmove` теперь делегируют в `SharpOS.Std.NoRuntime.MemoryPrimitives`.
- в `apps/sdk` оставлен только thin-wrapper `StringAlgorithms`, который делегирует в `SharpOS.Std.NoRuntime.StringAlgorithms`.
- в `OS_0.1/src/Boot/MinimalRuntime.cs` добавлены `System.Type`/`System.RuntimeType`, чтобы shared string surface не ломал AOT-кодогенерацию ядра.

### 6) Обновлены string experiments под новые операторы и стабильный runtime-прогон

Обновлены:

- `apps/sdk/StringExperimentSuite.cs`
- `apps/HelloSharpFs/Program.cs`
- `std/string-experiments/run_matrix.ps1`
- `std/string-experiments/run_matrix_values.ps1`

Изменение:

- добавлены тесты `EXP_TEST_07` (`StringEqualsLiteral`) и `EXP_TEST_08` (`StringNotEquals`);
- в test-режиме `HelloSharpFs` больше не уходит в интерактивный launcher-loop и сразу делает `Exit`, чтобы runtime-матрица не зависала на вводе.

## Важная фиксация архитектуры

- `apps/sdk` не переиспользуется как “общая stdlib”.
- Всё, что относится к развитию no-runtime std/runtime (общий слой для OS и app), живёт в `std/`.
- OS-слои не становятся местом для std-экспериментов.

## Проверка

Проверка в рамках шага:

- структура каталогов разделена;
- общий no-runtime файл физически вынесен из OS/app;
- оба проекта подключают один и тот же shared-файл;
- runtime stubs в OS и app используют общий shared-код.

Прогон команд:

1. `./build_app_freestanding_wsl.ps1 -NoCopy`  
   - freestanding `HELLOCS.ELF` и `HELLOCS.ELF.abi` собраны успешно.

2. `./run_build.ps1 -NoRun`  
   - ядро и EFI-образ собираются успешно после подключения `std/no-runtime/shared`.

3. `./std/string-experiments/run_matrix.ps1`  
   - compile-matrix: `pass=12 fail=0`.

4. `./std/string-experiments/run_matrix_values.ps1`  
   - runtime-matrix: `pass=12 fail=0` (включая новые тесты 07/08).

## Итог

Step 26 оформил явное организационное разделение:  
**SDK приложений отдельно, std/runtime развитие отдельно, код ОС отдельно**, с реальным общим no-runtime модулем, используемым и в OS, и в app pipeline.
