# Step 45 — Phase 1 step 2: Coff method lookup + funclet→ROOT backward walk

## Контекст

Второй step из 11-step Phase 1 try/catch roadmap. Цель — managed view над `.pdata` секцией нашего PE binary + binary search IP→method + backward walk от funclet record до его ROOT parent. Это foundation для последующих steps:
- Step 3 (EH info decoder) читает `unwindBlockFlags` trailer что отсюда.
- Step 4 (StackFrameIterator) использует FindMethodInfo для resolve current frame.
- Step 5 (DispatchEx) полагается на funclet→ROOT walk чтобы найти EH clause table parent'а funclet'а.

Smoke gate L5 = 7.

## Решение

### CoffRuntimeFunctionTable.cs — managed `.pdata` view

`OS/src/Boot/EH/CoffRuntimeFunctionTable.cs`:

1. **Image base detection**: scan downward от anchor pointer (тот же `EETypePtrOf<object>()` что использует `NativeAotModuleInit`) для `MZ` DOS signature, page-aligned strides, max 16MB radius. Validate через PE signature at `e_lfanew`.

2. **PE header parsing**: Optional Header → DataDirectory[3] (`IMAGE_DIRECTORY_ENTRY_EXCEPTION`) → RVA + size of RUNTIME_FUNCTION array.

3. **Static cache**: `s_imageBase`, `s_records` (`RuntimeFunction*`), `s_recordCount`. Idempotent `TryInitialize`.

`RuntimeFunction` struct — 12-byte AMD64 layout: `BeginAddress` (RVA) + `EndAddress` (RVA) + `UnwindInfoAddress` (RVA), `[StructLayout(Sequential)]` matches PE format exactly.

### CoffMethodLookup.cs — IP resolution + WalkToRoot

`OS/src/Boot/EH/CoffMethodLookup.cs`:

- **`FindRecordIndex(byte* ip)`** — binary search через sorted `.pdata` array. O(log N). Convert IP к RVA, classic three-way comparison `targetRva < BeginAddress`/`targetRva >= EndAddress`/in range.

- **`ReadUnwindBlockFlags(RuntimeFunction*)`** — stock NativeAOT trailer formula:
  ```
  unwindSize = 4 + 2 * CountOfUnwindCodes
  if (UNW_FLAG_EHANDLER | UNW_FLAG_UHANDLER) set:
      unwindSize = ALIGN_UP(unwindSize, 4) + 4
  trailerByte = unwindBlob[unwindSize]
  ```
  В нашем binary все 733 records `Unwind flags: None` (no Windows-personality), так что EHANDLER path никогда не fires. Implementation correctness preserved для forward compat.

- **`WalkToRoot(int startIndex)`** — backward scan max 64 steps до record с `UBF_FUNC_KIND_ROOT` (0x00). Stock `CoffNativeCodeManager.cpp:271-283` literally декрементирует `pRuntimeFunction--` пока kind не станет ROOT. ILC эмитит funclet records immediately после parent ROOT, sorted by IP, поэтому linear backward walk корректен.

- **`TryFindMethod(byte* ip, out MethodInfo info)`** — full resolution: binary search → если current record это funclet → walk to root. Возвращает обе записи (current + root) с их block flags. Для non-funclet IP они идентичны.

### `unwindBlockFlags` constants

```csharp
UBF_FUNC_KIND_MASK              = 0x03
UBF_FUNC_KIND_ROOT              = 0x00
UBF_FUNC_KIND_HANDLER           = 0x01
UBF_FUNC_KIND_FILTER            = 0x02
UBF_FUNC_HAS_EHINFO             = 0x04
UBF_FUNC_REVERSE_PINVOKE        = 0x08
UBF_FUNC_HAS_ASSOCIATED_DATA    = 0x10
```

`kind=3` который наш probe раньше показывал — артефакт неправильной формулы (см. step 44 каверy в roadmap). С stock formula `4 + 2*N` (без EHANDLER padding для no-personality records) parser работает чисто.

### Phase 2 wiring

`BootSequence.Phase2_Runtime` после `NativeAotModuleInit.TryInitialize`:

```csharp
if (!OS.Boot.EH.CoffRuntimeFunctionTable.TryInitialize((byte*)anchor))
    Log.Write(LogLevel.Warn, "coff method table init failed");
```

Использует тот же `EETypePtrOf<object>()` anchor.

### L5 probe

`EhProbe.RootWalk()` — bit-mask 7:
1. `TryFindMethod(&RootWalk)` — binary search для self-IP должен найти valid record.
2. Linear scan первых 200 records — найти первый с `kind != ROOT`.
3. `WalkToRoot(funcletIdx)` — должен вернуть index < funcletIdx (root preceeds funclets).

Diagnostic line печатается всегда: `count`, `selfIp`, `selfRecord` (root index), `firstFunclet`.

`Probes.EhRootWalk = true` toggle.

## Результат

```
[info] coff-pdata: imageBase=0x000000000E0E0000 pdataRva=0x00046000 records=733
[info]   l5-diag: count=733 selfIp=0x000000000E0F39D0 selfRecord=373 firstFunclet=152
[info] eh L5 .pdata + root walk: val=7
```

`imageBase=0x0E0E0000` подтверждает что UEFI замапил kernel binary at PE-specified VirtualAddress. 733 records (диапазон вырос с 698 на step 43 до 733 — добавились bodies для exception types step 44 и Coff lookup step 45). `selfRecord=373` — middle of `.pdata`, binary search OK. `firstFunclet=152` найден linear scan'ом, walk to root succeeded.

L4 (exception shape) и существующие probe'ы (L1, L2, NativeAotProbe, CctorProbe) остаются зелёные. ELF apps + launcher работают без regression.

## Файлы

### Новые

- `OS/src/Boot/EH/CoffRuntimeFunctionTable.cs` — image base detection + `.pdata` array view.
- `OS/src/Boot/EH/CoffMethodLookup.cs` — binary search + `WalkToRoot` + `TryFindMethod`.
- `done/step045.md` — этот файл.
- `done/step043.md`, `done/step044.md` — retroactive writeups для предыдущих 2 коммитов (был breach коммит-протокола CLAUDE.md, исправлено).

### Изменённые

- `OS/src/Boot/BootSequence.cs` — `CoffRuntimeFunctionTable.TryInitialize` в Phase 2.
- `OS/src/Kernel/Diagnostics/EhProbe.cs` — `RootWalk()` (L5 probe).
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhRootWalk` toggle.

## Что дальше

Phase 1 progress: 2/11. Step 46 = step 3 — EH trailer + `ehInfoRVA` decoder с varint EH clause table. Smoke L6=111.
