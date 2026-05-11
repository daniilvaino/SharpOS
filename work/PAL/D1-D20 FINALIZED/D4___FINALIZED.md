## D4 — FINALIZED

### Решение

**Cross-runtime exception handling — environment-specific (per revised D10/D11 provider model)**:

- **Managed provider context** (kernel-tier C# в Phase 6 if applicable): manual try/catch wrapper в каждом `[UnmanagedCallersOnly]` экспорте. Catch только specific known exceptions (mapping в SystemError codes). Unknown exceptions намеренно не catch'аются — NativeAOT FailFast как safety net.

- **Native provider context** (C/C++ Windows shim в Phase 2, possible C/C++ glue в Phase 6): direct return SHARPOS_E* status codes на failure. C++ exceptions через extern "C" boundary запрещены (per D11 C ABI rules). Если внутри implementation возможен C++ exception (например через STL) — локальный catch до возврата. Hard prohibition enforced через discipline + compiler flags, не через runtime guarantee.

Общий принцип одинаковый: catch только known errors, never propagate exceptions через C ABI boundary, fail loud для unexpected.

### Verified factual basis

**Scope**: applicable when provider includes managed code (Phase 6 kernel-tier C# context). Для native provider context (Phase 2 Windows shim, C/C++ glue) NativeAOT runtime не задействован — applies C++ ABI rules (C++ exceptions через extern "C" = undefined behavior, prevent через discipline).

Подтверждено через раскопки в `dotnet/runtime release/10.0`:

**Source: `src/coreclr/nativeaot/Runtime.Base/src/System/Runtime/ExceptionHandling.cs`**

Когда managed exception escape'ит через `[UnmanagedCallersOnly]` границу:

1. Line 767-772: EH walker iterates frames, при reverse P/Invoke transition `unwoundReversePInvoke = true`, loop break'ит — **никакого walking через native frames не происходит**
2. Line 804-829: на non-Apple target (нет FEATURE_OBJCMARSHAL) `pReversePInvokePropagationCallback` остаётся `IntPtr.Zero`
3. Line 831-844: `pCatchHandler == null && pReversePInvokePropagationCallback == IntPtr.Zero` → **`UnhandledExceptionFailFastViaClasslib(RhFailFastReason.UnhandledException, ...)` гарантированно**

Stack overflow обрабатывается отдельным path (`EHHelpers.cpp:373`): `PalPrintFatalError + RhFailFast`. Тоже kill, не cross C-ABI.

**Conclusion: undefined behavior через C-ABI границу физически невозможен на NativeAOT Linux/SharpOS target.** Худший случай — fatal error с известным symptom.

### Что это означает для D4

NativeAOT runtime обеспечивает **safety net автоматически**. Try/catch нужен **не** для correctness (она и так есть), а для **resilience** — чтобы host bug не убивал kernel целиком.

Это меняет философию pattern'а:

- Старый thinking: "catch всё, иначе undefined behavior"
- Реальный thinking: "catch только то что знаем как обработать, остальное — пусть kernel умирает громко"

### Pattern — managed provider context

Применимо когда provider это kernel-tier C# code с `[UnmanagedCallersOnly]` exports (Phase 6 if applicable).

```csharp
[UnmanagedCallersOnly(EntryPoint = "SharpOSHost_AllocPages")]
public static IntPtr AllocPages(nuint size, uint flags) {
    try {
        // === actual logic ===
        return KernelHeap.AllocateAligned(size, flags);
        // ====================
    }
    catch (OutOfMemoryException) {
        // Recoverable — host failed to allocate, PAL может попробовать что-то другое
        ThreadLocalState.LastError = SystemError.ENoMem;
        return IntPtr.Zero;
    }
    catch (ArgumentException) {
        // Bad arguments from PAL — bug в pal/sharpos/, но recoverable
        ThreadLocalState.LastError = SystemError.EInval;
        return IntPtr.Zero;
    }
    // НИКАКОГО catch (Exception) — unexpected exceptions делают RhFailFast
    // Это нарочно — unknown exception в host = fundamental bug, лучше kill чем продолжать
}
```

**Три уровня catch (намеренно ограниченные)**:

1. **Specific known exceptions** → mapping в specific SystemError code (через D1)
2. **Other expected exception types** (если есть для конкретной функции)
3. **НЕТ catch-all** — let NativeAOT runtime делать FailFast

### Pattern — native provider context

Применимо когда provider это C/C++ код (Phase 2 Windows shim, possible Phase 6 C/C++ glue).

```cpp
// sharpos_host_windows_shim/memory.cpp
extern "C" SharpOS_SystemError SharpOSHost_AllocPages(
    void* requestedAddress,
    size_t size,
    uint32_t flags,
    void** result)
{
    void* p = ::VirtualAlloc(
        requestedAddress, size,
        MEM_RESERVE | MEM_COMMIT,
        PAGE_READWRITE);
    
    if (p == nullptr) {
        DWORD lastError = ::GetLastError();
        *result = nullptr;
        return TranslateWin32ErrorToSharpOS(lastError);  // direct return, no exception
    }
    
    *result = p;
    return SHARPOS_SUCCESS;
}
```

**Правила native provider**:

1. **Direct return SHARPOS_E* codes** на failure, no exceptions для error reporting
2. **C++ exceptions через extern "C" boundary запрещены** (per D11 C ABI rules: "no exceptions across boundary")
3. **Локальный catch если STL может throw** — exception захватывается внутри implementation до возврата
4. **Compiler flag policy**: либо `/EHsc` (default MSVC) с локальными catch'ами, либо `/EHs-c-` для shim files что не используют exceptions
5. **Hard prohibition enforced через discipline + compiler flags**, не через runtime guarantee (нет NativeAOT FailFast в native context)

### Почему НЕ catch-all (Exception)

Старый instinct — добавить `catch (Exception ex) { Log; return generic_error; }` как safety net. Это **антипаттерн** в нашем случае:

- Catch-all маскирует bugs — exception caught, generic error returned, pal/sharpos/ продолжает не зная что что-то фундаментально сломалось
- Bug может остаться скрытым месяцами, проявиться позже как симптом который никак не связан с реальной причиной
- NativeAOT runtime уже даёт нам RhFailFast = громкий crash с stack trace
- Громкий crash легче debug'ить чем silent corruption + delayed symptom

**Принцип**: catch только то что знаем **как именно** обработать. Если непонятно как — let it crash.

### Variants which were rejected

|Variant|Почему отклонён|
|---|---|
|**B. Allow exceptions cross C-ABI**|NativeAOT runtime не пускает (FailFast гарантированный). Но kernel kill на любой host bug — слишком хрупко. Try/catch нужен для resilience.|
|**A2. Source generator auto-wrap**|Premature complexity. 30-50 функций manual try/catch — не настолько много чтобы оправдать source generator infrastructure. Magic усложняет debugging.|
|**A3. Helper method с delegate**|Lambda allocation на каждый call (potential hot path concern). Less clean чем direct try/catch.|
|**A4. Convention-based с linter**|Custom analyzer = additional infrastructure. Можно начать без, добавить если забывание try/catch станет реальной проблемой.|

### Что украдено

|Артефакт|Источник|Используется для|
|---|---|---|
|FailFast гарантия|NativeAOT runtime EH machinery (`ExceptionHandling.cs::DispatchException`)|Safety net в managed provider context — не нужно нам обеспечивать correctness|
|Specific exception → error code mapping pattern|`dotnet/runtime/Interop.IOErrors.cs::GetExceptionForIoErrno`|Как переводить exceptions в SystemError codes (managed provider)|
|`[UnmanagedCallersOnly]` attribute usage|NativeAOT documentation|Сама механика export'ов (managed provider)|
|C++ ABI rules (no exceptions across extern "C")|C++ standard|Native provider boundary discipline|

### Что своё

- Pattern «catch specific + let RhFailFast handle unknown» — наш pragmatic выбор для resilience+honesty (managed provider context)
- Pattern «direct SHARPOS_E* return + локальный catch для STL» — discipline для native provider context
- LogUnexpectedHostFault (если будет) — наша diagnostic infrastructure
- Compiler flag policy для shim files — наш choice для enforcement

### Связь с другими decisions

- **D1**: Status codes used by both contexts. Managed provider catches → SystemError. Native provider returns SHARPOS_E* directly. Translation logic ports'ит из `Interop.IOErrors.cs::GetExceptionForIoErrno`.
- **D2**: `ThreadLocalState.LastError` (managed) и `g_palThreadState.lastError` (native) оба используют thread-local storage из D2 (через PAL_THREAD_LOCAL macro / Phase 5.5 native TLS infrastructure).
- **D10/D11 revised**: provider environment-specific (Windows shim C++ для Phase 2, kernel symbols/glue/managed exports для Phase 6). D4 patterns применяются по contextу.
- **D11 C ABI rules**: "no exceptions across boundary" — enforced через managed FailFast в managed context, через discipline + compiler flags в native context.
- **D14**: Hard prohibition exceptions через C-ABI. В managed context enforced runtime'ом автоматически (NativeAOT не пускает). В native context enforced discipline (C++ ABI rules).
- **D3**: Если catch (specific exception) → SystemError mapping использует scaffolding logging для diagnostic (managed context). Native provider может использовать тот же tracing для return value logging.

### Implementation guidelines

**Per-export checklist для managed provider** (когда пишешь C# `[UnmanagedCallersOnly]` export):

1. ✅ Identify какие specific exceptions могут быть thrown в managed logic
2. ✅ Для каждой — какой SystemError code соответствует (через D1 mapping)
3. ✅ Какой "error return value" возвращается на C-ABI side (IntPtr.Zero, false, -1, etc.)
4. ✅ Wrap logic в try, добавь catch для каждой identified exception
5. ❌ НЕ добавлять catch (Exception) — нарочно
6. ✅ Test что на expected exception → правильный error code; на unexpected — FailFast (убедиться что safety net работает)

**Per-export checklist для native provider** (когда пишешь C/C++ shim function):

1. ✅ Identify failure modes (system call errors, allocation failures, invalid args)
2. ✅ Для каждой — какой SHARPOS_E* code возвращается
3. ✅ Output value через pointer parameter, status code через return
4. ✅ Direct return на failure — никаких exceptions
5. ✅ Если STL может throw (std::bad_alloc, std::system_error) — локальный try/catch в implementation
6. ❌ НЕ throw C++ exceptions через extern "C" boundary
7. ✅ Compiler flag policy: `/EHsc` с локальными catches OR `/EHs-c-` для exception-free shim files

**Tests**:

- Положительный: PAL вызывает функцию с valid args → success
- Recoverable error (managed): PAL вызывает с args что trigger'ят specific exception → возвращается error code, LastError set
- Recoverable error (native): provider возвращает SHARPOS_E* code → pal/sharpos/ translates в Win32 code
- Unrecoverable (managed): induced bug bросает unexpected exception → FailFast
- Unrecoverable (native): induced C++ exception escape attempt → undefined behavior, prevented через discipline + compiler flags

### Linter — добавляется если будет нужно

Сейчас не делаем. Если в practice окажется что забывают добавить try/catch — добавляем custom Roslyn analyzer:

- Triggers на каждой `[UnmanagedCallersOnly]` функции
- Verifies что body начинается с try
- Reports warning/error если нет

Это infrastructure которая может быть нужна, может не быть. Решается при появлении реальной потребности.

### Преимущества решения

1. **Verified safety**: NativeAOT runtime гарантирует что cross-C-ABI escape физически невозможен. Не нужна программистская дисциплина для correctness.
2. **Honest catch policy**: catch только known exceptions, let unknowns crash громко. Не маскирует bugs.
3. **Resilience где нужно**: known recoverable errors (OOM, bad args) преобразуются в SystemError codes, pal/sharpos/ продолжает работать.
4. **Aggressive где надо**: unknown exceptions = fundamental bug = kernel kill = быстрый debug.
5. **Минимум infrastructure**: только manual try/catch, никаких generators/linters/helpers upfront.
6. **Future-extensible**: linter добавляется если потребуется.

### Принципы установленные D4

(дополнение к D1-D3)

12. **Catch только то что знаем как обработать.** Catch-all (Exception) — антипаттерн, маскирует bugs. Лучше громкий crash чем silent corruption. (Applicable both managed и native provider contexts.)
13. **Полагаться на runtime guarantees когда они verified.** NativeAOT обеспечивает no-escape-through-C-ABI автоматически в managed provider context. В native context аналогичная гарантия достигается через discipline (C++ ABI rules + compiler flags), не runtime mechanism.
14. **Verify before assume.** Claim'ы о runtime поведении проверяются по коду, не приниматься на веру. Particularly для critical assumptions (что именно делает FailFast, при каких условиях).