# Замеры hosted-яруса: прогресс

Как меняются бенчи от правки к правке. Таблицу между маркерами строит
`tools/perf_report.ps1 -Label "<имя>"` после прогона `\SHARPOS\Bench.dll` из
лаунчера; данные — `docs/perf-history.csv`. Руками таблицу не править:
перезапишется.

- Значение — медиана тёплых прогонов: если Bench запускали несколько раз за
  загрузку, первый (холодный, платит за JIT самого бенча) отбрасывается.
- QEMU TCG, `-cpu qemu64,+nx`, Release-форк, диск по USB, если в строке
  `config` не сказано иное.
- `host` — тот же `Bench.dll` под `dotnet` на ПК (`tools/bench-host-reference.log`).
  Программная эмуляция сама по себе даёт примерно ×20 на обычном коде, поэтому
  `x host` мешает цену эмуляции с ценой SharpOS.
- `linux qemu` — тот же `Bench.dll` на Debian в том же QEMU (TCG, тот же `-cpu`,
  один процессор, 2 ГиБ): `run_linux_ref.ps1`, результат —
  `tools/bench-qemu-linux-reference.log`. Эмуляция есть в обоих, так что
  `x linux` (последний столбец к нему) — то, что стоит сам SharpOS.
- Строки `AOT …` — `apps_native/BenchAot` (`BENCHAOT.EXE` из лаунчера): та же
  работа на нашем NativeAOT-ярусе приложений. `host` и `linux qemu` для них —
  те же строки `Bench.dll`, то есть это сравнение с обычным .NET на той же работе.
- Подозреваемые и их статус — рабочий список `work/perf-suspects.md` (не в git);
  что закрыто — в `done/step168.md` и далее.

