# step246: звук — AC'97, служба AudioWrite, `tone`

## Контекст

Звука не было вовсе. Пользователь решил начать с AC'97: UEFI звук после
ExitBootServices не даёт (да и до — почти никогда), а у MOOS (Unlicense) есть
драйвер на ~90 строк. AC'97 — QEMU и старое железо; на современных машинах Intel
HD Audio, это следующий шаг.

## Архитектура

- **Порт I/O двойными словами.** `PortIo.In32/Out32` — та же схема, что у байтов и
  слов: `[RuntimeExport]`-заглушка `PortIo_Inl/Outl` и шеллкод из BootAsm
  (`in eax, dx` / `out dx, eax`). Нужен регистру базы списка буферов.
- **`Hal/Audio/Ac97.cs`** — порт MOOS `Kernel/Driver/AC97.cs` (коммит `6dc9964`):
  поиск 8086:2415, BAR0/BAR1 в портах, cold reset, сброс микшера, громкости,
  список дескрипторов, PCM-out. Отличия (`LICENSE-MOOS.txt`, шапка файла):
  - без прерывания: у MOOS линия INTx, у нас все устройства на MSI, а маршрута
    INTx (ACPI `_PRT`) нет. Поток ядра раз в 5 мс читает текущий индекс;
  - кольцо непрерывное: последний валидный индекс всегда сразу за текущим, DMA не
    останавливается; буфер заливается за ≤4 буфера (~85 мс) до проигрыша и
    гасится после — пропуск слышен как тишина, а не повтор кольца;
  - ожидание готовности кодека и сброса бокса (HPET, с потолком спина), 48 кГц —
    частота, которая есть у любого кодека AC'97 без VRA;
  - буферы — `DmaMemory`, с проверкой «ниже 4 ГиБ» (адреса в дескрипторах 32-битные).
- **`Kernel/Audio/AudioOut.cs`** — очередь PCM 128 КиБ (~680 мс) между службой и
  драйвером; не микшер: один поток. Счётчики `Written`, `Played`, `Starved`.
- **Службы** (хвост таблицы, ABI не меняется): `AudioWrite(byte*, int)` — берёт, что
  влезло, целыми кадрами; `AudioControl(op)` — частота, занято, свободно, сброс.
- **SDK** — `SharpOS.AppSdk.Audio`: `Available`, `SampleRate`, `QueuedFrames`,
  `FreeFrames`, `Write(ReadOnlySpan<short>)`, `Clear()`.
- **`tone [HZ [MS]]`** в `UTILS` (и псевдоним оболочки): синус, затухание 5 мс по
  краям, ждёт, пока очередь доиграет.
- **`run_build.ps1`**: `-Audio` — AC'97 в динамики хоста (DirectSound);
  `-AudioWav <файл>` — запись в WAV для проверки без слушателя.
- Загрузка: `Probes.AudioInit`, после USB в `PostEbsBoot`; одна строка
  `[audio] ac97 48000 Hz …` или `[audio] none (причина)`.

## Проверка

Рабочая копия, WHPX, все приложения собраны `./build.ps1`.

- Прогон 310 (`-AudioWav`): `[audio] ac97 48000 Hz stereo s16, dac=48000`. `tone
  440 1000`, `tone 1000 500`, `tone --help`, `expect 2 tone abc` — 8/8. WAV
  разобран на хосте (свой скрипт: RMS-окна 10 мс, пересечения нуля, ДПФ):
  звук 1010 мс на 440 Гц, затем 500 мс на 1000 Гц, пик ~8940. QEMU пишет WAV на
  44100 Гц — его собственный пересчёт; частоты совпали, значит 48 кГц у нас верно.
- Пользователь, `-Audio` (DirectSound, динамики хоста), `tone` на разных частотах,
  анализатор спектра на телефоне (Spectroid): расхождение не больше ~5 Гц на высоких
  частотах и ~1 Гц на низких — в пределах разрешения анализатора.
- Батарея 311 (`s296.sh`, без звуковой карты): 65/66, `[audio] none (no device)`.
  Упал PROCTEST №15 — SYM-008, флейк по времени (тот же, что в 299 и 308); второй
  прогон PROCTEST — 80/0.

## Файлы

- `OS/src/Hal/PortIo.cs`, `PortIoStub.cs`, `PortIoPatcher.cs`, `PortIoPatcher.BootAsm.cs`.
- `OS/src/Hal/Audio/Ac97.cs` (новый), `OS/src/Hal/LICENSE-MOOS.txt`, `OS/src/Hal/Pci.cs`
  (`EnableIoAndBusMaster`, `TryFindDevice`).
- `OS/src/Kernel/Audio/AudioOut.cs` (новый).
- `OS/src/Kernel/Process/AppServiceTable.cs`, `AppServiceBuilder.cs`;
  `apps_native/sdk/AppServiceTable.cs`, `apps_native/sdk/Audio.cs` (новый),
  `apps_native/sdk/FreestandingPe.props`.
- `OS/src/Kernel/Diagnostics/Probes.cs`, `OS/src/Boot/PostEbsBoot.cs`.
- `apps_native/UtilsApp/Tone.cs` (новый), `Program.cs`, `UtilsApp.csproj`;
  `apps_native/Shell/Executor.cs`.
- `run_build.ps1`, `README.md`, `sbom.cdx.json`, `release.md`,
  `docs/nativeaot-nostd-kernel-limits.md` (§21).

## Дальше

Звук DOOM и эмуляторов (пересчёт частоты в 48 кГц), затем микшер и HD Audio.
