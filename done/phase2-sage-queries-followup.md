# Phase 2 sage queries — follow-up

## Контекст обновлений

Первый round queries дал convergence на:
- Linux baseline (не Mac)
- Skip hostfxr/hostpolicy в spike
- Direct hosting через `coreclr_initialize`
- Runtime trace > static analysis
- Stub policy: fatal trap on must-have, recoverable on optional
- Test progression: minimal hello → coverage → threading

Sage 1 critical correction: PAL не standalone DSO. `nm -D --undefined-only`
даёт libc/pthread, не PAL. Sage 2 дал proper readelf-based pipeline.

Согласовали с user: WSL2 для spike, **.NET 10 LTS** target (gc-experiment'овский release/7.0 — .NET 7, EOL).

После этого открылись gap'ы которые первый round не покрыл. Ниже — focused
follow-ups на оба sage.

---

## Query 3 (Sage 1 follow-up — architectural blind spots)

После твоих tarpits ответов на C++ exceptions / TLS / VirtualAlloc semantics
осталось пять unanswered вопросов:

### 1. Below-PAL dependencies — minimum viable embedded approach

Ты упомянул C++ exceptions/libunwind как **самое серьёзное Phase 6
architectural decision**. Но не дал scope estimate для **всего bottom-of-PAL
stack** для embedded scenario.

CoreCLR на Linux под капотом использует:
- `libstdc++` (C++ stdlib — std::vector, std::string, RTTI, exceptions)
- `libc` (стандартные C functions — malloc, printf, memcpy, errno, fopen)
- `libunwind` (DWARF CFI unwinding для C++ exceptions)
- `libdl` (dlopen/dlsym для libclrjit.so)
- `libpthread`, `libm` (используются PAL'ом, не самим runtime'ом)

В embedded scenario (kernel-of-Zephyr / mbed / freestanding):
1. Кто-нибудь уже port'ил CoreCLR в такой environment? Что мы можем reuse?
2. Realistic minimum subset libstdc++/libc++? (full vs subset vs custom)
3. Static linking всей цепочки в один binary — feasible? Какие подводные?
4. Если C++ exceptions невозможны — что точно от runtime ломается? GC? JIT?

### 2. EH inversion — can we expose our managed EH to CoreCLR?

У нас уже работающий managed EH в Phase 1 (17/17 gates). Это для
**AOT-emitted** code path (Win-style `.pdata`/`.xdata` walk).

CoreCLR's JIT-emitted code uses **different** unwind format (DWARF CFI on
Unix). Stock vm/exceptionhandling.cpp parses этот in-memory format при
throw.

**Вопрос**: вместо того чтобы port'ить libunwind для CoreCLR, можем ли мы
**inverse the model** — expose to CoreCLR функцию "throw managed exception"
и пусть CoreCLR call'ит ЕЁ через PAL? То есть наш managed EH дёргается
when JIT-compiled code throws?

Или это ломается на семantic mismatch — наш .pdata-based unwinder не знает
как unwind через JIT-generated frames с DWARF unwind info?

Если можем — это упрощает Phase 6 значительно (нам не нужен второй unwinder).

### 3. Build target strategy

Two path forward для CMake target:

**Option A**: Add new "SharpOS" target в `eng/native/configurecompiler.cmake`
+ patches к build/native/build-runtime.sh. Required `sharpos-x64` triplet
recognized всем pipeline'ом.

**Option B**: Build for `win-x64` target, потом hand-patch link step
заменяя Win32 deps на наши.

A — больше upfront work, но clean. B — гадкий, но quick для spike.

Что бы ты делал? И есть ли третий path который мы не видим?

### 4. PAL output format from C# (NativeAOT-emitted static archive)

Per plan.md мы пишем PAL **на C#** через `[UnmanagedCallersOnly]`. NativeAOT
умеет emit static lib (`.lib`/`.a`) с C-ABI symbols.

**Вопрос**: пробовал ли кто-нибудь link'ить такую NativeAOT-emitted static
archive **с CMake-built CoreCLR**? Есть ли подводные:
- Symbol mangling differences (NativeAOT vs Clang/MSVC)?
- `.tls` section conflicts (NativeAOT runtime initializes own TLS)?
- Static initializer ordering (NativeAOT's `RhpCheckStartUp` vs CoreCLR's
  CRT init)?