<!-- perf-table:begin -->
| | host | linux qemu | step168 часы: до | step168 часы: HPET | step169 все бенчи | step169 счётчики | step169 перерисовки ≤60/с | опыт: -cpu max | step169 кеши+аллокаторы | step169 BenchAot, EH-трасса выкл | step169 вывод AOT: кадр не чаще 60/с | step169 детектор: вывод до задач | step169 вывод AOT = hosted (s_feeding) | step169 обход GC приложений через переходники | опыт: TieredCompilation=false | step170 профайлер (исходная) | step170 замеры на TSC, memcpy по 8 байт | step170 трасса EH форка: макросы | step170 ожидание USB: HPET раз в 256 оборотов | x host | x linux |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| date | | | 2026-09-11 | 2026-09-11 | 2026-09-11 | 2026-09-11 | 2026-09-13 | 2026-09-13 | 2026-09-13 | 2026-09-13 | 2026-09-13 | 2026-09-13 | 2026-09-13 | 2026-09-13 | 2026-09-13 | 2026-09-13 | 2026-09-13 | 2026-09-13 | 2026-09-13 | | |
| config | | |  |  |  |  |  | cpu=max | cpu=max |  |  |  |  |  | tiering=off |  |  |  |  | | |
| alloc.short, ns/op | 17 ns | 150 ns |  |  | 417 ns | 395 ns | 690 ns | 518 ns | 162 ns | 164 ns | 154 ns | 156 ns | 162 ns | 158 ns | 158 ns | 174 ns | 94 ns | 102 ns | 88 ns | x5.2 | x0.6 |
| alloc.short, gen0 collections |  | 2 |  |  | 78 | 80 | 87 | 36 | 2 | 2 | 2 | 2 | 3 | 2 | 2 | 3 | 3 | 3 | 3 |  |  |
| alloc.retained, ns/op | 41 ns | 472 ns |  |  | 468 ns | 424 ns | 495 ns | 898 ns | 494 ns | 490 ns | 520 ns | 492 ns | 649 ns | 524 ns | 669 ns | 618 ns | 504 ns | 410 ns | 463 ns | x11.3 | x1 |
| strings, ns/op | 95 ns | 1.5 us |  |  | 4.1 us | 4.0 us | 3.9 us | 4.4 us | 728 ns | 730 ns | 635 ns | 804 ns | 798 ns | 624 ns | 755 ns | 629 ns | 474 ns | 580 ns | 688 ns | x7.2 | x0.5 |
| collections, ns/op | 43 ns | 384 ns |  |  | 490 ns | 603 ns | 98 ns | 525 ns | 526 ns | 281 ns | 442 ns | 667 ns | 432 ns | 431 ns | 433 ns | 462 ns | 280 ns | 357 ns | 393 ns | x9.1 | x1 |
| exceptions, ns/op | 1.7 us | 40.0 us |  |  | 113.4 us | 116.2 us | 124.0 us | 118.4 us | 117.3 us | 127.4 us | 119.9 us | 117.0 us | 120.4 us | 118.9 us | 120.5 us | 137.3 us | 90.5 us | 59.1 us | 60.9 us | x36.1 | x1.5 |
| jit, ns/op | 26.9 us | 814.8 us |  |  | 579.2 us | 610.1 us | 626.8 us | 583.0 us | 590.4 us | 557.6 us | 644.2 us | 569.9 us | 611.3 us | 627.3 us | 627.6 us | 665.0 us | 629.1 us | 656.2 us | 696.3 us | x25.9 | x0.9 |
| tasks, ns/op | 5.2 us | 86.3 us |  |  | 49.5 us | 20.1 us | 23.1 us | 121.2 us | 95.2 us | 48.2 us | 18.9 us | 27.8 us | 22.4 us | 30.3 us | 12.6 us | 18.2 us | 33.4 us | 21.3 us | 72.5 us | x14.1 | x0.8 |
| output, ns/line | 12.2 us | 225.9 us |  |  | 1.00 ms | 1.01 ms | 398.3 us | 384.1 us | 413.1 us | 429.6 us | 405.3 us | 395.8 us | 413.0 us | 390.7 us | 441.9 us | 1.21 ms | 429.8 us | 429.0 us | 430.2 us | x35.3 | x1.9 |
| clock read, ns (kernel) |  |  | 9.8 us | 961 ns | 880 ns | 968 ns | 1.0 us | 940 ns | 1.1 us | 1.2 us | 1.0 us | 992 ns | 980 ns | 980 ns | 1.1 us | 1.4 us | 808 ns | 788 ns | 880 ns |  |  |
| clock backsteps (UtcNow) |  |  | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |  |  |
| clock test by Stopwatch, ms | 8 | 98 | -442 | 62 | 56 | 60 | 61 | 60 | 70 | 78 | 66 | 63 | 62 | 62 | 73 | 96 | 42 | 42 | 48 |  |  |
| whole Bench run by HPET, ms |  |  | 574 | 66 | 852 | 844 | 738 | 856 | 529 | 480 | 475 | 504 | 482 | 472 | 502 | 1992 | 1274 | 964 | 1030 |  |  |
| screen repaints, ms/run |  |  |  |  |  | 132 | 10 | 10 | 12 | 10 | 12 | 12 | 11 | 10 | 11 | 14 | 12 | 12 | 12 |  |  |
| disk log, ms/run |  |  |  |  | 26 | 23 | 17 | 17 | 16 | 22 | 16 | 17 | 18 | 17 | 17 | 54 | 18 | 18 | 20 |  |  |
| writes served by kernel, ms/run |  |  |  |  |  |  |  |  |  |  |  |  | 95 | 91 | 101 | 278 | 101 | 100 | 100 |  |  |
| census, ms |  |  | 16261 | 16536 | 16826 | 16639 | 16587 | 16244 | 16396 | 16403 | 16347 | 16224 | 16357 | 16295 | 19022 | 16929 | 17096 | 16495 | 16499 |  |  |
| AOT alloc.short, ns/op | 17 ns | 150 ns |  |  |  |  |  |  |  | 123 ns | 108 ns | 113 ns | 138 ns | 140 ns | 127 ns |  |  | 123 ns | 113 ns | x6.6 | x0.8 |
| AOT alloc.short, collections |  |  |  |  |  |  |  |  |  | 0 | 0 | 0 | 0 | 0 | 0 |  |  | 0 | 0 |  |  |
| AOT alloc.retained, ns/op | 41 ns | 472 ns |  |  |  |  |  |  |  | 262 ns | 218 ns | 152 ns | 209 ns | 204 ns | 194 ns |  |  | 186 ns | 169 ns | x4.1 | x0.4 |
| AOT strings, ns/op | 95 ns | 1.5 us |  |  |  |  |  |  |  | 1.0 us | 919 ns | 874 ns | 1.0 us | 1.0 us | 966 ns |  |  | 892 ns | 840 ns | x8.8 | x0.6 |
| AOT collections, ns/op | 43 ns | 384 ns |  |  |  |  |  |  |  | 186 ns | 156 ns | 177 ns | 193 ns | 196 ns | 178 ns |  |  | 191 ns | 189 ns | x4.4 | x0.5 |
| AOT exceptions, ns/op | 1.7 us | 40.0 us |  |  |  |  |  |  |  | 8.9 us | 4.9 us | 4.8 us | 5.7 us | 5.4 us | 5.1 us |  |  | 5.3 us | 5.7 us | x3.4 | x0.1 |
| AOT tasks, ns/op | 5.2 us | 86.3 us |  |  |  |  |  |  |  | 528.0 us | 573.9 us | 415.3 us | 468.4 us | 382.8 us | 553.6 us |  |  | 432.9 us | 372.7 us | x72.3 | x4.3 |
| AOT output, ns/line | 12.2 us | 225.9 us |  |  |  |  |  |  |  | 1.42 ms | 642.6 us | 616.2 us | 425.6 us | 430.5 us | 454.7 us |  |  | 467.6 us | 474.1 us | x38.9 | x2.1 |
| AOT output before tasks, ns/line | 12.2 us | 225.9 us |  |  |  |  |  |  |  |  |  | 639.3 us | 461.6 us | 436.0 us | 465.1 us |  |  | 482.1 us | 450.0 us | x36.9 | x2 |
| AOT screen repaints, ms/run |  |  |  |  |  |  |  |  |  |  |  | 33 | 27 | 26 | 27 |  |  | 31 | 28 |  |  |
| AOT writes served by kernel, ms/run |  |  |  |  |  |  |  |  |  |  |  |  | 190 | 181 | 194 |  |  | 199 | 194 |  |  |
<!-- perf-table:end -->

