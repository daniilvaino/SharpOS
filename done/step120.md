# step 120 — libcmt.lib cut at fork side

## Цель

Перенести `libcmt.lib` (MSVC C/C++ runtime) из kernel-link cmdline'а
**внутрь форка** где она реально нужна (C++ код CoreCLR'а: `operator
new/delete`, `type_info`, scrt scaffolding). Kernel C# архитектурно
libcmt не использует — это deps стороны форка.

Эта итерация **commit'ает только fork-side** изменения; kernel-side
завершение перенесено в step 121 (требует CoffStub.Generator чтобы
закрыть последнюю транзит-зависимость на ручной `.c` файл).

## Fork-side changes (этот коммит)

### `dotnet-runtime-sharpos/eng/native/configurecompiler.cmake`

Добавлены под `CLR_CMAKE_TARGET_SHARPOS`:

- **`/GS-`** — отключить stack-cookie codegen в C++ форкна. Удалило
  большую часть `__security_cookie` references (но не все — некоторые
  обj'ы compile с force'ом GS regardless).
- **`/Zc:threadSafeInit-`** — отключить C++11 magic-static
  thread-safe init. Удалило ВСЕ `_Init_thread_*` references — наш
  boot single-threaded, thread-safe statics не нужны.
- **`/GUARD:NO`** — отключить CFG в линкере (комплементарно к
  существующему `CLR_CONTROL_FLOW_GUARD=OFF` который только compiler-
  side). Удалило `__guard_dispatch_icall_fptr` refs.

### `dotnet-runtime-sharpos/build_clr_sharpos.ps1`

После successful build добавлен **post-build merge step**:

```powershell
lib.exe /OUT:coreclr_static.lib coreclr_static.lib libcmt.lib
```

vswhere'ом находим `lib.exe` + `libcmt.lib` в актуальной MSVC tools
install. Merge пакует все libcmt'овы `.obj` в `coreclr_static.lib` →
форк-сторона становится **self-contained** для kernel link'а. Линкер на
kernel-стороне тащит libcmt'овы `.obj` on-demand как обычно, без
mention'а libcmt в kernel cmdline'е.

## Investigation summary (для будущего step'а)

Эксперимент с `/NODEFAULTLIB:libcmt` показал что libcmt тащит **12
уникальных symbols** (1819 LNK2001 references):

| Symbol | Тип | Откуда |
|---|---|---|
| `__security_cookie` | data | seccook.obj / GS |
| `_fltused` | data | auto-emit на FP usage |
| `_tls_index` | data | auto-emit на TLS usage |
| `__GSHandlerCheck` | func | GS-aware SEH |
| `__guard_dispatch_icall_fptr` | data | CFG indirect-call slot |
| `__dyn_tls_init`/`__dyn_tls_on_demand_init` | func | dynamic TLS |
| `__tls_guard` | data | TLS canary |
| `_Init_thread_*` (4 шт) | func+data | C++11 magic-statics |

CMake флаги закрыли `_Init_thread_*` + `__guard_dispatch_icall_fptr`
полностью; `__security_cookie` уменьшили с ~500+ до 326 refs.

**ILC ограничение** — `[RuntimeExport]` на static field НЕ
emit'ит native data symbol (только method-side работает). Это
исторический gap; раньше у нас были "ложные" `[RuntimeExport]`
определения `__security_cookie`/`_fltused`/`_tls_index` в
`CrtAndEhStubs.cs` которые **выглядели работающими**, но реально
символы тащились из libcmt через `/FORCE:MULTIPLE` collision.

## Kernel-side state (НЕ committed — будет в step 121)

Локально на машине разработчика работает следующее:
- `OS.csproj` — убран `<LinkerArg Include="libcmt.lib" />`, добавлен
  `/NODEFAULTLIB:libcmt.lib`
- `OS/kernel_crt_stubs.c` — однострочный hand-rolled `.c` определяющий
  `__security_cookie` data symbol для покрытия OS.obj's единственной
  ссылки (ILC gap)
- `CrtAndEhStubs.cs` — удалены "ложно-работающие" `[RuntimeExport]`-ы
  на static fields (`__security_cookie`, `_fltused`, `_tls_index`)
- `run_build.ps1` — `$env:VSLANG = "1033"` для англоязычных linker
  диагностик
- `tools/verify_kernel_externals.ps1` — gate-скрипт через `dumpbin
  /symbols OS.obj` со whitelist'ом ожидаемых UNDEF references

Эти изменения **не committed** потому что `OS/kernel_crt_stubs.c` —
**транзитный .c файл в дереве** который нарушает Инвариант 1 (C# is
the only source language). Закроем в step 121 через **CoffStub.Generator**
по аналогии с BootAsm.Generator: атрибут на C# static field, generator
материализует `.obj` на build-time:

```csharp
[CoffDataSymbol("__security_cookie", Section = ".data", Alignment = 8)]
public static ulong SecurityCookie = 0x2B992DDFA232UL;
```

Reference для генератора — `dotnet-runtime-sharpos/src/coreclr/tools/aot/
ILCompiler.Compiler/Compiler/ObjectWriter/CoffObjectWriter.cs` (читаем
COFF format mapping, deps на ILCompiler.DependencyAnalysis НЕ тащим —
для нашего use-case data-only-symbol достаточно ~250 строк
purpose-built emitter'а).

## Verified state

- Census (post-merge): **OK=113 DEG=2 FAIL=8** — без регрессий vs step 119
- EBS + Launcher 4/4 + Phase E threading + drivers + CoreCLR — все green
- `verify_kernel_externals.ps1` — 9 UNDEF refs, all whitelisted
  (`coreclr_initialize`, `coreclr_execute_assembly`, `__security_cookie`
  + 6 `SharpOSHost_*` diag backdoors)

## Файлы (этот коммит)

```
dotnet-runtime-sharpos/eng/native/configurecompiler.cmake
  + /GS- /Zc:threadSafeInit- /GUARD:NO под CLR_CMAKE_TARGET_SHARPOS
dotnet-runtime-sharpos/build_clr_sharpos.ps1
  + post-build merge libcmt.lib → coreclr_static.lib
done/step120.md
```

## Next step

**step 121 — CoffStub.Generator + finish libcmt cut в kernel.csproj**:
1. Изучить CoffObjectWriter.cs форка как референс для COFF binary
   layout (без import'а зависимостей)
2. Написать `bootasm/CoffStub.Generator/` (~250 строк) — IIncrementalGenerator
   который читает `[CoffDataSymbol]` атрибут на static field, материализует
   per-class `.obj` в `IntermediateOutputPath/native/`
3. Перенести `__security_cookie` определение обратно в `CrtAndEhStubs.cs`
   как managed C# с новым атрибутом, удалить `OS/kernel_crt_stubs.c`
4. Закоммитить `OS.csproj` + `CrtAndEhStubs.cs` + `run_build.ps1` +
   `verify_kernel_externals.ps1` + generator
