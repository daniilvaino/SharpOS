# String Runtime Analysis

Дата: 2026-04-02

Источник: `runtime-latest.md`

## Итог прогона

- runtime-матрица: `pass=0`, `fail=10`;
- для тестов `01,03,09,10,11,12,13`:
  - `test_id` корректный;
  - `test_result=0` при ожидаемом ненулевом значении;
  - `process exit code = 21` (app завершает flow штатно).
- `test02` уходит в timeout на этапе `HELLOCS` после `string exp start / abi=2`.
- `test16` и `test18` падают на этапе NativeAOT (`Invalid IL or CLR metadata`).

## Ключевая диагностика ILC

По `ilc --resilient --verbose --parallelism 1` для `EXP_TEST_16` последний метод перед падением:

- `System.Runtime.RuntimeImports.RhNewString(...)`

Падение:

- `Invalid IL or CLR metadata`

То есть проблема локализована в пути аллокации строки через `FastAllocateString -> RhNewString`.

## Практический вывод

- compile-surface для части string API есть, но runtime-correctness пока отсутствует;
- путь `new string(char,int)` сейчас не доведён до рабочего контракта в этом minimal runtime;
- следующий эксперимент нужен именно вокруг совместимого string allocation path, а не вокруг `StringExperimentSuite`.
