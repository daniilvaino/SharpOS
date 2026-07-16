# step 138 — общий app-SDK + тест-батарея app-рантайма (fetch на PE, граница вскрыта)

## Контекст

После step137 (первый freestanding PE-лаунчер на bare metal) рецепт сборки жил
целиком в `HelloSharpFs.csproj`. Задача: (1) вынести его в общий `.props`, чтобы
новые PE-аппы были тонкими; (2) мигрировать `FetchApp` на PE (первый non-launcher
PE-апп); (3) добавить батарею тестов app-рантайма и прогнать — увидеть, что аппы
реально умеют. Это чекпоинт **перед** большим фронтом «app-рантайм шарит рантайм
ядра» (interface dispatch + исключения через kernel export table + PE-imports).

## Результат

Все три аппа собрались freestanding win-x64 PE через общий SDK. FetchApp работает.
Батарея прогналась и **вскрыла границу app-tier рантайма: 11/14** — три FAIL все про
value-type interface dispatch (`EqualityComparer<int>.Equals` и `List.Contains`/
`Dictionary.TryGetValue` через него). Это не баг батареи — это её работа: она
задокументировала, чего аппам не хватает, и стала регрессионным gate'ом для
следующего фронта.

## Общий SDK: `apps_native/sdk/FreestandingPe.props`

Весь freestanding-рецепт (PropertyGroup + base sdk/std Compile-list + win-x64
LinkerArgs + CoffStub.Generator import + `ExcludeNativeAotRuntime`/`CustomizeReferences`/
`RemoveHostConfigurationOptions` targets) вынесен в один `.props`, импортится
каждым app-csproj. Апп несёт только `IlcSystemModule` (per-app corelib name) +
свои исходники. `HelloSharpFs.csproj`/`FetchApp.csproj`/`AotTests.csproj` теперь
крошечные (5-10 строк).

`WinCrtStubs.cs` переехал `HelloSharpFs/` → `sdk/` (namespace `SharpOS.AppSdk`) —
общий для всех аппов.

**Ловушка (`OutputType`):** props ставил `<OutputType Condition="'$(OutputType)'==''">Exe`,
но `Sdk.props` уже дефолтит `OutputType=Library` **до** mid-body import → условие
false → апп собирался как `.dll` → NativeAOT линковал `bootstrapperdll.obj` (DLL-
вариант бутстрапа) с unresolved `RhInitialize`/`InitializeModules`/…, а
`ExcludeNativeAotRuntime` снимает `bootstrapper.obj` (EXE-вариант). Фикс —
`<OutputType>Exe</OutputType>` **безусловно**.

## FetchApp → PE

Мигрирован тем же тонким паттерном (`IlcSystemModule=FetchApp`, только `Program.cs`
+ import props). Первый non-launcher PE-апп. Собирается `build_fetch.ps1`.

## Тест-батарея: `apps_native/AotTests/`

14 проверок app-рантайма, печатает `ok`/`FAIL` на каждую, exit = pass-count.
Value-type статики `s_pass`/`s_total` (не cctor-trap). Покрывает: `new object()`,
`new int[5]`+index, `new string(char[])`, string concat/eq/PadRight, `List<int>`
add/count/indexer/Contains/ToArray, `Dictionary` add/count/TryGetValue/missing-key,
`EqualityComparer<int>.Default`.

**Прогон: 11/14.** FAIL: `List<int>.Contains`, `Dictionary.TryGetValue`,
`EqualityComparer<int>`.

## Что вскрыла батарея — граница app-рантайма

Freestanding PE-апп несёт **минимальный** рантайм (GC + прямые вызовы). Он **НЕ**
имеет:

- **interface dispatch** — машинерия `OS/src/Kernel/Memory/InterfaceDispatch*.cs`
  (Bridge-шеллкод + resolver + `RhpInterfaceDispatch`) kernel-only. `DefaultComparer<T>.Equals`
  делает `x is IEquatable<T>` + `eq.Equals(y)` = interface isinst + dispatch → в
  аппе некому резолвить.
- **исключения** — нет типа `System.Exception`, ThrowHelpers = `while(true)` halt,
  EH-машинерия (`OS/src/Boot/EH/*`) kernel-only → **Tier-B halt-on-throw**.

Всё, что идёт через `IEquatable<T>`-dispatch (`Contains`/`TryGetValue`/`EqualityComparer`),
падает; прямые операции (`List` add/index/count/ToArray, `Dictionary` add/count) — ok.

## Следующий фронт (разблокируется одним ходом)

Апп исполняется **внутри адресного пространства ядра** → должен **шарить**
kernel-рантайм, а не переизобретать. Механизм:

1. **Kernel export table** — ядро экспортит `RhpInterfaceDispatch`/resolver/
   `RhpThrowEx`/personality.
2. **App PE-imports** — апп импортирует их (перестаёт нести свой минимальный слой).
3. **`PeLoader.TryResolve`** (M3, уже написан в step136) биндит импорты к export
   table.
4. **`.pdata` registration** — PeLoader регистрирует .pdata аппа в kernel function-
   table (per-image EH).
5. **Cross-image MethodTable identity** — чтобы `catch (FooException)` матчился.

Тогда interface dispatch **и** исключения включаются вместе. Три FAIL'а батареи =
регрессионный gate: перевернутся в `ok`, когда фронт закрыт. Начинаем с interface
dispatch (меньше EH, сразу видно в батарее), EH — следующим.

## Build-скрипты (PE-only)

- `build_launcher_win.ps1` → `build_launcher.ps1` (generic, `-AppProject` default
  HelloSharpFs).
- Новые тонкие обёртки `build_fetch.ps1` / `build_aottests.ps1` (зовут
  `build_launcher.ps1 -AppProject …`).
- WSL/ELF-скрипты удалены (`build_launcher_wsl.ps1`, `build_fetch_wsl.ps1`).
- `run_build.ps1` стейджит `$peApps` (HELLO/FETCH/AOTTESTS.EXE) + cleanup stale ELF.

## Файлы

- `apps_native/sdk/FreestandingPe.props` (new) — общий рецепт.
- `apps_native/sdk/WinCrtStubs.cs` (moved from HelloSharpFs/).
- `apps_native/{HelloSharpFs,FetchApp}/*.csproj` — тонкие (import props).
- `apps_native/AotTests/{AotTests.csproj,Program.cs}` (new) — батарея.
- `build_launcher.ps1` (renamed), `build_fetch.ps1`/`build_aottests.ps1` (new),
  `build_launcher_wsl.ps1`/`build_fetch_wsl.ps1` (deleted).
- `run_build.ps1`, `OS/src/Kernel/Elf/ElfAppContract.cs`/`ElfValidation.cs`.
- `docs/nativeaot-nostd-kernel-limits.md` (§8 PE-app tier), `README.md` (инвариант-1).

## Откладываем

- Порт interface dispatch + EH в аппы (следующий фронт, см. выше).
- Stage B: удаление kernel ELF-кода (`ElfLoader`/`ElfParser`/`ElfTypes`/… + ELF-
  ветка `AppServiceBuilder`) — сейчас живут unused.
- Апгрейд лаунчера на новый std (byte*/stackalloc → List/string/LINQ).
- PeLoader: per-section NX/RO, off-base relocations.
