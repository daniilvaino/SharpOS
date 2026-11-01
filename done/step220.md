# step 220 — релиз 0.1, раздел 1: чистка ядра и раскладка образа

## Почему

Курс на релиз 0.1 (`release.md`). Перед ним — порядок: аудит ядра и
бинарников двумя агентами (`work/release/cleanup_kernel.md`,
`work/release/binaries.md`), по их отчётам — удаление мёртвого и разового,
журнал релиза без тестового шума, образ, в котором лежит только нужное.

## Ошибка, найденная аудитом

**Exec-пулы не регистрировались в `SehUnwind` в сборке без тестов.** С
step123 размотка считает адреса возврата внутри пулов шеллкода (BigStack,
RegionBarrier, JumpStub, IDT, AsmExec) кадрами-листьями. Регистрация жила в
`DumpExecBuffers`, а та вызывалась только под `Probes.Diagnostics` — то есть
только в сборке с тестами. В `-NoTests` размотка сквозь эти стабы обрывалась,
как до step123. Теперь `RegisterExecPools` зовётся всегда.

Попутно: `PciScan` стал тестом, а `Ahci` искал контроллер в таблице PCI, не
поднимая её сам, — при загрузке с SATA до USB диск бы не нашёлся. `Ahci`
зовёт `Pci.Init` (идемпотентен) сам.

## Удалено

- `TestApp/DemoApp.cs`: вызов после `LauncherBoot.Run` недостижим (тот
  выключает машину); ветка «нет карты памяти» звала бы `KernelHeap.Alloc` без
  `Init` — теперь `Panic.Fail`.
- Мёртвые пробы (никто не зовёт или только выключенный флаг): `BootAsmProbe`,
  `ProcessDiagnostics`, `KernelGcPreciseSmokeProbe`, `IdtProbe`,
  `ExceptionProbe`, `ProtectPagesProbe`, `InputDiagnostics`,
  `Stub5_5_TestHarness`; ступени 5.5a и ingress в `EhProbe`; печать 5.1–5.5b в
  `ExceptionEngine` и флаг `EhRealDispatch` (`RhpTest_ThrowIngress` — всегда
  `DispatchEx`); три блока `#if false` (старые ручные эмиттеры);
  `GcStaticsMaterializer.Diagnose()`.
- Пробы подъёма, покрытые другими: EH L1, L2, L4–L7, 5.3; `CoffGcInfoDump`,
  `GcInfoResolverSmoke`, `GcContextSpillSmoke`, `GcHeapSmoke`, `GcStress`,
  `KernelHeapSmoke`; `TebFacade`, `Atomics`, `ThreadPingPong`;
  `HwFaultBridge.FaultClassify` (разборщик фронтов step71).
- Дубли AotTests: `Preemption`, `PreemptedAlloc` (её `CEILING-HIT` мерил
  хост: порог из времён таймера 100 Гц), `rethrow` в NativeAotProbe (искал имя
  кадра, которого в трассе нет; верно проверяют L20 и AotTests).
- Записи этих проб в `tools/probe_report.ps1`.

Около 4 000 строк.

## Журнал релиза

`-NoTests`: 59 строк вместо 167, ни одного FAIL. Под `Probes.Diagnostics`:
установка шеллкодов (отказ печатается всегда), `naot-init`, `[cr3]`, pager,
`[phaseN done]`, `[exec-pool]`, `[fatdir]`, `[fsread]`, строки лаунчера,
`[screen]`, `heap grow`, `[stub] jumpstub`, `[appwalk]`; `[appgc]` в SDK — по
биту `SettingReportCollections` (2), его ставит ядро с диагностикой. Дамп
перехваченной аппаратной ошибки (~45 строк на каждый `NullReferenceException`)
и трасса `[seh-step]` — тоже; первая строка `HW fault: vec= RIP= CR2=`
остаётся. Тестовый кадр после EBS и квадрат `SerialSmoke` — только с
тестами. Без CoreCLR BigStack на 16 МиБ не выделяется.

Ложные красные: `[ahci]` на ATAPI (QEMU, загрузка с USB) — «нет SATA-диска»;
`[pci]` без AHCI — PASS (машина с NVMe); `[fat] lfn` без CoreLib — skip.
Честные красные (`enum.toString`, `term wide`, `ModuleInitializer`) остались.

`-NoDiagnostics`: тесты без шума (`SHARPOS_NO_DIAGNOSTICS`). Сборка `-NoTests`
без предупреждений: прагма CS0162 во всех файлах с `Probes.*` (было 18).

