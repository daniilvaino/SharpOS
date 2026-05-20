# Step 81 — Phase D landed: FrameChain walker integration closes §11

**Status:** § 11 (единственный uncatchable EH-фронтир по
`coreclr-hosted-limits.md`) закрыт. `Socket` / OpenSSL `RNG` /
`SHA256` и прочие P/Invoke-trap'ы теперь ловятся как catchable
`SEHException`. Census полностью прошёл (`OK=20 DEG=2 FAIL=22`,
ноль HALT'ов). Тред-cohort (`new Thread().Start()`, `ThreadPool`,
`Task.Run`, `Timer`, `Thread.Sleep`) остаётся гарантированным
HALT — это **другой** класс (direct `SharpOSHost_Panic`,
не C-SEH), не EH-баг и не §11. Phase E (threading-PAL) — отдельный
фронт.

Сделано за один день вместо ожидаемых 1-2 недель — благодаря
точной диагностике в step 89 и тому, что Frame layout в форке
уже без vtable (ID-based dispatch, не нужно портировать C++
vtable runtime).

## Три итерации до landing'а

**iter 1.** Добавил debug-print в начало `CallDescrWorkerUnwind-
FrameChainHandler` (форк): `pThread`, `m_pFrame`, `pestab`, `flags`,
`rip`. Подтвердило: handler действительно invoked при walk через
kill frame, `GetThread()` возвращает singleton, `m_pFrame`
non-null (CoreCLR стаб-макросы maintain'ят FrameChain).

**iter 2.** Расширил print до полей `InlinedCallFrame`
(`frameId`, `m_Next`, `m_pCallSiteSP`, `m_pCallerReturnAddress`,
`m_pCalleeSavedFP`). Значения sanity-passed: CallerRA — code
address в R2R-загруженной DLL, CallSiteSP/CalleeSavedFP — валидные
стек-адреса.

**iter 3.** Фактический walker fix:
- Форк: два EXTERN_C helper'а `SharpOSHost_GetCurrentFrame()` /
  `SharpOSHost_SetCurrentFrame(void*)`.
- Kernel C# `SehDispatch.TryActivateFrameChain(Context*)`:
  читает `Thread::m_pFrame`, для активного `InlinedCallFrame`
  (frameId=1, `m_pCallerReturnAddress != 0`) overrid'ит
  `ctx->{Rip, Rsp, Rbp}`.
- Hook'и в обоих walker'ах — `DispatchException` (search) и
  `RtlUnwind` (unwind). На месте `IsValidIp(controlPc) == false`,
  до bail'а, пробуем FrameChain skip.

**Iteration 3.1** — небольшая доработка после первого прогона:
убрал pop из TryActivateFrameChain (chain нужен для unwind pass'а,
там personality routine делает pop через `CleanUpForSecondPass`),
добавил `CallSiteSP > ctx->Rsp` guard для анти-реактивации, убрал
ошибочный `isThrowSite = true` (CallerRA — post-call IP, нужен
`Rip - 1` для finding EH-clause).

## Frame layout в форке — без vtable

```cpp
// vm/frames.h:538-542
FrameIdentifier _frameIdentifier;   // +0,  1 = InlinedCallFrame
PTR_Frame       m_Next;              // +8,  ~0 = FRAME_TOP
// InlinedCallFrame-specific:
PTR_PInvokeMethodDesc m_Datum;       // +16
PTR_VOID              m_pCallSiteSP; // +24, caller's RSP
TADDR                 m_pCallerReturnAddress; // +32, caller's RIP (managed JIT)
TADDR                 m_pCalleeSavedFP;       // +40, caller's RBP
PTR_VOID              m_pThread;     // +48
```

ID-based dispatch (`FrameIdentifier` enum в `FrameTypes.h`) вместо
C++ virtual methods. Намного дружественнее к kernel-side порту —
не нужно ничего знать о vtable lookup. Switch по `frameId`.

## Верификация на v3 репро (Socket-in-try/catch)

Лог (Verbose=true, kernel only):
```
[step89] sec11 v3 probe -- new Socket(...) inside try/catch
[stubhdr CDW] ... m_pFrame=0x277E220 flags=0x1 (search-pass) ...
              ICF{CallSiteSP=0x277E1F0 CallerRA=0x50000856D52F CalleeSavedFP=0x277E2B0}
[fchain] activate ICF: CallerRA=0x50000856D52F ...                ← search pass skip
[stubhdr CDW] ... flags=0x3 (unwind-pass) ...                     ← second pass entered
[fchain] activate ICF: CallerRA=0x50000856D52F ...                ← RtlUnwind same skip
[step89] CAUGHT: SEHException msg=External component has thrown an exception.
[step89] after try/catch -- if you see this, sec11 path is reachable
================ LAUNCHER ================                        ← NormalHello completed cleanly
```

## Полная census-регрессия (Verbose=false, Trace=false)

| | step 73 baseline | step 90 (this) | delta |
|---|---|---|---|
| OK | 19 | 20 | +1 |
| DEG | 2 | 2 | 0 |
| FAIL | 20 | 22 | +2 |
| HALT | 1 (Socket) | 0 | -1 ✓ |

3 пробы `Socket` / `RNG` / `SHA256` re-enabled (были `Skip` как
HARD-PANIC §11). Все три FAIL как catchable `SEHException`
(`[FAIL] PAL-STUB/SEH: External component has thrown an exception.`).
Threading cohort (5 проб) остался `Skip` (не §11, Phase E).
`+1 OK` дельта — interim baseline-shift с step 73 (один из FAIL
стал OK благодаря Phase B/C interim fix'у), не Phase D-эффект.

## Файлы

**Main repo:**
- [`OS/src/PAL/SharpOSHost/SehDispatch.cs`](../OS/src/PAL/SharpOSHost/SehDispatch.cs) —
  `TryActivateFrameChain` + два DllImport'а
  (`SharpOSHost_GetCurrentFrame`/`SetCurrentFrame`) + hook'и
  на `IsValidIp` fail в search-pass `DispatchException` и
  unwind-pass `RtlUnwind`. step-89 диагностический scaffolding
  (`Trace=false`) оставлен.
- [`docs/coreclr-hosted-limits.md`](../docs/coreclr-hosted-limits.md) §11
  — раздел переписан с CLOSED-статусом и описанием реализации.
- [`docs/eh-model.md`](../docs/eh-model.md) §C
  — обновлён под закрытие §11; threading-cohort явно выведен в
  отдельный класс (Phase E).

**Fork (`dotnet-runtime-sharpos`, отдельный repo):**
- `src/coreclr/vm/exceptionhandling.cpp` — два EXTERN_C helper'а
  (`SharpOSHost_GetCurrentFrame`/`SetCurrentFrame`) и diagnostic
  print в `CallDescrWorkerUnwindFrameChainHandler` (gated
  `TARGET_SHARPOS && !DACCESS_COMPILE`, не зависит от Verbose
  через kernel C# side — печатает через weak SharpOSHost_DebugPrint).

## Deferred

- **Phase C4** — flip `ExitBootServicesExperiment` to default boot,
  retire UEFI launcher path.
- **Phase E** — threading-PAL (real threads + scheduler +
  SwitchToThread waits + timers). Threading cohort census-проб
  останется HALT до этого.
- Расширение `TryActivateFrameChain` под другие Frame-типы
  (`HelperMethodFrame`, `UMThunkUnwindFrameChainHandler`-кадры,
  и т.д.) — когда встретятся в census или конкретном scenario.
  Сейчас нет потребителя.
- Memory `project_phase_d_framechain_walker.md` обновить — Phase D
  выполнен; «1-2 недели» оценка оказалась один-день благодаря
  точности step-89 диагностики и удобному no-vtable layout'у Frame'а.
