# Sage 2 — refinement запрос с empirical данными

## Контекст

Phase 1 last item: managed try/catch/finally в SharpOS (NativeAOT 7.x + NoStdLib + UEFI win-x64). Принято:

- **Option A** (full unwinder), не B/C/D.
- Sequencing managed-dispatcher-first (твой order на 11 шагов).
- **Главное**: нацелены на ПОЛНУЮ реализацию. Phase 1 закрывается **только после step 11** (collided unwind + filter + fault + HW-bridge + rich stack trace + полный 88-byte Exception layout + все 6 missing derived types). НЕ останавливаемся на 1.5b. НЕ оставляем 1.5c "на потом".
- XMM6-XMM15 spill — оставляем по stock NativeAOT, не оптимизируем.

Прогнали два probe'а на текущем kernel binary (`OS.exe`, win-x64 EFI PE) чтобы конкретизировать твой план:

## Empirical findings

### probe_unwind_codes.ps1 — opcode coverage scan

698 RUNTIME_FUNCTION records, ВСЕ 710 records с `Unwind flags: None` (нет EHANDLER/UHANDLER/CHAININFO). Это подтверждает что NativeAOT не использует Windows-personality. Чистый managed dispatcher.

**Opcode histogram across all records:**

| Opcode | Count | Notes |
|---|---|---|
| `PUSH_NONVOL` | 1468 | rbx/rbp/rsi/rdi/r12-r15 |
| `ALLOC_SMALL` | 477 | sub rsp, ≤128 |
| `ALLOC_LARGE` | 32 | sub rsp, >128. Examples up to 0x2D0 (`Boot.Entry`) |
| `SET_FPREG` | 8 | mov rbp, rsp+offset. Always rbp, offsets 0x20/0x30/0x40 |
| `SAVE_NONVOL` | 0 | none |
| `SAVE_NONVOL_FAR` | 0 | none |
| `SAVE_XMM128` | 0 | **none** — confirms no SSE/FP in our code |
| `SAVE_XMM128_FAR` | 0 | none |
| `PUSH_MACHFRAME` | 0 | none |
| `EPILOG` (extended) | 0 | none |

**ALLOC_LARGE methods** (top examples):
- `AppServiceBuilder.RunExternalApp` — 0x248 bytes
- `Boot.Entry` — 0x2D0 bytes
- `UefiBootInfoBuilder.Build` — 0x138 bytes
- `Rtc.TryRead` — 0x118 bytes
- ~28 others, all 0x88..0x248 bytes.

**SET_FPREG methods** (8 total): все используют rbp. Offsets 0x20/0x30/0x40 (typical funclet-parent frame pointer convention). Methods: `AppServiceBuilder.FileExists/ReadFile/ReadDirEntry/RunApp/TryReadAbiManifest`, `FileDiagnostics.DumpDirectory`, `FileSystem.List`, `Console.WriteULongRaw`.

**Code count histogram**: spread 0..15+, peak at count=2 (110 records), count=4 (75 records), count=6 (43 records).

### probe_eh_trailer.ps1 — empirical NativeAOT trailer reader

Прочитал PE binary напрямую, walked `.pdata` → unwind info → trailer.

**Sections**:
```
.text     VA=0x00001000 VSize=0x000114
.unbox    VA=0x00002000 VSize=0x000111
.managed  VA=0x00003000 VSize=0x01CBBD   <-- большой managed code blob
.rdata    VA=0x00020000 VSize=0x0100CD
.data     VA=0x00031000 VSize=0x013B10
.pdata    VA=0x00045000 VSize=0x002148
.modules  VA=0x00048000 VSize=0x000008   <-- TypeManagerIndirection?
.rsrc     VA=0x00049000 VSize=0x0004C8
.reloc    VA=0x0004A000 VSize=0x000B68
```

