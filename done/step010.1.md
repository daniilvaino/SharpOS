# step010.1

Дата: 2026-04-01

## Цель

Перед `jump` добавить обязательную пост-загрузочную проверку корректности ELF image:
- проверить, что байты `filesz` реально скопированы в mapped memory;
- проверить, что `zero-tail` (`memsz - filesz`) действительно занулен;
- проверить права сегментов после `Pager.Map`;
- проверить, что `entrypoint` лежит внутри executable `PT_LOAD` сегмента.

## Что реализовано

### 1. Добавлен слой проверки загруженного образа

Добавлен файл:
- `OS_0.1/src/Kernel/Elf/ElfLoadValidation.cs`

Реализовано:
- `ElfLoadValidation.Run(ref ElfParseResult, ref ElfLoadedImage)`;
- проверка page flags по всем страницам каждого `PT_LOAD` сегмента;
- побайтная проверка copy-path (`source ELF bytes == loaded bytes`);
- побайтная проверка zero-tail;
- проверка `entrypoint`:
  - входит в executable segment (`PF_X`);
  - mapped в pager;
  - не отмечен `NoExecute`.

### 2. Вынесена policy-конверсия флагов для переиспользования

Обновлен файл:
- `OS_0.1/src/Kernel/Elf/ElfLoader.cs`

Изменение:
- `ProgramFlagsToPageFlags(...)` сделан `internal`, чтобы и loader, и validation использовали один и тот же rule-set.

### 3. Интеграция в pipeline шага 10

Обновлен файл:
- `OS_0.1/src/Kernel/Elf/ElfValidation.cs`

После успешного `ElfLoader.TryLoad(...)` теперь вызывается:
- `ElfLoadValidation.Run(...)`.

## Проверка

Проверено:
1. `.\run_build.ps1 -NoRun` — успешно.
2. `.\run_build.ps1` — успешно.

Ключевые строки COM1:
- `[info] elf load validate start`
- `[info] elf load validate bytes file/zero: 192/192`
- `[info] elf load validate done`

Параллельно подтверждены строки шага 10 (`map/copy/zero/entry ready`), значит загрузка и её валидация проходят единым pipeline.

## Итог

Шаг 10.1 закрыт:
- образ после load не просто “есть”, а проверен на корректность содержимого, zero-tail, flags и entrypoint-условия.

Это готовая база для следующего шага:
- `ProcessImage + stack mapping + controlled jump`.
