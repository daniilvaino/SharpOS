# step250: микшер в ядре; музыка DOOM

## Контекст

У ядра была одна очередь звука — две программы перемешивали бы блоки. Музыку DOOM
сначала собирались сводить с эффектами внутри DOOM, но микшер всё равно нужен (beep
поверх игры, любые две программы), и сведение в DOOM пришлось бы выбросить. Поэтому
сначала микшер, музыка — вторым потоком.

## Архитектура

- **`Kernel/Audio/AudioOut` — микшер:** до 16 потоков, у каждого очередь 128 КиБ и
  процесс-владелец. Драйвер (HDA, AC'97) просит буфер — потоки суммируются в int и
  ограничиваются по амплитуде; поток без данных молчит, иссякший посреди буфера
  отдаёт что было. Потоки процесса закрываются в `EndProcess` (рядом с его файлами):
  умершая программа не звучит.
- **Совместимость:** `AudioWrite`/`AudioControl` пишут в поток процесса по умолчанию
  (открывается первой записью) — `tone`, `beep`, Fami, эффекты DOOM без правок.
- **Служба `AudioStream(op, stream, pcm, length)`** (хвост таблицы, ABI не меняется):
  0 открыть, 1 писать, 2 в очереди, 3 свободно, 4 сбросить, 5 закрыть. SDK —
  `SharpOS.AppSdk.AudioStream` (`Open`, `Write`, `QueuedFrames`, `FreeFrames`,
  `Clear`, `Dispose`).
- **Музыка DOOM — `SharpMusic`:** `SilkMusic` ManagedDoom (декодеры MUS и MIDI без
  изменений) поверх `AudioStream` вместо потокового источника OpenAL; MeltySynth
  синтезирует сразу на частоте карты (тик MUS — rate/140 сэмплов: 342 при 48 кГц,
  на 0.25 % быстрее); `Pump` раз в тик из цикла игры доливает поток до ~100 мс.
  Эффекты (`SharpSound`) — поток по умолчанию, музыка — свой.
- **`vendor/MeltySynth`** (MIT, 2.4.1 — версия, которую берёт ManagedDoom): собран в
  DOOM.EXE. Две строки `SharpOS cut:` — `.AsSpan()` у массива в `MemoryMarshal.Cast`:
  в C# 14 (first-class spans) для массива выбирается перегрузка `ReadOnlySpan`, а код
  пишет через результат; вернуть проект на C# 13 нельзя — std требует `field`.
- **`vendor/TimGM6mb`** (GPL-2.0, SoundFont по умолчанию у ManagedDoom; извлечён из
  его релиза v2.1a): `\games\TimGM6mb.sf2` — сборка кладёт рядом с DOOM.
- **Лицензии в образе** (`\sharpos\licence\`): добавлены MeltySynth и TimGM6mb, а
  также пропущенные в step245/247 AccuracyCoin, Famidash, SerenityOS-HDA.

- **HD Audio: все контроллеры** (стенд): брался первый контроллер класса 04/03, а у
  ноутбука на Ryzen их два — HDMI/DP видеоядра первым по PCI (у его кодека только
  цифровые пины, путь не находится) и аналоговый. Теперь `Start` перебирает все и
  берёт первый с путём; на каждый — строка `[audio] hda <адрес> <vid:did> codec
  <vid:did>(<узел>=<вид>/<подключение>,…) -> path …` или причина.

## Проверка

Рабочая копия, WHPX, все приложения собраны `./build.ps1`.

- Прогон 322 (`-AudioWav`, hda): `tone 440 1500 | tone 1000 600` — два процесса
  одновременно: через 0.2 и 0.4 с в WAV оба тона с полной амплитудой (6001 и 5980 в
  мере ДПФ = 12000/2), через 1 с 1000 Гц нет (поток закрыт с процессом), 440 Гц
  звучит. DOOM: `doom: music /games/TimGM6mb.sf2`; звук демо непрерывный — 2 тихих
  окна по 100 мс из 140 (в прогоне 317 без музыки — 14 из 90); демо идёт штатно.
- Пользователь, `-Audio` (динамики хоста): DOOM — музыка и эффекты, «всё работает
  отлично».
- **Стенд** (ноутбук Ryzen 3 7320U, первая загрузка звука на железе): было
  `[audio] none (hda: no output path; ac97: no device)`, звучала только пищалка —
  её сигнал заведён в кодек (вход PC Beep), отдельного динамика нет. После правки:
  `3:0.1 1002:1640 codec 1002:aa01(3=5/0,5=5/1,…) -> no output path` (HDMI, вид 5 —
  цифровой), `3:0.6 1022:15e3 codec 10ec:0236(20=1/2,27=1/1,30=1/1,33=2/0) -> path
  20>2` — Realtek ALC236, встроенный динамик (пин 20, фиксированный) → ЦАП 2, 48 кГц.
  Пользователь: `beep` — два звука сразу (динамик и карта).
- Батарея 323 (`s296.sh`, без карты): 65/66 — PROCTEST №15 (SYM-008), как в шести
  батареях подряд.

## Файлы

- `OS/src/Kernel/Audio/AudioOut.cs` (микшер), `OS/src/Kernel/Process/AppServiceBuilder.cs`,
  `AppServiceBuilder.Processes.cs`, `AppServiceTable.cs`; `apps_native/sdk/AppServiceTable.cs`,
  `apps_native/sdk/Audio.cs` (`AudioStream`).
- `apps_native/GPL_AHEAD_WARNING_DOOM_managed/SharpMusic.cs` (новый), `Program.cs`,
  `DoomApp.csproj`.
- `vendor/MeltySynth/` (новый: `src/`, `LICENSE.txt`, `PROVENANCE.md`),
  `vendor/TimGM6mb/` (новый: `TimGM6mb.sf2`, `LICENSE`, `PROVENANCE.md`).
- `run_build.ps1` (SoundFont, лицензии), `README.md`, `sbom.cdx.json`, `release.md`,
  `donext.md`, `docs/nativeaot-nostd-kernel-limits.md` (§21).

## Дальше

Звук закрыт для релиза; HD Audio на стенде — §12.
