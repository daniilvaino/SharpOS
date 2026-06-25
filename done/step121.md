# step 121 — CoffStub.Generator + kernel-side libcmt cut

## Цель

Закрыть kernel-side часть libcmt cut'а (step 120 закрыл fork-side).
Архитектурно: `libcmt.lib` теперь упомянута только внутри
`coreclr_static.lib` (merge на post-build форка); kernel C# linkline
её не видит совсем. OS.obj's единственная ссылка на native data
symbol (`__security_cookie`) удовлетворяется compile-time-emitted
`.obj` через **CoffStub.Generator** — никаких ручных `.c` файлов в
дереве.

## CoffStub.Generator (новый build pipeline)

### Зачем

ILC's `[RuntimeExport]` на static field **не эмиттит** native data
symbol — историческое ограничение ILC codegen. Раньше у нас в
`CrtAndEhStubs.cs` стояли "ложно-работающие" `[RuntimeExport]`-ы на
`__security_cookie` / `_fltused` / `_tls_index`; выглядели как work,
но реально symbol'ы тащились из libcmt'овой копии через
`/FORCE:MULTIPLE` collision.

CoffStub.Generator закрывает gap: marker-attribute на C# static field
+ MSBuild Task который сканит C# код Roslyn'ом, эмиттит tiny
self-contained COFF `.obj` с этим полем как **external native data
symbol**. `.obj` автоматом попадает в `@(NativeLibrary)`, линкер
видит `__security_cookie` как обычный data symbol.

### Архитектура

```
bootasm/CoffStub.Generator/
├── CoffStub.Generator.csproj      ns2.0 MSBuild Task assembly
├── CoffDataSymbolAttribute.cs     marker-attribute (master)
├── CoffWriter.cs                  pure COFF binary emitter (~210 строк)
├── EmitCoffStubsTask.cs           Task с Roslyn-сканом (~290 строк)
└── CoffStub.Generator.targets     auto-import MSBuild props
```

### Consumer usage

```csharp
[BootAsm.CoffDataSymbol("__security_cookie", Section = ".data", Alignment = 8)]
public static ulong SecurityCookie = 0x2B992DDFA232UL;
```

OS.csproj добавляет один `<Import>` на .targets — это автоматически:
- Включает master `CoffDataSymbolAttribute.cs` в Compile (через
  `<Compile Include="$(_CoffStubGeneratorDir)CoffDataSymbolAttribute.cs" Link="..." />`)
- Регистрирует MSBuild Task через `<UsingTask>`
- Запускает `EmitCoffStubsTask` after `CoreCompile` / before `IlcCompile`
- Эмиттит `.obj` per-class в `OS/obj/native/<ClassName>.CoffData.obj`
- Добавляет emitted'ы в `@(NativeLibrary)`
- Touch'ает `CoffStubs.stamp` для incremental build

### Validation rules (в EmitCoffStubsTask)

- Field must be `public static`
- Type ∈ {byte, sbyte, short, ushort, int, uint, long, ulong, float, double}
- Initializer must be compile-time constant
- Section ∈ {.data, .rdata, .bss}; .bss requires zero init
- Alignment must be power of 2 in 1..8192

Любая ошибка → MSBuild fail с конкретным error message.

### COFF emission (CoffWriter.cs)

Целево минимальный COFF AMD64 emitter, no relocations / debug info /
COMDAT / big-obj layout. Per-class output:
- COFF header (20B)
- 1 section header (40B): `.data`/`.rdata`/`.bss` with proper
  IMAGE_SCN_* flags + alignment
- Section payload (concatenated symbol data with per-symbol alignment)
- Symbol table (18B per external symbol)
- String table (4B size + null-terminated names)

Total для одного `__security_cookie` symbol'а: ~108 байт.

Reference — `dotnet-runtime-sharpos/src/coreclr/tools/aot/ILCompiler.Compiler/
Compiler/ObjectWriter/CoffObjectWriter.cs`. Взято концептуально
(layout / size / characteristics flags), **без import** зависимостей
на ILCompiler.DependencyAnalysis / Internal.TypeSystem.

## Kernel-side libcmt cut completed

### OS.csproj

- Убран explicit `<LinkerArg Include="libcmt.lib" />`
- Сохранён `<LinkerArg Include="/NODEFAULTLIB:libcmt.lib" />` — игнорить
  embedded `/DEFAULTLIB:libcmt` директивы в .obj'ах
- Добавлен `<Import Project="..\bootasm\CoffStub.Generator\CoffStub.Generator.targets" />`

### CrtAndEhStubs.cs cleanup

Удалены три "ложно-работающих" `[RuntimeExport]` на static field
(`__security_cookie`, `_fltused`, `_tls_index`) — они никогда не
работали (ILC gap), просто маскировали libcmt'ову копию через
`/FORCE:MULTIPLE`. Block-комментарий с roadmap'ом ушёл.

