# step247: Intel HD Audio — порт драйвера SerenityOS

## Контекст

step246 дал звук через AC'97 — его нет на машинах моложе ~2005. Всё современное —
Intel HD Audio: контроллер (CORB/RIRB, потоки, BDL) плюс кодек, у которого маршрут
от ЦАП до гнезда — граф виджетов, разный у каждого производителя. Своего не писали:
взят драйвер SerenityOS (`Kernel/Devices/Audio/IntelHDA`, Jelle Raaijmakers,
BSD-2-Clause, коммит `e5966bd`), ~2700 строк C++ → ~700 строк C#.

## Архитектура

`OS/src/Hal/Audio/Hda/`, файлы повторяют исходные; в шапке каждого — откуда и
что вырезано/изменено; `LICENSE-SerenityOS.txt`.

- **`HdaController`** — поиск по классу PCI 04/03, MMIO из BAR0 (64-битный BAR
  учтён), сброс (3.3.7, 4.3), CORB/RIRB, кодеки по STATESTS, `SendCommand` (7.3:
  12-битный глагол — 8 бит данных, 4-битный — 16), маршрут: поиск в ширину от
  каждого физически подключённого выходного пина (line out, динамик, наушники) до
  ближайшего ЦАП — как в оригинале.
- **`HdaCodec`** — root → audio function group → виджеты: возможности, PCM-размеры
  и частоты, усилители, пин (возможности, configuration default), список соединений.
- **`HdaOutputPath`** — питание D0, поток/канал на ЦАП, выходные усилители на 0 дБ,
  выбор входов по пути, вывод пина; формат 48 кГц (иначе 44.1), 16 бит, стерео.
- **`HdaOutputStream`** — 32 буфера по странице (~680 мс), LVI=31, без IOC.
- **`HdaRingBuffer`**, **`HdaFormat`**, **`HdaRegisters`** (MMIO через NoInlining —
  опрос не поднимается компилятором), **`HdaTiming`** (ожидания по HPET с потолком).

Отличия от оригинала:
- **без прерывания** (INTCTL=0): у нас MSI и нет маршрута INTx — поток ядра раз в
  5 мс читает LPIB и доливает/гасит буферы, как `Ac97.Feed` (step246); пропуск
  слышен как тишина, поток не останавливается;
- **исправление:** у SerenityOS `set_amplifier_gain_mute` сдвигает флаги выходного и
  входного усилителя оба в бит 15; входной — бит 14 (7.3.3.7);
- **добавлено:** EAPD/BTL на пине, если он умеет (7.3.3.16) — многие ноутбуки
  держат за ним усилитель динамиков;
- проверка версий: контроллер и кодек — любой 1.x (у оригинала строго 1.0);
- контроллер без 64-битной адресации тоже берётся (страницы тождественно
  отображены).

FETCH: `SystemInfo.Audio` (`AudioFacts`: вид карты, кодек, частота) — строка
`audio  hda 1af4:0012, 48000 Hz` или `none`.

Загрузка: HDA первым, иначе AC'97; строка `[audio] hda codec VVVV:DDDD path P>…>C
48000 Hz` или `[audio] none (hda: …; ac97: …)`. `run_build -AudioCard hda|ac97`,
по умолчанию hda (`ich9-intel-hda` + `hda-output`).

## Проверка

Рабочая копия, WHPX.

- Прогон 312 (`-AudioWav`, hda): `[audio] hda codec 1af4:0012 path 3>2 48000 Hz`;
  `tone 440 1000`, `tone 1000 500` и прочие — 8/8. WAV: 1000 мс на 440 Гц, 510 мс на
  1000 Гц (ДПФ), пик 11999 при амплитуде 12000 — путь на 0 дБ.
- Прогон 315 (все приложения пересобраны, hda): `FETCH` — `audio  hda 1af4:0012,
  48000 Hz`.
- Прогон 313 (`-AudioCard ac97`): HDA нет — `[audio] ac97 48000 Hz …`, 8/8; WAV:
  1010 мс на 440 Гц, 500 мс на 1000 Гц — как в step246.
- Батарея 314 (`s296.sh`, без звуковой карты): `[audio] none (hda: no device;
  ac97: no device)`; 65/66 — упал PROCTEST №15 (SYM-008), второй прогон PROCTEST
  80/0. SYM-008 выпал в 4 батареях из 5 последних (299, 308, 311, 314) — чаще, чем
  флейк; к разбору.

## Файлы

- `OS/src/Hal/Audio/Hda/` (новые): `HdaController.cs`, `HdaCodec.cs`,
  `HdaOutputPath.cs`, `HdaStream.cs`, `HdaRingBuffer.cs`, `HdaFormat.cs`,
  `HdaRegisters.cs`, `LICENSE-SerenityOS.txt`.
- `OS/src/Boot/PostEbsBoot.cs`, `OS/src/Kernel/Diagnostics/Probes.cs`.
- `std-no-runtime/SystemInfo.cs` (`AudioFacts`), `OS/src/Kernel/SystemFacts.cs`,
  `apps_native/FetchApp/Program.cs`.
- `run_build.ps1` (`-AudioCard`), `README.md`, `sbom.cdx.json`, `release.md`,
  `docs/nativeaot-nostd-kernel-limits.md` (§21).

## Дальше

Звук эмуляторов и DOOM; HDA на стенде.