**710 records** parsed (slight diff with dumpbin's 698 — наша probe считает все включая некоторые edge entries).

**`unwindBlockFlags` kind histogram**:
- ROOT: 574
- HANDLER: 61
- FILTER: 38
- kind=3: 37 (undocumented, см. caveat ниже)

**Trailer flag totals**:
- HAS_EHINFO: 83 records
- HAS_ASSOCIATED_DATA: 34 records
- REVERSE_PINVOKE: 66 records

**ehInfoRVA → section histogram**:
- `.rdata`: 5 (clean parse, e.g. rec[167] `ehRVA=0x02ACFF` → .rdata)
- `<not found>`: 78 (broken parse — RVAs out of binary range)

### Caveat: trailer parsing off-by-2

Наша probe формула `stdSize = 4 + roundUpDword(2 * countOfCodes)` расходится со stock NativeAOT `sizeof(UNWIND_INFO) + sizeof(UNWIND_CODE) * (count & ~1)` для нечётного N. Stock считает `UnwindCode[1]` inline в struct (sizeof = 6 bytes), наша probe — separately. На odd-count records наш offset съезжает на 2 байта → garbage trailer.

Из 83 HAS_EHINFO records только 5 распарсились чисто (даже-count, видимо). 78 показали `ehRVA out of binary range`. **Это parser bug, не runtime issue** — реальный stock decoder работает по правильной формуле. Но для нашего production EH varint decoder (step 3) важно правильно implementire formula с самого начала.

37 records с kind=3 — скорее всего тоже артефакт того же off-by-2: для некоторых records мы читаем GCInfo varint вместо unwindBlockFlags, и random byte имеет low 2 bits = 11.

## Что меняется в твоём 11-step плане

### Step 4 (StackFrameIterator + unwind decoder MVP)

**Минимальный decoder = 4 opcode'а, не 2.** Должен поддерживать:
- `PUSH_NONVOL` — recover saved nonvol register from stack offset
- `ALLOC_SMALL` — adjust SP by ≤128 bytes
- `ALLOC_LARGE` — adjust SP by >128 bytes (variant flag bit determines whether SIZE field is 16-bit or 32-bit)
- `SET_FPREG` — mov rbp, rsp+offset (FrameRegister/FrameOffset из UNWIND_INFO header)

`PUSH_MACHFRAME` / `SAVE_NONVOL` / `SAVE_XMM128` — log + hard-fail если встретятся (currently 0 occurrences). Грейс fail, не silent skip.

### Step 3 (EH trailer + ehInfoRVA decoder)

Использовать stock NativeAOT formula `sizeof(UNWIND_INFO) + sizeof(UNWIND_CODE) * (count & ~1)` для location of trailer. НЕ нашу naive formula. Иначе 78/83 EH records не парсятся.

### Step 8 (Filter clauses) — реально нужны

Не odd case, а **38 filter funclets в нашем существующем коде уже**. ILC эмитит их, видимо, в каких-то iterator/async lowering paths или для `when` clauses из BCL portов. Мы в managed C# explicit `when` не пишем, так что эти 38 — implicit. Filter handling не "rare nice-to-have", а realistic requirement сразу.

### Step 4 + Step 7 (StackFrameIterator + finally) — funclet→parent walk

61 HANDLER funclet + 38 FILTER funclet → backward walk ROOT lookup нужен на step 4 уже (StackFrameIterator должен уметь сказать "это funclet, parent — record N steps back"). Stock `CoffNativeCodeManager.cpp:271-283` walks pRuntimeFunction-- до UBF_FUNC_KIND_ROOT.

### XMM6-XMM15 spill — можем дропнуть

0 records с `SAVE_XMM128`. Confirmed: managed code не использует SSE/FP. Если ABI fork делаем, XMM spill (160 байт на throw) — pure waste. Решение оставляем за тобой — рекомендуешь оставлять spill стандартным или вырезать?

## Запросы

1. **Refined plan: дай 11-step roadmap with these empirical adjustments.**
   - Step 3: stock UNWIND_INFO size formula explicitly.
   - Step 4: 4-opcode decoder (PUSH_NONVOL/ALLOC_SMALL/ALLOC_LARGE/SET_FPREG) + funclet→parent backward-walk.
   - Step 8: filter handling может быть критичным для совместимости с ILC's implicit filter funclets — не обязательно ждать "user explicit `when`" use case.
   - Все остальные шаги — зафиксированы или измени если empirical data что-то меняет.

2. **Detailed step 5 breakdown** (ты сам предложил):
   - Какие struct'ы нужны (PAL_LIMITED_CONTEXT, REGDISPLAY, ExInfo, RhEHClause). Layouts с offset'ами.
   - Какие fields в ExInfo читаются/пишутся managed dispatcher'ом vs asm thunk'ами.
   - Аргументы byte-array shellcode emitter'а для RhpThrowEx и RhpCallCatchFunclet (что в RCX/RDX/R8/R9 на entry, что в RAX на возврате, какие nonvols spill'ить).
   - Exact smoke sequence: какие промежуточные log'и должны появиться от `throw new InvalidOperationException("eh8")` до `catch.Body returns 801` — checkpoint список чтобы понять где сломалось при first failure.

3. **Decision: XMM6-XMM15 spill.**
   - Empirically 0 SAVE_XMM128 codes в нашем коде. Можем дропнуть spill (ABI fork) или оставить stock-compatible. Что советуешь и почему.

4. **kind=3 records — что это.**
   - 37 records с unwindBlockFlags low 2 bits = 0b11. NativeAOT spec знает только ROOT/HANDLER/FILTER (0/1/2). Это parser bug у нас (off-by-2), или реально existing kind=3 в каком-то контексте?

5. **Trailer formula source-of-truth.**
   - Stock CoffNativeCodeManager.cpp formula `sizeof(UNWIND_INFO) + sizeof(UNWIND_CODE) * (count & ~1)`. Это `(count & ~1)` rounds DOWN, что выглядит counter-intuitive для layout с padding-to-dword. Можешь объяснить почему именно эта формула — что в компоновке struct делает её корректной?

6. **Estimate refinement.**
   - С учётом 4 opcode'ов в step 4 (вместо 2) и filter handling в step 8 как realistic requirement, изменился ли твой estimate 3-5.5 мес или укладываемся?