Если этот path не работает — нужен C wrapper layer (`extern "C"` C functions
calling our managed C# funcs через function pointers). Двойной overhead.

### 5. "Spike succeeded" — concrete criteria

Mы сказали "managed Hello World работает с JIT". Но что **именно** мы
наблюдаем чтобы decide spike pass/fail?

Конкретные logs/outputs которые validate:
- "PAL initialization completed" log line?
- "JIT compilation finished" log line?
- "hello" в stdout?
- Exit code == 0?
- Hardware exception delivery NOT exercised (because Hello shouldn't throw)?

Что **указывает** что наш PAL contract sane vs broken? И как distinguish:
"spike сработал" vs "спайк сломался по тривиальной причине вне PAL contract"?

---

## Query 4 (Sage 2 follow-up — technical specifics)

Твои pipelines + code paths были solid. Несколько dig-deeper questions:

### 1. CMake-side: точный SHARPOS_PAL=ON pattern

Можешь дать точные **CMake patches** к `pal/src/CMakeLists.txt` чтобы:
- Skip компиляцию Linux PAL .cpp files
- Не emit'ить `libpalrt.a`
- При линковке `libcoreclr.so` substitute `palrt` target на наш external
  static archive (passed via `-DSHARPOS_PAL_LIB=path/to/libpal.a`)

Какой CMake idiom самый clean? `target_link_libraries` override?
`add_library(palrt INTERFACE IMPORTED)`? Или просто `set(PAL_TARGET ...)`?

### 2. NativeAOT static-lib quirks при link-step

Когда NativeAOT публикует `.lib`/`.a`:
1. Что попадает в `_DllMainCRTStartup` / CRT init? Это collid'ится с
   CoreCLR's static initializers?
2. Какие symbols **обязательно** export'ятся из NativeAOT runtime
   (`RhpReversePInvoke`, `RhpNewFast`, etc.)? Они conflict'ят с CoreCLR's
   own runtime helpers?
3. Если у нас две GC heap'a живут в одном binary (наш kernel GC + CoreCLR
   GC), они оба регистрируют `__chkstk` / `__security_cookie` / прочее?
   Конкретно — какие linker conflict'ы реалистично появятся?

### 3. CRT initialization order

Когда первый PAL call случается?

Hypothesis: до `main()`. CoreCLR's static C++ initializers могут вызывать
PAL functions (e.g. `CRITICAL_SECTION` global instance, initialized via
`InitializeCriticalSection`). 

Если так:
- В каком order static initializers run vs PAL_Initialize?
- Какие PAL functions safe-to-call before PAL_Initialize?
- На SharpOS у нас тоже static init pipeline (NativeAOT runs `cctors` через
  ClassConstructorRunner). Conflict'ы?

### 4. libstdc++ vs libc++ choice для embedded

Если мы порт'нём C++ stdlib subset, какой выбрать как baseline?
- **libstdc++** (GNU) — большой, GPL-with-runtime-exception, GCC-tied
- **libc++** (LLVM) — modular, BSD, Clang-friendly, embedded variant
  (`libcxx-no-exceptions`) существует

CoreCLR builds с обоими? Какой меньше assumption делает о underlying OS?

И конкретный вопрос: если мы build'им CoreCLR с `-fno-exceptions` (чтобы
обойти libunwind), **что точно** breaks? `vm/`'s `PAL_TRY/PAL_EXCEPT`
patterns? Specific files?

### 5. Spike measurement — concrete instrumentation

Ты предложил `SHARPOS_PAL_TRACE("name")` macro. Конкретно:
- Где **точно** в каждом pal/src/ .cpp file его поставить? (Function entry
  обычно достаточно — но какие callbacks через `PALAPI` macros тоже надо
  cover?)
- Output format для analysis: `[pal-call] Name args=...`? Stack trace на
  каждый call? Counter aggregation в end-of-process?
- Какие **ожидаемые** counts для Hello World? Если `VirtualAlloc` called
  10 раз — нормально. Если 10000 — что-то wrong. Estimate.

Если есть git repo с историческим Linux trace — point us. Если нет —
estimate ranges.

### 6. Hello.dll + System.Private.CoreLib.dll для .NET 10

Конкретно:
- Какой **smallest possible** managed binary что exercises full JIT path?
  (Sage 1 said "Console.WriteLine"; ты дал coverage smoke с
  StringBuilder/Dict/Sleep)
- Должны ли мы использовать `dotnet publish` или ручной csc compile?
- System.Private.CoreLib — full size или есть способ trim?
- TPA list (Trusted Platform Assemblies) — что **минимум** нужно перечислить?

---

## Что мы хотим обратно

Те же rules: file:line refs, concrete commands, no fluff. Plus:
- **Concrete CMake patches** где они нужны
- **Concrete failure modes** что мы можем encounter но not anticipate
- **Existing precedents** (другие embedded CoreCLR ports — kestrel, mbed,
  Zephyr-related) — если знаешь о таких

Format: per-query numbered list. Direct.
