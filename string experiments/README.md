# String Experiments

Эксперименты для freestanding `HelloSharpFs`:
- тестируется поддержка `string` в NativeAOT no-stdlib профиле;
- каждый прогон собирается отдельным define-символом (`EXP_TEST_XX`);
- результат сохраняется в markdown и в логах.

## Запуск матрицы

```powershell
./string experiments/run_matrix.ps1
```

Скрипт запускает `build_app_freestanding_wsl.ps1 -NoCopy` для тестов:
- `01`, `02`, `03`, `09`, `10`, `11`, `12`, `13`, `16`, `18`.

Выход:
- `string experiments/results/latest.md`
- `string experiments/results/logs/testXX.log`

## Запуск runtime-матрицы (проверка значений)

```powershell
./string experiments/run_matrix_values.ps1
```

Скрипт:
- собирает `HELLOCS.ELF` для каждого `EXP_TEST_XX`;
- запускает `run_build.ps1` и парсит секцию `HELLOCS.ELF` из COM1-лога;
- проверяет `test_id`, `test_result`, `process exit code`.

Выход:
- `string experiments/results/runtime-latest.md`
- `string experiments/results/runtime-logs/testXX.build.log`
- `string experiments/results/runtime-logs/testXX.run.log`
