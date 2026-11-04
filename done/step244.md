# step244: шина USB — из пробы в Hal/Usb

## Контекст

release.md §1: драйверная часть `UsbProbe` стояла в `Kernel/Diagnostics` под именем
пробы, хотя `RunXhci` вызывался из `PostEbsBoot` без флага и делал всю работу шины:
запуск контроллеров, сброс порта, слот, адрес, конфигурация функций, привязка
клавиатуры, CDC-журнала и флешки. Печать (`[xhci]` ≈10 строк на устройство, `[umsd]`)
была вшита в этот путь. Аудит откладывал перенос на после релиза («проверить на
железе нечем»); перенесён до §12, чтобы стенд проверял ту раскладку, что уйдёт в
релиз.

## Архитектура

- **`Hal/Usb/UsbBus.cs`** — `UsbBus.Start(observer, serialLog)`: `Xhci.StartAll`,
  обход портов каждого контроллера (NoOp, сброс, слот, адрес, дескриптор,
  `TryConfigureFunctions`), затем клавиатура (`ScancodeSource.TryAttachUsb`),
  CDC-ACM с первой строкой и `BootLog.AttachSerial`, флешка
  (`UsbMassStorage.TryAttach`). Ни строки печати и ни одной ссылки на `Probes`.
- **Наблюдатель** — `IUsbBusObserver` (`ControllerUp`, `PortDone`, `Configured`) и
  `UsbPortOutcome` (докуда дошёл порт, коды, дескриптор). Шина решает и действует,
  наблюдатель только читает и может спрашивать устройство в точках, где оно обязано
  ответить (дампы до и после конфигурации).
- **`UsbProbe`** — обзор контроллеров (`Run`, до EBS, как был), `Observer`
  (печатающий `Watcher`, когда включён `Probes.UsbReport`), `AfterStart`: строки о
  клавиатуре и CDC, эхо, CTAP, проверка флешки (сектор 0, восстановление,
  запись) и итог `[usbsum]`. Печать — та же, что была.
- **`Probes.UsbReport = Diagnostics`** — построчный отчёт. `[usbsum]` печатается
  всегда: одна строка с вердиктом для машины, с которой иначе нечего прочитать.
- Порядок строк чуть сдвинулся: строка о CDC теперь после подключения флешки (сама
  привязка журнала к порту — по-прежнему до неё).

## Проверка

Рабочая копия, WHPX, `-UsbOnly` (ESP на USB-флешке, SATA-диска нет: ядро монтирует
том через свой стек), клавиатура и мышь по USB.

- Прогон 306 (диагностика включена): строки `[xhci]` совпадают с прошлым `-UsbOnly`
  прогоном (порт 2 флешка — слот 1, порт 4 клавиатура — слот 2, порт 5 мышь —
  слот 3), плюс дампы и census; `[umsd] … PASS`, восстановление PASS;
  `[usbsum] … keyboard=yes storage=yes … PASS`; DATATEST 29 — 17/17, `ls /` с
  флешки.
- Прогон 307 (`-UsbOnly -NoDiagnostics`, как в релизе): от USB в журнале одна
  строка `[usbsum] … keyboard=yes storage=yes … PASS`; том с флешки смонтирован,
  DATATEST 29 — 17/17, fsck чистый.
- Батарея 308 (`s296.sh`, обычная конфигурация, без xHCI): 65/66. Путь без
  контроллеров: `[xhci] no controller came up`, `[usbsum] … SKIP` — как было. Упал
  PROCTEST №15 (доля времени счётчика рядом со сборкой без паузы) — SYM-008, флейк
  по времени, тот же, что в батарее 299; второй прогон PROCTEST в батарее — 80/0.
  DATATEST под `--gc-stress` — 363/0 (тест 24 step243 прошёл).

## Файлы

- `OS/src/Hal/Usb/UsbBus.cs` (новый).
- `OS/src/Kernel/Diagnostics/UsbProbe.cs` — наблюдатель и диагностика.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `UsbReport`.
- `OS/src/Boot/PostEbsBoot.cs` — `UsbBus.Start` + `UsbProbe.AfterStart`.
- `docs/nativeaot-nostd-kernel-limits.md`, `docs/boot-order.md`, `donext.md`,
  `release.md`.

## Дальше

§1: ROM (за пользователем). §9 «Ошибки (не CoreCLR)».
