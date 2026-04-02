# step021

Дата: 2026-04-02
Статус: завершён (подготовительный инкремент)

## Цель

Подготовить стабильную базу под минимальный app-проводник на SDK ABI v2:
- убрать legacy сборочный путь;
- оставить рабочий file-browser каркас в `HELLOCS`;
- зафиксировать ограничения freestanding AOT перед экспериментами со строками.

## Что реализовано

### 1. Очистка build pipeline

- Удалён legacy-скрипт `build_app_wsl.ps1`.
- Актуальным сборочным путём оставлен только `build_app_freestanding_wsl.ps1`.

### 2. Рабочий каркас минимального проводника в app

Обновлён `apps/HelloSharpFs/Program.cs`:
- приложение печатает ABI;
- проверяет `FileExists` для `\EFI\BOOT\HELLO.ELF`;
- перечисляет каталог `\EFI\BOOT` через цикл `TryReadDirEntry(index)` до `EndOfDirectory`;
- для freestanding-совместимости использует `byte*` null-terminated литералы.

### 3. SDK-фиксация string-пути (подготовка к экспериментам)

Обновлён `apps/sdk/AppHost.cs`:
- string-overload `WriteString(string)` упрощён (один временный буфер вместо чанк-цикла);
- `TryEncodeAscii(...)` оставлен как базовый helper.

Зафиксировано ограничение:
- при активном использовании string-конвертации с pinning (`fixed (char* ...)`) NativeAOT может падать в freestanding профиле (`System.RuntimeType not found`);
- текущий стабильный runtime-путь для app — `byte*` строки.

## Файлы шага

- Удалён:
  - `build_app_wsl.ps1`
- Изменены:
  - `apps/HelloSharpFs/Program.cs`
  - `apps/sdk/AppHost.cs`
  - `done/step021.md`

## Проверка

- `./build_app_freestanding_wsl.ps1 -NoCopy` — успешно, артефакт:
  - `apps/HelloSharpFs/bin/Release/out-linux-x64/HELLOCS.ELF`
- `./run_build.ps1 -NoRun` — успешно (ядро + подготовка EFI/ELF).

## Итог и остаток

Стабильная база под проводник готова: file-listing path в app работает, freestanding сборка зелёная, legacy build path удалён.

Осталось на следующий инкремент:
- интерактивная клавиатурная навигация;
- запуск выбранного файла через `RunApp(...)`;
- отдельный цикл экспериментов по строкам в freestanding runtime.
