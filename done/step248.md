# step248: PC speaker, `Console.Beep`, `beep`

## Контекст

После карт (step246 AC'97, step247 HD Audio) — третий источник звука, который есть на
любом x86: PC speaker, канал 2 таймера 8254, выведенный на динамик битами порта
0x61. Обнаружить сам динамик нельзя — у него нет регистров; проверить можно только,
что канал 2 считает (его выход — бит 5 порта 0x61).

## Архитектура

- **`Hal/Audio/PcSpeaker.cs`**: `Present` — проба один раз: ворота канала 2 открыты,
  данные динамика закрыты (тихо), делитель 64, бит 5 порта 0x61 должен смениться за
  ≤100 000 чтений. `Tone(hz, ms)` — режим 3 (меандр), делитель 1193182/hz, оба бита
  0x61; срок конца — в тиках HPET. Поток ядра (запускается первым звуком) раз в 2 мс
  гасит тон по сроку: программа, умершая посреди звука, не оставит машину пищать.
  Проверка срока и выключение — под подавлением вытеснения, чтобы новый звук не
  срезался концом прошлого.
- **Служба** `Speaker(int hz, int ms)` (хвост таблицы, ABI не меняется); SDK —
  `Audio.Speaker(hz, ms)`.
- **`Console.Beep()` / `Console.Beep(int, int)`** — API BCL: пределы и исключения
  как в BCL (37–32767 Гц, длительность > 0), ждёт длительность, как Windows; без
  динамика — BEL в терминал, как BCL на Unix.
- **`beep [HZ [MS]]`** в `UTILS` (и псевдоним оболочки): динамик и синус на карте
  одновременно, 800 Гц 200 мс по умолчанию — звучит то, что есть; без обоих —
  ошибка. `tone` и `beep` делят разбор аргументов и проигрывание синуса.
- Загрузка: `[audio] pc speaker: PIT channel 2 counts` (или `does not count`);
  `SystemInfo.Audio.Speaker`, FETCH: `audio  hda 1af4:0012, 48000 Hz, pc speaker`.
- `run_build`: с `-Audio`/`-AudioWav` динамик QEMU подключён к тому же выводу
  (`-machine q35,…,pcspk-audiodev=snd0`).

## Проверка

Рабочая копия, WHPX, все приложения собраны `./build.ps1`.

- Прогон 316 (`-AudioWav`, hda): `[audio] pc speaker: PIT channel 2 counts`;
  `beep 1500 400`, `tone 440 300`, `beep --help`, `expect 2 beep 0`, `FETCH` — 8/8.
  WAV: 470 мс на 1500 Гц (динамик сразу, карта с задержкой очереди), затем 300 мс на
  440 Гц. В участке `beep` нечётные гармоники 4500 Гц (4059) и 7500 Гц (2076) при
  почти нулевой чётной 3000 Гц (49) — меандр динамика поверх синуса карты. Пик
  32768: в смеси QEMU динамик идёт на полной амплитуде и вместе с картой
  упирается в потолок — свойство эмуляции.

## Файлы

- `OS/src/Hal/Audio/PcSpeaker.cs` (новый).
- `OS/src/Kernel/Process/AppServiceTable.cs`, `AppServiceBuilder.cs`;
  `apps_native/sdk/AppServiceTable.cs`, `apps_native/sdk/Audio.cs`,
  `apps_native/sdk/SystemConsole.cs` (`Console.Beep`).
- `apps_native/UtilsApp/Tone.cs`, `Program.cs`; `apps_native/Shell/Executor.cs`.
- `OS/src/Boot/PostEbsBoot.cs`, `std-no-runtime/SystemInfo.cs`,
  `OS/src/Kernel/SystemFacts.cs`, `apps_native/FetchApp/Program.cs`.
- `run_build.ps1`, `docs/nativeaot-nostd-kernel-limits.md` (§21), `release.md`.

## Дальше

Звук Fami и DOOM (step249).
