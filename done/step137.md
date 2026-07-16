# step 137 — ВЕХА: freestanding win-x64 PE launcher работает на bare metal

## Контекст

Замыкание ELF→PE миграции для лаунчера (donext workstream 2, north-star путь).
После PeNet (чтение PE, step133/135) и PE-loader-трансформов (flatten/relocate/
imports, step136) — **build-side (эмиссия PE-аппа без WSL) + kernel execute-side
(map + jump)**, и первый реальный managed-PE исполнен ядром.

## Результат

**Полная работоспособность.** HelloSharpFs собран как freestanding win-x64 PE
(`dotnet publish`, без WSL/cl.exe/ручного линка), ядро грузит его через PeLoader
(flatten → map@ImageBase → jump), TUI-файлпикер видит `.EXE`, запускает сам себя
(nested-launch лимит на второй раз — как задумано). ELF выпилен из app-batch.

## Build-side: freestanding win-x64 PE без WSL

Рецепт живёт в `apps_native/HelloSharpFs/HelloSharpFs.csproj` (gated на win-x64),
`dotnet publish -r win-x64` эмиттит PE напрямую. `build_launcher_win.ps1` — 20
смысловых строк (было 173 у ELF-скрипта): ensure CoffStub.Generator + publish.

Ключевые куски (зеркало OS.csproj, минус CoreCLR):
- **`__security_cookie`** через `CoffStub.Generator` (`[CoffDataSymbol]` на
  `WinCrtStubs.SecurityCookie`) — нативный data-`.obj` в `@(NativeLibrary)`, не
  ручной cl.exe-стаб. `__security_check_cookie` — C# `[RuntimeExport]` no-op.
- **LinkerArgs**: `/ENTRY:SharpAppBootstrap /SUBSYSTEM:NATIVE /BASE:0x400000
  /FIXED /NODEFAULTLIB` → self-contained PE (imports=0, relocs=0, ImageBase 0x400000).
- **`ExcludeNativeAotRuntime`** target: снять SDK-рантайм (`Runtime.WorkstationGC`
  + `Runtime.VxsortEnabled` + `bootstrapper.obj`) — иначе net8/ILC8 дублирует
  наш рантайм + тащит CRT-каскад (`_tls_index`/`operator new`/`__GSHandlerCheck`/…).
- **`DebuggerSupport=false`** (убирает `DotNetRuntimeDebugHeader`-экспорт),
  **`IlcDehydrate=false`** (иначе пустые string-литералы — dehydration ждёт
  stock-bootstrapper, который снят).
- **`__managed__Startup`** no-op стаб: ILC ссылается на него из module-startup,
  а `EntryPointSymbol=SharpAppEntry` переименовывает entry → символ не определён.
  ELF (`ld -e`) dead-stripил ссылку; win SDK-линк тащит весь obj.
- **net7.0 → net8.0** бамп (совпасть с ядром/major-9 GC-layout).
- **`ParamArrayAttribute`** в app-sdk MinimalRuntime (shared `SystemString.cs`
  теперь юзает `params char[]` — step134; app-std его не имел = `curated_csproj_drift`).

## Kernel execute-side

- **`PeLoader.TryLoad(MemoryBlock)`** (`OS/src/Kernel/Pe/PeLoader.cs`): copy →
  `PeImageLayout.TryFlatten` → map contiguous phys→VA@ImageBase (RWX v1) → blit →
  `ElfLoadedImage` (EntryPoint = ImageBase + AddressOfEntryPoint). Дальше тот же
  формат-агностичный `ProcessImageBuilder` + `JumpStub`.
- **Magic-dispatch**: `AppServiceBuilder` (service RunApp) + `ElfValidation.RunApp`
  (boot-batch) сниффят `MZ` → PeLoader. Batch теперь **PE-only** (единственный
  апп = HELLO.EXE; ELF hello/abiInfo/helloCs/marker убраны).
- **JumpStub ABI-фикс** (`JumpStub.Iced.cs`): entry-вызов клал startup block в
  **RDI** (SysV, legacy от ELF-апп) → win64 PE-апп читал RCX (=адрес entry,
  `call rcx`) как startup → дереф entry-кода → **#GP**. Фикс: `mov rax, rcx;
  mov rcx, r8; call rax` — startup в **RCX** (win64 arg0).
- **Legacy byte-эмиттер + CompareOrPanic** JumpStub'а **удалены** (по просьбе —
  «тыщу раз проверили, ни разу дрейфа»), только Iced.

## Ключевой инвариант (entry ABI)

Апп теперь win64 (первый арг в RCX), не SysV (RDI). JumpStub обязан класть
startup block в RCX. `ServiceAbi=WindowsX64` в peHello-entry + `.abi`-манифест
(ServiceAbi=0) — согласованы.

## run_build.ps1 / ESP

ELF-генераторы (`New-*ElfImage`, 226 строк) + ELF-стейджинг удалены; стейлы
(`HELLO/ABIINFO/MARKER/HELLOCS/FETCH.ELF` + `.abi`) активно чистятся; стейджится
только `HELLO.EXE` + `.abi` (AbiV2, ServiceAbi=0). Лаунчер-фильтр `.ELF`→`.EXE`.

## Отложено (следующие шаги)

- **Свип остальных legacy-эмиттеров** (Cr3Accessor, AppServiceBuilder-thunks,
  ByRefAssignRef, GcStackSpill, BootAsm-патчеры) — по той же причине.
- **Kernel ELF-файлы под нож** (Stage B): `ElfLoader`/`ElfParser`/`ElfTypes`/
  `ElfDiagnostics`/`ElfLoadValidation` + AppServiceBuilder ELF-ветка +
  ProcessValidation ELF-хедеры. Оставлены живыми (компилятся, unused) чтобы этот
  шаг был компилируемым.
- **Апгрейд лаунчера** на новую std (голые `byte*`/stackalloc → List/string/LINQ).
- **Фетч на PE-рельсы** + общий SDK (сейчас dormant).
- PeLoader: per-section NX/RO протекция, base-relocations для off-base, per-image
  .pdata/EH (сейчас Tier-B halt-on-throw + RWX + honor-ImageBase).

## Next

Апгрейд лаунчера + Stage-B ELF-removal + legacy-свип. Потом фетч на PE.