## Образ

- `<SharpOsName>` в `.csproj` — имя на образе (`tools/Apps.ps1`):
  `sample_pipegen`, `sample_pipesynth`, в `/bin` всегда. Длинные имена — LFN
  (`mcopy` пишет, наш FAT читает с step208); проверка «8 символов» снята.
- PIPEFILT, PIPECNT, PIPESPY, VIEWFILT — `tests`; USBTEST — `none` (не на
  образ). Устаревшие `.EXE` из заготовки ESP удаляются.
- `-NoTests`: без `/tests`, без `fswrite.bin`, без предразметки журнала
  (ядро создаёт 1 МиБ само), `PATH=/bin;/games`, размер образа по
  содержимому (×1,25 + 64 МиБ, кратно 16): lite — 112 МБ вместо 512. С
  тестами — 512, как было.
- `TZ=+00:00`: образ одинаков, кто бы его ни собрал.
- KQLBABY влит в KQLTEST (`Engine.cs`).
- История — `$HOME/.sh_history`, `$HOME/.kql_history` (было в корне).
- Удалён `OS/.qemu/esp/esp.zip` (144 МБ, копия ESP от 25.10), который
  `New-EspImage` клал в каждый образ.

## Переименования

`ExitBootServicesProbe` → `Boot/PostEbsBoot` (это основной путь загрузки;
флаг `ExitBootServicesExperiment` и мёртвая ветка CoreCLR до EBS свёрнуты),
`FatProbe` → `Kernel/File/BootVolume` (единственное монтирование ФС).

## Проверка

Рабочая копия, WHPX, `-SkipCoreClr`. С тестами: `ran=41 failed=1` — PROCTEST
15 (порог ¼ от нагрузки хоста, step219; прогоном раньше — 42/0); AOTTESTS
319/319 ×3, DATATEST 239/0 и 236/0, PROCTEST 80/0, KQLTEST 7/0 и 9/0,
PIPEPERF 20/0, JSONTEST 64/0, GVMTEST 10/0; все `eh L8–L20` зелёные.
`-NoTests`: `ls`, `FETCH`, `sample_pipegen 3 | KQL`, `which sample_pipesynth`
— 6/6; интерактивно (QMP) `echo hi`, `exit` — `/home/.sh_history` создан
нашим FAT, fsck чистый. Предупреждений компилятора — 0 в обоих режимах.
DATATEST 19 ловил `processors` в панели FETCH — компактный FETCH (step219)
пишет `N cpu`, тест поправлен.

## Не сделано

- Раскладка full (NormalHello, Bench, StarlingProbe в `tpa.txt`),
  `CoreClrProbe.RunAssembliesInSequence` — в раздел CoreCLR.
- Драйверная часть `UsbProbe` → `Hal/Usb` — после релиза.
- ROM вместо `GAME.NES` — за пользователем.

## Файлы

Ядро: `Boot/BootSequence.cs`, `Boot/PostEbsBoot.cs` (из
`ExitBootServicesProbe.cs`), `Boot/ExceptionEngine.cs`, `Boot/EH/*Patcher.cs`,
`Boot/EH/HwFaultBridge.cs`, `Hal/Ahci.cs`, `Hal/Platform.cs`,
`Kernel/Diagnostics/{Probes,EhProbe,NativeAotProbe,AhciProbe,PciProbe,Sampler,FatWriteProbe}.cs`,
`Kernel/File/BootVolume.cs` (из `FatProbe.cs`), `Kernel/Memory/*`,
`Kernel/Paging/Cr3Accessor.cs`, `Kernel/Exec/JumpStub.cs`,
`Kernel/Process/{LauncherBoot,AppServiceBuilder,AppServiceBuilder.Processes,AppServiceTable}.cs`,
`PAL/SharpOSHost/SehDispatch.cs`, `OS.csproj`; удалённые — выше.
SDK: `AppGC.cs`, `AppServiceTable.cs`. Приложения: `KqlTest` (+`Engine.cs`),
`DataTest`, `ProcTest`, `Shell` (`LineReader`, `autorun.sh`, `Completion`),
`KqlApp`, `PipeGen`, `Synth`, `PipeSpy`, `HistApp`, `JsApp`, `.csproj` стадий
и `UsbTest`. Скрипты: `run_build.ps1`, `tools/Apps.ps1`,
`tools/probe_report.ps1`. Документы: `release.md`, `README.md`,
`docs/boot-order.md`, `docs/build-warnings.md`,
`docs/nativeaot-nostd-kernel-limits.md`.
