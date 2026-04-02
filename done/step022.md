# step022

Дата: 2026-04-02  
Статус: завершён

## Цель

1. Зафиксировать, что текущее решение для string allocation во freestanding app — **временное**.  
2. Продолжить runtime-прогоны строковых экспериментов по одному тесту.  
3. Свести актуальную картину по surface/runtime-статусу строк.

## Что сделано

### 1. Зафиксирован временный bridge для allocator path строки

Чтобы `new string(char, int)` работал в текущем freestanding-профиле:

- в `apps/sdk/MinimalRuntime.cs` строковая аллокация идёт через `RuntimeImport("RhNewString")`;
- в `build_app_freestanding_wsl.ps1` на этапе линковки генерируется `runtime_stubs.c` с временной реализацией `RhNewString` (bump allocator).

Это **временное решение для экспериментов**, не финальная архитектура рантайма.

### 2. Усилен `EXP_TEST_16`

В `apps/sdk/StringExperimentSuite.cs` тест `EXP_TEST_16` теперь проверяет:

- `Length == 4`;
- все символы равны `'A'`;
- при `fixed(char* p = s)` есть null-terminator (`p[4] == '\0'`).

Возвращаемый код успеха сохранён (`test_result=4`) для совместимости текущего парсинга логов.

### 3. Продолжены runtime-прогоны по одному тесту

В этом шаге дополнительно прогнаны:

- `EXP_TEST_11` (`test_result=423`);
- `EXP_TEST_12` (`test_result=294`);
- `EXP_TEST_13` (`test_result=97`);
- `EXP_TEST_90` (`test_result=1`).

Все прогоны завершились с `process exit code = 21` и без регрессий batch-run.

## Текущий статус строк (на конец шага)

### Surface/runtime matrix (актуально)

| Test | Проверка | Ожидаемо | Факт | Статус |
|---|---|---:|---:|---|
| `EXP_TEST_01` | `Length` | 3 | 3 | pass |
| `EXP_TEST_02` | `Indexer_FirstChar` | 97 | 97 | pass |
| `EXP_TEST_03` | `Indexer_LoopSum` | 294 | 294 | pass |
| `EXP_TEST_09` | `AsciiEncode_Indexer` | 198 | 198 | pass |
| `EXP_TEST_10` | `Utf16LeEncode_Indexer` | 131 | 131 | pass |
| `EXP_TEST_11` | `Utf8Encode_Bmp_NoPin` | 423 | 423 | pass |
| `EXP_TEST_12` | `fixed(char* p = s)` | 294 | 294 | pass |
| `EXP_TEST_13` | `GetPinnableReference` | 97 | 97 | pass |
| `EXP_TEST_16` | `new string(char,int)` + fill + terminator | 4 | 4 | pass |
| `EXP_TEST_18` | `Concat` (`s + "b"`) | 2 | 2 | pass |
| `EXP_TEST_90` | layout/pinning invariants | 1 | 1 | pass |

Итог: текущий baseline строк в freestanding app рабочий для ключевых сценариев чтения/энкодинга/pinning/базовой аллокации/concat.

## Файлы шага

- Изменены:
  - `apps/sdk/StringExperimentSuite.cs`
  - `apps/sdk/MinimalRuntime.cs`
  - `build_app_freestanding_wsl.ps1`
  - `done/step022.md`
- Добавлены:
  - `string experiments/results/test16-runtime-pass.md`
  - `string experiments/results/test18-runtime-pass.md`
  - `string experiments/results/test11-runtime-pass.md`
  - `string experiments/results/test12-runtime-pass.md`
  - `string experiments/results/test13-runtime-pass.md`

## Зафиксированный техдолг

1. Убрать временный C-stub `RhNewString` из build-пайплайна.  
2. Перенести allocator path строки в устойчивый managed/freestanding runtime-контракт.  
3. После замены allocator path повторить критичные runtime-тесты (`12/13/16/18/90`).

## Проверка

Команды (по одному тесту):

```powershell
./build_app_freestanding_wsl.ps1 -DefineConstants EXP_TEST_XX
& "C:\Program Files\PowerShell\7\pwsh.exe" -Command "./run_build.ps1"
```

Подтверждённые свежие прогоны в этом шаге:

- `EXP_TEST_11`: `test_id=11`, `test_result=423`
- `EXP_TEST_12`: `test_id=12`, `test_result=294`
- `EXP_TEST_13`: `test_id=13`, `test_result=97`
- `EXP_TEST_90`: `test_id=90`, `test_result=1`
