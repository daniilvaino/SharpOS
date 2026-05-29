# step 114 — Release CoreCLR on bare metal + __CxxFrameHandler4 (FH4) EH personality

## Веха

**Стоковый форкнутый CoreCLR, собранный в Release (`/Ox`), хостится на SharpOS bare metal** — байт-в-байт тот же managed-бинарь, `coreclr_initialize` OK, `execute_assembly` exitCode=42, census OK=51 DEG=2 FAIL=7, launcher 4/4. Ближе к «настоящему живому .NET» чем Debug-форк.

Попутно реализован **полный `__CxxFrameHandler4`** — компактная FH4 C++ EH personality (search + catch-match + continuation + unwind), которую clang-cl эмиттит под оптимизацией вместо FH3.

Конфиги ортогональны и оба switchable:
- `$Configuration` (default **Release**) — ядро (C#/NativeAOT/ILC)
- `$ForkConfig` (default **Debug**) — CoreCLR форк; `-ForkConfig Release` на обоих раннерах (QEMU `run_build.ps1`, VBox `run_vbox.ps1`)

## Что вскрыл Release (по нарастающей, каждый — реальный фикс)

Release-codegen ходит по каноничным путям которые Debug не дёргал. Цепочка блокеров:

1. **debug-name guards** — `object.cpp`/`prestub.cpp` SharpOS-диаг использовали `GetDebugClassName`/`m_pszDebugMethodName` (`_DEBUG`-only члены MethodTable) безусловно → CS-ошибки в Release. Обёрнуты в `#ifdef _DEBUG`.

2. **native PGO** — Release инжектил `/LTCG /USEPROFILE:PGD` с путём к NuGet PGO-пакету (ломался на кириллическом username) + несовместим с нашим lld-link/SharpOS таргетом. `add_pgo()` → no-op под `CLR_CMAKE_TARGET_SHARPOS` (pgosupport.cmake). Теряем только profile-guided тюнинг **самого рантайма** (не managed-кода — тот JIT'ится RyuJIT'ом, PGO coreclr.dll на него не влияет); `/Ox` остаётся.

3. **ThinLTO bitcode → COFF** — Release включал `CMAKE_INTERPROCEDURAL_OPTIMIZATION` → clang-cl эмиттил LLVM bitcode объекты (`BC\xC0\xDE`); `coreclr_static.lib` оказывался bitcode-форматом, MSVC `link.exe` (наш kernel-линк) репортил `LNK1136 invalid/corrupt`. IPO **OFF** для SharpOS (configurecompiler.cmake + mscordac CMakeLists для mscordacobj, который собирается lib.exe).

4. **cdac contract descriptor** — Release-оптимизатор выкидывал scrape-payload из datadescriptor.cpp.obj (`could not scrape payload`). SHARPOS трактуется как WIN32 для cdac (clrdatadescriptors.cmake). + build-pack фиксы (`SharpOSBuild=true`, skip clr.packages/host.native, mscorrc.lib вместо .dll, explicit template instantiation в corhlprpriv.cpp).

5. **`_callnewh` trap-стаб** — Release `operator new` failure-path зовёт `_callnewh` (MSVC new-handler invoker); был CRT_STUB→panic. Real impl `return 0` (нет new-handler'а, alloc фейлится штатно).

6. **bad_alloc без OOM (ROOT)** — `operator new → malloc → null → _callnewh(0) → throw std::bad_alloc`, но `[HeapAlloc NULL]` не срабатывал. Причина: в `winapi_shim.cpp` strong-fallback `SharpOSHost_HeapAlloc(){ return nullptr; }` — Release-clang свернул тело в той же TU, превратив `malloc` в `xor eax,eax; ret` ещё до линковки; managed strong-export не имел шанса подставиться. Фикс: fallbacks → **`__attribute__((weak))`**. После — malloc маршрутится в managed kernel-heap, bad_alloc исчезает.

## __CxxFrameHandler4 (FH4) реализация

Под `/Ox` clang-cl эмиттит компактные FH4 EH-таблицы вместо FH3. У нас был полный FH3-handler, FH4 — паника-стаб. Реализован `OS/src/PAL/SharpOSHost/CxxFrameHandler4.cs` портом MSVC `ehdata4.h`/`ehdata4_export.h`:

- `ReadUnsigned`/`ReadInt` — back-reading varint (negLength/shift вычисляются без таблиц — C# 11 без collection-expr)
- `DecompFuncInfo` — FuncInfo4 + separated-segment IPtoState resolution
- `StateFromIp` — delta-encoded function-relative IP map, state stored +1
- search-pass: TryBlockMap4 + HandlerMap4 decode, `MatchHandler4` (mangled-name compare на image-relative `dispType`)
- **`dispOfHandler` IMAGE-relative** (не func-relative как FH3 — ключевое отличие, поймано через #UD на TargetIp+funcStart)
- continuation address decode (cont0/cont1)
- unwind-pass: `FrameUnwindToState` следует toState-цепочке через back-offset (`entryStart - nextOffset`; before-buffer = state −1), НЕ `state--` (нелинейные цепочки)

Wiring: `SehDispatch` вызывает personality generically → FH4 export дёргается; catch-marker (0x100) уже распознаётся. Валидировано живым прогоном: bad_alloc (до weak-fix) корректно ловился FH4, catch-funclet выполнялся, continuation резюмил. После weak-fix bad_alloc не возникает — FH4 спит-но-готов для любого Release C++ EH.

Сопутствующие правки `SehDispatch.cs`: `IsValidIp` теперь проверяет реальную разрешимость через .pdata/dynamic/static/stub ranges (не широкий диапазон от imageBase); при `LookupFunctionEntry==null` — recovery через `TryActivateFrameChain` (то же в second-pass/RtlUnwind).

## Методология (ценное)

- **QEMU `-d int,cpu_reset,guest_errors -no-shutdown`** — отличил triple-fault от shutdown; ловил #UD/PF на CPU-уровне до того как handler развалился. (см. step113 memory)
- **`pmemsave` через QMP + `info registers`** — дамп стека/регистров висящего QEMU без пересборки.
- **Дифференциальная изоляция** — bad_alloc: добавил `[HeapAlloc NULL]` принт → НЕ сработал → значит не наш HeapAlloc → дизассемблировали malloc → `xor eax,eax;ret` → strong-fold root. Прямой замер вместо теории.

## Файлы

### Fork (dotnet-runtime-sharpos/)
- `pal/sharpos/winapi_shim.cpp` — strong→weak fallbacks (**ROOT**)
- `pal/sharpos/crt_imp_stubs.cpp` — `_callnewh` real impl
- `eng/native/configurecompiler.cmake` — IPO OFF для SharpOS
- `src/coreclr/pgosupport.cmake` — PGO no-op
- `src/coreclr/clrdatadescriptors.cmake` — SHARPOS как WIN32
- `src/coreclr/dlls/mscordac/CMakeLists.txt` — IPO OFF для mscordacobj
- `build_clr_sharpos.ps1` + `eng/Subsets.props` — SharpOSBuild, pack-сборка
- `src/coreclr/inc/corhlprpriv.cpp` — explicit template instantiation
- `vm/object.cpp` + `vm/prestub.cpp` — debug-name guards под `#ifdef _DEBUG`

### Kernel (OS/)
- `OS/src/PAL/SharpOSHost/CxxFrameHandler4.cs` (**new**) — FH4 EH personality
- `OS/src/PAL/SharpOSHost/CrtAndEhStubs.cs` — FH4 panic-стаб убран
- `OS/src/PAL/SharpOSHost/SehDispatch.cs` — FH4 continuation + IsValidIp realizable + frame-chain recovery
- `OS/src/PAL/SharpOSHost/CrtHeapStubs.cs` — real-OOM детектор (HeapAlloc null print)
- `OS/OS.csproj` — `$(CoreClrForkConfig)` plumbing
- `run_build.ps1` + `build_media_xorriso.ps1` + `run_vbox.ps1` — `$ForkConfig` проброс (QEMU + VBox)
- `.gitignore` — track `work/normal-hello/` source (bin/obj остаются ignored)

### Probe harness (now in codebase)
- `work/normal-hello/{Program.cs,NormalHello.csproj}` — CoreCLR-hosted census/regression suite (стоковый .NET бинарь, хостится байт-в-байт). Добавлены этой сессией пробы: Timer (50ms), Task.Delay(3s,ct) cancellation, ThreadPool stress (1000×20), FP/XMM smoke, GC FRAMEREG_REL refs across Collect, System.Text.Json reflection roundtrip.

## Lessons learned

1. **Release vs Debug = разные CRT/codegen пути.** Release ходит по каноничным operator-new/EH/CRT путям которые Debug обходит. Каждый незаполненный стаб/strong-fold всплывает. Конечное число, но цепочка длинная.
2. **strong-fallback в той же TU = clang свернёт до линковки.** Любой `extern "C" Foo(){...}` fallback который должен переопределяться managed-export'ом ОБЯЗАН быть `__attribute__((weak))` — иначе Release-LTO/fold убивает подстановку. Аудитить остальные SharpOSHost_* fallbacks в winapi_shim/crt_imp_stubs.
3. **FH4 dispOfHandler image-relative, FH3 func-relative** — тонкое отличие, ловится только живым прогоном (#UD на неверном TargetIp). Комменты в reference (`__RVAtoRealOffset` vs `__FuncRelToRealOffset`) — load-bearing.
4. **«НЕ сработавший диагностический принт» — сам по себе сигнал.** `[HeapAlloc NULL]` не появился → bad_alloc не из нашего HeapAlloc → правильный вывод о strong-fold, а не дальнейшее копание EH.

## Что откладывается / следующее

- Аудит остальных `SharpOSHost_*` strong-fallbacks на weak (превентивно).
- Release-only известные limits те же что Debug (Socket/FS-write/GZip/Process.Start — инфра отсутствует, не Release-специфично).
- FH4 catch-object construction (`dispCatchObj`/copy-ctor) не реализован — паритет с FH3, вся батарея проходит без него.
- README comparative table: добавить «Release CoreCLR-hosted» как достигнутую веху если решим отразить tier user-facing.
- IST для #PF/#DF (из step113) — всё ещё стоит, чтобы будущие stack-overflow печатали panic.