Добавлен `SecurityCookie` обратно как managed field через
`[CoffDataSymbol]`:
```csharp
[BootAsm.CoffDataSymbol("__security_cookie", Section = ".data", Alignment = 8)]
public static ulong SecurityCookie = 0x2B992DDFA232UL;
```

`_fltused` / `_tls_index` пока остаются satisfied'ed из libcmt'овой
копии внутри merged `coreclr_static.lib` (OS.obj их не references).

## Кросс-tier чистка

### `apps/HelloSharpFs/security_cookie.c` удалён

Транзитный leftover от ручных WSL ELF build экспериментов. Реальный
WSL build (`build_launcher_wsl.ps1`) генерит `.c` ad-hoc в `obj/`
дереве каждый билд — он не tracked.

### Verification gate — `tools/verify_kernel_externals.ps1`

PowerShell-скрипт через `dumpbin /symbols OS.obj` парсит **UNDEF
external references** и сверяет со whitelist'ом ожидаемых категорий
(Rh runtime helpers / coreclr_* host API / CRT primitives / EH
personalities / vcrt-scrt scaffolding / etc).

Текущее состояние OS.obj: **9 UNDEF references**, all whitelisted:
- 2 × `coreclr_initialize` / `coreclr_execute_assembly` (host API)
- 1 × `__security_cookie` (now satisfied by CoffStub.Generator emission)
- 6 × `SharpOSHost_GetCurrentFrame` / `RunCxxCtors` / `GetCtorDiag` /
  `GetCtorTable` / `SetCtorLimit` / `SetCtorSkipMask` — fork-side
  exposed diagnostic backdoors

Run from VS Dev cmd:
```
pwsh -File tools\verify_kernel_externals.ps1
```

Без VS Dev cmd `dumpbin.exe` не в PATH — gate не запустить
automatically. Когда понадобится polling в CI — можно extend'ить
запуском через vswhere lookup, но пока manual.

### `run_build.ps1` — `$env:VSLANG = "1033"`

Force MSVC toolchain (`cl.exe`, `link.exe`) emit'ить английские
диагностики вместо локализованных CP866 — иначе re-read'ed
`last_build.log` как UTF-16LE мангалится.

## README

Способ 3 в Инварианте 1 переписан с "PowerShell `.c` codegen для
security_cookie" на **CoffStub.Generator + `[CoffDataSymbol]`
pattern**. Пример из managed C# приведён. ELF apps пока на старом
PowerShell-emitted `.c` пути; архитектурно дрейфуют к тому же
compile-time approach.

## Файлы (этот коммит)

```
bootasm/CoffStub.Generator/             (NEW project, ~750 строк)
  CoffStub.Generator.csproj
  CoffStub.Generator.targets
  CoffDataSymbolAttribute.cs
  CoffWriter.cs
  EmitCoffStubsTask.cs

OS/OS.csproj                            + /NODEFAULTLIB:libcmt + Import .targets
OS/src/PAL/SharpOSHost/CrtAndEhStubs.cs — dummy [RuntimeExport]'ы удалены,
                                          + SecurityCookie [CoffDataSymbol]
run_build.ps1                           + $env:VSLANG = "1033"
tools/verify_kernel_externals.ps1       NEW dumpbin gate
README.md                               — способ 3 переписан
docs/coreclr-hosted-limits.md           — generic as T статус сверен (stale info refresh)
docs/nativeaot-nostd-kernel-limits.md   — generic as T статус сверен
done/step120.md                         NEW writeup (fork commit landed separately)
done/step121.md                         NEW writeup (этот)
apps/HelloSharpFs/security_cookie.c     DELETED (транзитный leftover)
```

## Verified state

- Census: **OK=113 DEG=2 FAIL=8** — без регрессий vs step 119
- EBS + Launcher 4/4 + Phase E threading + drivers + CoreCLR — все green
- `verify_kernel_externals.ps1`: **✓ Clean** — все 9 UNDEF symbols
  в whitelist

## Архитектурно: где какие .obj приходят в kernel link

```
OS.obj                                  ← managed C# через ILC (наш)
*.CoffData.obj (из @(NativeLibrary))    ← CoffStub.Generator emits
  └─ только: __security_cookie
coreclr_static.lib                      ← C++ форкна + libcmt вложена
gc_pal.lib, minipal.lib, ...            ← остальные форк-side libs
clrjit.lib, mscorrc.lib, ...
```

Kernel C# linkline (явные `<LinkerArg>` в OS.csproj) больше **не
упоминает libcmt** ни в каком виде. libcmt живёт строго как fork-
side toolchain dep для C++ runtime ('operator new'/'delete'/
'type_info'/scrt scaffolding/'_Init_thread_*'/etc).
