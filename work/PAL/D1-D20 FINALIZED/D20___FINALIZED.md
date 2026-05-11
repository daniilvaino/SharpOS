# D20 — FINALIZED

## Решение

**Local leaf функции живут где **естественнее** для implementation. Direction environment-specific (per revised D10/D11):**

- **Phase 6 endpoint state** — provider environment-specific (per revised D11):
  - C++ pal/sharpos/ для тривиальных constants + hot-path thread_local
  - **Functionality** что C# уже умеет (UTF, formatting, timer) — желательно живёт в C# kernel-tier для Invariant 1
  - **ABI provider mechanism** — может быть C/C++ glue, generated veneer, direct kernel symbol, или kernel-tier C# export с `[UnmanagedCallersOnly]`. Choice decided в Phase 6.2 design — C# UCO export это **одна из возможных** форм, не default requirement.
- **Phase 2 transition state** — provider это C++ Windows shim: всё реализовано native (C++) либо через Win32 APIs либо через украденный код (minipal). C# kernel-tier недоступен.

Static linking (per D10) делает direction implementation transparent — линкер связывает оба одинаково, performance equivalent. Decision основано на **где удобнее писать**, не на "где быстрее".

## Decision rule

> Где implementation **естественнее жить** (Phase 6 endpoint):
> 
> - **Trivial constant or hot-path thread_local** → C++ pal/sharpos/
> - **Algorithmic work что C# уже умеет** → C# kernel-tier через extern "C" exports
> - **Требует kernel capabilities** → C# kernel-tier (где kernel resources)
>
> Phase 2 spike — провайдер это C++ shim, реализация native:
>
> - **Trivial constant** → C++ pal/sharpos/ (как Phase 6, transparent)
> - **Algorithmic work** → C++ shim через Win32 API или minipal (no C# kernel-tier yet)
> - **Stub for kernel capability** → C++ shim через Win32 backend (temporary)
> 
> Линкер связывает все одинаково. Performance equivalent. Critical = где удобнее код держать.

## 6 категорий — endpoint state (Phase 6) и transition (Phase 2)

|Категория|Phase 6 endpoint|Phase 2 transition (C++ Windows shim)|Обоснование Phase 6|
|---|---|---|---|
|**UTF-8/UTF-16 conversion**|C# kernel-tier|C++ shim via Win32 `MultiByteToWideChar` или minipal/utf8|`System.Text.Encoding.UTF8` уже работает в Phase 1. Reuse|
|**Process/Thread ID**|C++ pal/sharpos/|C++ pal/sharpos/ (same)|Hardcoded constants (1, 0). Тривиально, identical в обеих фазах|
|**Tick count**|C# kernel-tier|C++ shim via Win32 `GetTickCount64()`|Phase 1 имеет `OS/src/Hal/Timer/`. Reuse в Phase 6|
|**System info**|C++ pal/sharpos/|C++ pal/sharpos/ (same)|Hardcoded constants (page size 4096, CPU count 1, x86-64). Identical|
|**Get/SetLastError**|C++ pal/sharpos/|C++ pal/sharpos/ (same)|thread_local per D2, hot path, per-thread state. Identical|
|**printf-family**|C# kernel-tier|C++ shim via MSVC CRT|C# может форматировать строки. Reuse BCL в Phase 6|

## Конкретная реализация

### Структура pal/sharpos/

```
pal/sharpos/
├── memory.cpp        ← VirtualAlloc → C# kernel-tier (kernel page tables)
├── thread.cpp        ← CreateThread = ABORT_FATAL stub (per D5)
├── module.cpp        ← LoadLibrary → C# kernel-tier
├── file.cpp          ← CreateFile → C# kernel-tier (kernel filesystem)
├── unicode.cpp       ← MultiByteToWideChar → C# kernel-tier (System.Text.Encoding)
├── time.cpp          ← GetTickCount → C# kernel-tier (kernel timer)
├── process.cpp       ← GetCurrentProcessId → return 1 (trivial constant)
├── sysinfo.cpp       ← GetSystemInfo → hardcoded constants
├── errno.cpp         ← Get/SetLastError thread_local (per D2)
├── crt.cpp           ← snprintf etc → C# kernel-tier formatting
├── exception/        ← unwind path (per D13)
└── trace.cpp         ← scaffolding (per D3+D15+D17)
```

### Пример C++ trivial constant (process.cpp)

```cpp
// pal/sharpos/process.cpp
// Trivial constants — нет смысла делать host call
// для возврата фиксированного значения

DWORD WINAPI GetCurrentProcessId(void) {
    return 1;  // Single-process semantic в SharpOS
}

DWORD WINAPI GetCurrentThreadId(void) {
    return 0;  // Single-thread в Phase 6.1 per D5
}
```

### Пример pal/sharpos/unicode.cpp (identical в Phase 2 и Phase 6)

```cpp
// pal/sharpos/unicode.cpp
// Algorithmic work — провайдер реализует через extern "C"
// 
// Provider symbol resolution environment-specific:
//   Phase 2: sharpos_host_windows_shim.lib (C++ через Win32)
//   Phase 6: kernel-tier C# export через [UnmanagedCallersOnly]

extern "C" int SharpOSHost_Utf8ToUtf16(
    const char* utf8, int utf8Len,
    wchar_t* utf16, int utf16Capacity);

int WINAPI MultiByteToWideChar(
    UINT codePage, DWORD flags,
    LPCSTR lpMultiByteStr, int cbMultiByte,
    LPWSTR lpWideCharStr, int cchWideChar)
{
    if (codePage != CP_UTF8) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return 0;
    }
    
    // Direct extern "C" call — линкер связал с provider
    return SharpOSHost_Utf8ToUtf16(
        lpMultiByteStr, cbMultiByte,
        lpWideCharStr, cchWideChar);
}
```

`pal/sharpos/` код **идентичен** в обоих фазах. Меняется только **provider implementation** что резолвит SharpOSHost_Utf8ToUtf16.

### Provider Phase 2 — C++ Windows shim

```cpp
// sharpos_host_windows_shim/unicode.cpp
// Phase 2 implementation — Win32 backend

#include <windows.h>
#include "sharpos_host_api.h"

extern "C" int SharpOSHost_Utf8ToUtf16(
    const char* utf8, int utf8Len,
    wchar_t* utf16, int utf16Capacity)
{
    // Direct Win32 — temporary spike infrastructure
    return ::MultiByteToWideChar(
        CP_UTF8, 0,
        utf8, utf8Len,
        utf16, utf16Capacity);
}
```

### Provider Phase 6 — one possible form: kernel-tier C# exports

Применимо **если** Phase 6 решит использовать managed provider context. Per revised D11 — provider environment-specific, и C# `[UnmanagedCallersOnly]` это **одна из возможных форм**, не default. Другие варианты: C/C++ glue к kernel internal ABI, generated veneer, direct kernel symbol. Choice decided в момент Phase 6.2 design.

Если выбрана managed provider form:

```csharp
// SharpOS kernel-tier code (НЕ отдельная NativeAOT static library)
public static class UnicodeExports
{
    [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_Utf8ToUtf16")]
    public static int Utf8ToUtf16(byte* utf8, int utf8Len, char* utf16, int utf16Capacity)
    {
        // Reuse System.Text.Encoding из Phase 1 BCL
        ReadOnlySpan<byte> input = new ReadOnlySpan<byte>(utf8, utf8Len);
        Span<char> output = new Span<char>(utf16, utf16Capacity);
        
        try {
            return Encoding.UTF8.GetChars(input, output);
        }
        catch (ArgumentException) {
            // Buffer too small или invalid sequence
            // Per D4 — translate exception to return value, no escape
            return 0;
        }
    }
}
```

Phase 6 provider может также быть C/C++ glue к kernel internal ABI (без managed code) — решается в момент Phase 6.2 design (per D5).

## Что украдено

|Артефакт|Источник|Использование|
|---|---|---|
|`System.Text.Encoding.UTF8`|Phase 1 BCL infrastructure|UTF conversion в Phase 6 (managed provider context)|
|Phase 1 Timer infrastructure|`OS/src/Hal/Timer/`|Tick count в Phase 6 (managed provider context)|
|Phase 1 BCL string formatting|NativeAOT BCL|printf-family в Phase 6 (managed provider context)|
|Win32 `MultiByteToWideChar` / `GetTickCount64` / MSVC CRT|Win32 API|Phase 2 Windows shim implementations|
|D2 thread_local pattern|D2 финализ|Last error storage (identical в обеих фазах)|

## Что наше

- Decision rule для direction (C++ pal/sharpos/ vs C# kernel-tier vs C++ shim)
- pal/sharpos/ thin wrappers что делегируют через extern "C" (identical в обеих фазах)
- Phase 2 Windows shim implementations (temporary, через Win32 backends)
- C# `[UnmanagedCallersOnly]` exports для kernel-tier functions (Phase 6 if managed provider context)

## Расширение списка по trace-driven discovery

Список **не финальный**. Per D3 (trace-driven progressive classification), новые функции попадут в один из buckets когда trace observations покажут что они вызываются.

Применяем decision rule **per phase**:

**Phase 6 endpoint state**:
- C# уже умеет это? → kernel-tier
- Тривиальная константа? → C++ pal/sharpos/
- Hot path per-thread state? → C++ pal/sharpos/ (как D2)
- Требует kernel capabilities? → kernel-tier

**Phase 2 transition state** (every algorithmic function gets C++ shim implementation):
- Тривиальная константа? → C++ pal/sharpos/ (same)
- Hot path per-thread state? → C++ pal/sharpos/ (same)
- Algorithmic? → C++ Windows shim через Win32 backend OR украденный код (minipal)
- Kernel capability? → C++ Windows shim через Win32 stub OR ABORT_FATAL для not-yet-supported

Возможные future additions (Phase 6 endpoint):

- **Memory barriers / atomics** → C++ pal/sharpos/ (CPU intrinsics, нет смысла делегировать; identical Phase 2)
- **Random number generation** → C# kernel-tier (System.Random или kernel RNG); Phase 2 через Win32 BCryptGenRandom
- **GUID generation** → C# kernel-tier (System.Guid); Phase 2 через Win32 CoCreateGuid или UuidCreate
- **Cache flush operations** → C++ pal/sharpos/ (CPU intrinsics; identical Phase 2)

## Что НЕ local leaf (для контраста)

Чтобы decision rule был ясен — примеры что **точно** идут через kernel C# (требуют kernel capabilities):

- **VirtualAlloc** → kernel page tables, virtual memory mapping
- **CreateFile / ReadFile / WriteFile** → kernel filesystem
- **CreateThread** → kernel scheduler (но в Phase 6.1 = ABORT_FATAL per D5)
- **LoadLibrary** → kernel module loader
- **VirtualProtect** → kernel page table updates

То есть всё что нуждается в **kernel resources** — однозначно в C# kernel-tier.

## Связь с другими decisions

- **D2** (LastError): thread_local pattern в C++ pal/sharpos/, hot path. Identical в обеих фазах.
- **D3** (scaffolding): trace-driven progressive classification применяется к D20 expansion
- **D5** (CreateThread = ABORT_FATAL): Process/Thread ID hardcoded к single-process/thread values
- **D9** (тонкий PAL): pal/sharpos/ остаётся thin layer независимо от provider implementation
- **D10 revised** (CoreCLR guest archive + provider environment-specific): direction transparent через static linking. Provider это либо C++ Windows shim (Phase 2) либо kernel-provided symbols (Phase 6)
- **D11 revised** (SharpOSHost_* как ABI namespace): same extern "C" calls в обеих фазах, отличается только provider
- **Phase 2 Redesign**: Phase 2A может shortcut через Win32, Phase 2B обязан использовать SharpOSHost_* boundary (D11 enforced)
- **Invariant 1** (C# only в основном репо): максимизация C# в kernel-tier удовлетворяет принцип в Phase 6 endpoint state

## Принципы установленные D20

(в дополнение к D1-D17 + Phase 2 Redesign + TARGET_SHARPOS Build Configuration)

42. **Static linking делает direction transparent.** Local C++ vs kernel-tier C# через extern "C" exports — оба прямые function calls после линковки. Performance equivalent. Decision на основе **где удобнее держать код**, не на performance.
    
43. **Maximize C# kernel-tier для Invariant 1.** Чем больше функционала живёт в основном C# репо, тем меньше C++ кода в форке dotnet/runtime. Каждый C++ файл в форке = forever maintenance burden.
    
44. **Reuse Phase 1 infrastructure где она уже работает.** `System.Text.Encoding`, kernel timer, BCL formatting — Phase 1 infrastructure что мы уже написали и проверили. Не дублируем через украденный minipal или другой external код.
    
45. **Trivial constants в C++ — это OK.** Hardcoded `return 1` для GetCurrentProcessId не нуждается в kernel-tier delegation. Тривиально, нет maintenance burden, нет state.
    

---