## PowerShell интерактивно (step171)

PowerShell 7.6.5, одинаковый сценарий руками на SharpOS и стендом на Debian в
том же QEMU (`run_linux_ref.ps1 -PowerShell`, `tools/pwsh-qemu-linux-reference.log`):
приглашение, `ls`, `echo $PSV` + Tab, Enter. SharpOS — счётчики ядра
`run.PowerShellBootstrap.first_input_ms` и `key.*` (от выдачи клавиши до
следующего ожидания ввода), сверены с видеозаписью по кадрам: расхождение
1–2 кадра.

| | SharpOS | Debian в QEMU, тёплый / первый |
|---|---:|---:|
| до приглашения | 6.1–6.5 с | 5.8 / 8.1 с |
| `ls` + Enter → приглашение | 0.93–0.96 с | 0.76 / 0.93 с |
| Tab: `$PSV` → `$PSVersionTable` | 0.57–0.62 с | 0.91 / 1.04 с |
| Enter на `echo $PSVersionTable` → таблица | 0.40 с | не сравнимо: стенд досчитал приглашения раньше Enter |

У SharpOS рантайм к старту pwsh уже поднят и прогрет переписью, на Debian каждый
запуск — новый процесс; зато SharpOS грузит профиль (~0.8 с), Debian нет.
Debian без ответов на запрос позиции курсора (`ESC[6n`) ждал бы 16 с — стенд
отвечает, как терминал.

## На железе (step172)

Тот же `Bench.dll` и PowerShell на ноутбуке (Ryzen 3 7320U) и ПК (Ryzen 9
7900X3D) — SharpOS против Windows на той же машине, плюс SharpOS против
Debian в QEMU. Эталоны — `BenchHost` (три прогона в одном процессе, настройки
GC как у SharpOS), чтобы сравнивать тёплое с тёплым. Таблица и разбор —
`done/step172.md` §8–9; сырьё — `work/bench-2026-09-14/` (не в git).
