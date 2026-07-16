# Step 143 — ★ СЕВЕРНАЯ ЗВЕЗДА ДОСТИГНУТА: DOOM играбелен на SharpOS

## Результат

**Managed DOOM полноценно играется на bare metal против нашей std.** Из
лаунчера: DOOM.EXE → тайтл → attract-демо в реальном темпе → Enter → New
Game → E1M1 под клавиатурой — движение, повороты, стрельба, двери, HUD —
на весь экран 1280×800 (2× integer scale от 640×400), 35 Hz по HPET, без
замедлений под QEMU TCG. Выход через меню → exit 42 → лаунчер.
Скриншот-подтверждение: игрок в E1M1, живой HUD. Цель donext.md
(«Managed-порт DOOM запускается на bare metal SharpOS против нашей
собственной std») — закрыта в играбельной форме; за кадром остались
звук/музыка (Null-заглушки вендора) и мышь.

Один step = P2b (видео) + P3 (ввод/тайминг). Батарея зелёная,
регрессий нет.

## Видео: GOP-handoff + транспонирующий blit

- **AppServiceTable +5 data-полей** (Base/Width/Height/Stride/PixelFormat)
  — сырые данные, не сервис (паттерн InterfaceDispatchBridge/RhpThrowEx):
  GOP identity-мапится в общий pager ещё на буте (Hal.Framebuffer), аппа
  пишет пиксели напрямую, ноль вызовов на рендер-пути. Append-only, без
  версионного гейта (новые аппы проверяют Base != 0; headless → NullVideo).
- **GopVideo (DoomApp)** — CPU-эквивалент GL-транспонирования SilkVideo:
  Renderer заполняет column-major 32-bit буфер (`i = x*H + y`, R в младшем
  байте — DOOM рисует колоннами с 1993), блит ходит по строкам назначения
  с шагом `height` по источнику, для BGRX свапая R↔B. **Integer scale**:
  `min(fbW/W, fbH/H)` с центрированием — 640×400 в 1280×800 = ровно 2×
  full-screen (горизонтальная репликация пикселя + вертикальная строки).

## Ввод: raw make/break через существующий сервис

PS/2-драйвер видел break-коды и глотал их; DOOM-у нужны отпускания
(held-key движение). Без единого нового thunk'а:

- `Ps2Keyboard.DecodeEx` — полная информация (make/extended/isBreak);
  старый Decode — обёртка, shell-путь не тронут.
- `Platform.TryReadKeyRaw` — post-EBS отдаёт КАЖДОЕ событие: упаковка
  `bits0-7 make | bit8 ext | bit9 down | bit31 present` в свободном поле
  `AppReadKeyRequest.Reserved`; legacy unicode/scan для нажатий сохраняют
  лаунчер-контракт (breaks приходят нулями — матчинг лаунчера их игнорит
  естественно, проверено: меню живо).
- **SharpUserInput (DoomApp)**: PumpEvents (raw → held-таблица по DoomKey +
  `Doom.PostEvent` KeyDown/KeyUp), полный порт `BuildTicCmd` из
  SilkUserInput минус мышь, set-1→DoomKey таблица (QWERTY + стрелки +
  модификаторы + F-ряд).

## Тайминг: HPET-handoff → живой Stopwatch → 35 Hz

- Таблица +`HpetCounterAddress`/`HpetFrequencyHz` (identity-мапленный MMIO
  main counter + калиброванная частота — тот же data-handoff).
- **sdk Stopwatch из нулевого стаба стал настоящим**: HPET-backed, чтение
  каунтера через `[MethodImpl(NoInlining)]` — готча AOT-MMIO-hoist (ILC
  LICM выносит non-volatile MMIO-чтение из spin-петли) учтена сразу.
- Игровой цикл: pump → update → render → spin-wait до `nextFrame` с
  восстановлением после стола (map-load/wipe не проматывают бэклог кадров).

## Гэп по дороге: MemoryMarshal.Cast не переживал ILC

Первая реальная инстанциация `Cast<byte,uint>` (Renderer.WriteData) — ILC
«Code generation failed»: реализация шла через `Unsafe.AsPointer` +
`checked`-конверсию (conv.ovf требует overflow-хелпер по имени, которого
app-ThrowHelpers не нёс; ядро дженерик никогда не инстанцировало — мёртвый
generic-код не валидируется до первой инстанциации). Фикс: Cast переписан
в upstream-форму (ref-ctor Span, кламп вместо checked) + app-ThrowHelpers
дополнен до std-надмножества (Overflow/DivideByZero/ArrayTypeMismatch).

## Сопутствующее

- README «Отдельное спасибо»: +ManagedDoom (sinshu, GPL-2.0, изолирован
  отдельным приложением), PeNet переописан честно — боевой парсер лоадера
  (каждый запуск PE идёт через PeImageLayout/PeImports/PeRelocations), не
  «референс».
- Лицензионная проверка: Apache-2.0 (PeNet) ≈ MIT для наших целей
  (+patent grant); единственная несовместимость — с GPLv2 в одном бинаре,
  у нас разведено by construction (PeNet в ядре, GPL-код только в
  DOOM.EXE). Правило: не вкомпиливать Apache-вендор в GPL-аппы.

## Уроки

1. **Свободное поле в существующем request-струкnе >> новый сервис**: весь
   raw-ввод проехал через `Reserved` без thunk-плюмбинга; обратная
   совместимость лаунчера — бесплатно.
2. **Data-handoff в таблице** (FB, HPET) — правильный примитив для
   ресурсов общего адресного пространства: ноль вызовов на горячем пути.
3. Готчи из памяти проекта сработали как чеклист: AOT-MMIO-hoist пойман
   ДО прогона (NoInlining-ридер написан сразу), а не отладкой зависшего
   spin'а.
4. Generic-код в std валидируется только первой инстанциацией — «ядро
   компилит этот файл давно» ничего не говорит о непроинстанцированных
   путях.

## Что дальше (за пределами звезды)

- Звук/музыка: ISound/IMusic реальные (PC speaker? AC'97/HDA — новый
  драйверный фронт) — вендорные Null-заглушки пока.
- Мышь: PS/2 aux-поток сейчас дропается в драйвере.
- Kernel-консоль пишет в тот же framebuffer — при желании silent-режим
  на время игры.
- Config/savegame — discard до write-сервиса ядра.
- Отложенные фронты donext: вариантный dispatch, HW-fault→app-exception
  мост, слияние MinimalRuntime, net10-бамп.
