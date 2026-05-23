# Step 103 — Phase E11: ThreadPool/Task.Run + msc-throw recovery + Unix-style mscorrc bundle

**Date:** 2026-05-23
**Status:** ✅ ThreadPool + Task.Run работают, msc-throws раскрываются в specific managed exceptions (`InvalidCastException`, `EntryPointNotFoundException`, `CultureNotFoundException` с **proper localized message**). 🚧 Timer (1ms) skipped — отдельный шаг. 🚧 Regex.IsMatch падает в `COR_E_EXECUTIONENGINE` (не msc-throw, прямой crash; кандидат — stack-overflow в Regex compiler).

## Что закрыто

### 1. ThreadPool / Task.Run (E11 step1)

`new Task(() => 42).Result` теперь возвращает 42 на bare-metal. Path
требовал:

#### PAL surface

`OS/src/PAL/SharpOSHost/IocpBridge.cs` (новый) — LIFO counting
semaphore wrapper для backing `LowLevelLifoSemaphore.Windows.cs`
который использует `PortableThreadPool` для worker wake-up.
Реализован через polling-timeout (Hpet) — single-CPU cooperative.

`OS/src/Kernel/Threading/Iocp.cs` (новый) — kernel-side LIFO
semaphore. Использует **concrete-type pattern**
`if (target is not Iocp ev) return 0` вместо `LookupAs<T>(...)` —
generic `RhTypeCast_IsInstanceOf` нелинкуем в std/no-runtime
([reference_rhtypecast_generic_unresolved]).

`OS/src/Kernel/Threading/Semaphore.cs` — добавлен `TryAcquire()`
(non-blocking permit consumption).

#### Fork PAL stubs

Добавлены 5 missing P/Invokes в `crt_imp_stubs.cpp`:
- `LocalAlloc` / `LocalFree`
- `InitializeConditionVariable` / `SleepConditionVariableCS` /
  `WakeConditionVariable`
- `GetSystemTimes`, `QueryUnbiasedInterruptTime`,
  `GetCurrentProcessorNumberEx`
- IOCP wrappers: `CreateIoCompletionPort`, `GetQueuedCompletionStatus`,
  `PostQueuedCompletionStatus` (forward к `SharpOSHost_Iocp*`).

Все зарегистрированы в kernel32 resolver.

### 2. msc-throw → specific managed exception (step103c)

**До:** `(InvalidCastException)x` cast ловился только как
`SEHException` ("External component has thrown an exception"), даже
если был `EEMessageException(kind=kInvalidCastException)`.

**Корень:** на `HOST_WINDOWS + TARGET_UNIX + TARGET_SHARPOS` build
`PAL_TRY/PAL_CATCH` оборачивает MSVC C++ throws (code `0xE06D7363`)
как нативный `SEHException`. По дороге через
`CreateCOMPlusExceptionObject` / `GetThrowableFromException`
шло в default branch `MapWin32FaultToCOMPlusException` →
`kSEHException` — теряли оригинальный m_kind.

**Фикс:** в обоих местах (`vm/excep.cpp:CreateCOMPlusExceptionObject`,
`vm/clrex.cpp:GetThrowableFromException`) детектируем
`ExceptionCode == 0xE06D7363 + magic ∈ {0x19930520..22}` и
восстанавливаем embedded throwable через `ExceptionInformation[1]`.

**Ловушка which I hit (step103a/b):** для `throw new T(...)` —
тип throw-выражения **pointer**, и `ExceptionInformation[1]` это
`void**` (адрес слота хранящего бросаемый указатель), **не сам
объект**. Без extra deref читали "vtable" = адрес объекта;
виртуальный вызов халтил.

m_kind=16 (`kEntryPointNotFoundException`) на Socket ctor (missing
P/Invoke `SystemNative_CreateSocketEventPort`) подтвердил
интерпретацию.

### 3. Unix-style mscorrc bundle (step103d)

`EEMessageException::GetThrowableMessage` дёргает
`LoadResource(CCompRC::Error, m_resID)` → mscorrc.dll. Раньше под
HOST_WINDOWS+TARGET_SHARPOS шёл `WszLoadLibrary("mscorrc.dll")` →
не найдено → throw в `CreateThrowable` → recursion → fatal
`TerminateProcess(COR_E_EXECUTIONENGINE)`.

На Unix CoreCLR делает **compile-in** native string table через
`processrc.ps1` / `.sh` (parsing `STRINGTABLE BEGIN ... END`
в `mscorrc.rc` → C++ массив `{resID, "string"}` пар). Lookup
через `bsearch` с **fallback** `"Undefined resource string
ID:0x..."` — БЕЗ throw.

**Реализован Unix path под TARGET_SHARPOS:**

- `src/coreclr/CMakeLists.txt`: native resource pipeline gate
  расширен `if(CLR_CMAKE_HOST_UNIX OR CLR_CMAKE_TARGET_SHARPOS)`.
- `src/coreclr/dlls/mscorrc/CMakeLists.txt`: под SHARPOS строится
  как `STATIC` lib (не `OBJECT` — `PUBLIC` target_link_libraries
  не propagate'ит OBJECT lib транзитивно через utilcode).
- `src/coreclr/utilcode/CMakeLists.txt`: `nativeresourcestring` +
  `mscorrc` как `PUBLIC` deps `utilcodestaticnohost`/`utilcode_dac`/
  `utilcode` — все downstream (coreclr/mscordbi/mscordac/jit/…)
  автоматически тащат символ.
- `src/coreclr/utilcode/ccomprc.cpp`: три `#ifdef HOST_WINDOWS` →
  `#if defined(HOST_WINDOWS) && !defined(TARGET_SHARPOS)` (LoadString
  branch, GetLibrary, LoadResourceFile).
- `src/coreclr/nativeresources/processrc.ps1`:
  - `extern NativeStringResourceTable` → **`extern const`** (MSVC name
    mangling включает const).
  - `__attribute__((visibility("default")))` обёрнут `#ifdef _MSC_VER`
    skip-attribute / else GCC visibility default.
- `src/coreclr/nativeresources/resourcestring.cpp`: parameter и
  body использует `char16_t*` matching header (с
  `reinterpret_cast<WCHAR*>` для Win32 API calls — MSVC `WCHAR =
  wchar_t`, не `char16_t`).
- `OS/OS.csproj`: kernel link добавил `mscorrc.lib` +
  `nativeresourcestring.lib` (ILC не CMake; auto-propagation не
  работает — нужны явно).

**Real impls (заменили CRT_STUB trap):**

- `bsearch` (~10 строк) — portable binary search, используется
  `LoadNativeStringResource`.
- `FormatMessageW` (~80 строк) — minimal impl покрывающий CoreCLR
  usage `SString::FormatMessage`: `FORMAT_MESSAGE_FROM_STRING +
  ARGUMENT_ARRAY [+ ALLOCATE_BUFFER]`. Inserts `%1..%9` с optional
  `!s!` суффиксом, `%%`, `%n`, `%0`.

### 4. CultureInfo.GetCultureInfo подтверждает работу

```
[FAIL] CultureNotFoundException: Only the invariant culture is
supported in globalization-invariant mode. See https://aka.ms/...
ru-ru is an invalid culture identifier.
```

Specific exception type + full localized message — **точно как на
Linux/Windows**. До step103 это ловилось как
`SEHException("External component has thrown an exception.")`.

## Документация

### `docs/eh-model.md` — новая секция «Жизненный путь исключения в Tier C»

Карта типов exceptions:

- **Native (C++) иерархия в форке** — `Exception → CLRException →
  EEException → {EEMessageException, EEResourceException, EECOMException,
  EEFieldException, EEMethodException, EEArgumentException,
  EETypeLoadException, EEFileLoadException}` + sibling `SEHException`,
  `HRException`, `OutOfMemoryException`. Каждый c_type ID + что
  несёт (m_kind/m_hr/m_resID).
- **Managed (BCL) иерархия** — `System.Exception → SystemException →
  InvalidCastException / CultureNotFoundException / EntryPointNotFoundException`.
- **4 SEH кода**: `EXCEPTION_COMPLUS (0xE0434352)` для managed
  throw, `MSC (0xE06D7363)` для native `COMPlusThrow`, HW codes,
  raw native SEH.
- **msc throw layout** включая ловушку с `void**` (адрес слота
  хранящего указатель, не объект) — нужен extra deref.
- **Две точки конвертации** native → managed:
  `GetThrowableFromException` и `CreateCOMPlusExceptionObject`.
- **CreateThrowable internals**: virtual `GetThrowableMessage` ветки
  + источники message (**mscorrc** compiled-in, **mscorlib SR.resx**,
  **System.Globalization** invariant-mode hardcoded).
- **Диаграмма** полного пути `(Bar)foo cast → InvalidCastException
  catch`.
- **Сводка** симптом→корень→фикс для всех EH-багов: step71
  string-as-MethodTable, step103c msc-throw lost type, step103b
  vtable-garbage red herring, step103d mscorrc cascade.

## Files (форк + ядро)

### Fork

- `src/coreclr/vm/excep.cpp` — msc-throw recovery в
  `CreateCOMPlusExceptionObject`.
- `src/coreclr/vm/clrex.cpp` — same в `GetThrowableFromException`.
- `src/coreclr/CMakeLists.txt` — native resource pipeline под
  SHARPOS.
- `src/coreclr/dlls/mscorrc/CMakeLists.txt` — STATIC lib под SHARPOS.
- `src/coreclr/utilcode/CMakeLists.txt` — PUBLIC link mscorrc +
  nativeresourcestring.
- `src/coreclr/utilcode/ccomprc.cpp` — три gate'а на Unix-style
  branch.
- `src/coreclr/nativeresources/processrc.ps1` — `extern const` +
  MSVC attr guard.
- `src/coreclr/nativeresources/resourcestring.{h,cpp}` — char16_t
  signature sync.
- `src/coreclr/pal/sharpos/crt_imp_stubs.cpp` — bsearch real,
  FormatMessageW real, LocalAlloc/Free, ConditionVariable shims,
  IOCP forwards, GetSystemTimes/QueryUnbiasedInterruptTime/
  GetCurrentProcessorNumberEx, TerminateProcess caller VA dump.

### Kernel

- `OS/OS.csproj` — link mscorrc.lib + nativeresourcestring.lib.
- `OS/src/Kernel/Threading/Iocp.cs` — new.
- `OS/src/Kernel/Threading/Semaphore.cs` — `TryAcquire()`.
- `OS/src/PAL/SharpOSHost/IocpBridge.cs` — new.
- `docs/eh-model.md` — Жизненный путь раздел.

## Acceptance

- ThreadPool [OK], Task.Run [OK] добавлены к OK.
- Socket ctor (TCP) — раньше hard-crash, теперь ловится как
  `EntryPointNotFoundException` (от missing
  `SystemNative_CreateSocketEventPort` P/Invoke).
- CultureInfo.GetCultureInfo(ru-RU) — раньше `SEHException`, теперь
  `CultureNotFoundException` с full localized message.

## Что не сделано / отложено

- **Timer (1ms)** — hard-halt без явного `[seh] throw`. Skip'нут в
  `work/normal-hello/Program.cs` до step104+.
- **Regex.IsMatch** — `TerminateProcess(COR_E_EXECUTIONENGINE 0x80131506)`
  без `[seh] throw`. Это прямой crash (не msc-throw / не mscorrc
  cascade — mscorrc bundle устранил тот корень). Кандидаты:
  stack-overflow в Regex compiler (recursive descent parser),
  "SEH exception leaked into managed code" от unrecoverable HW
  fault в JIT'ed Regex code. caller=0x1C177896 (RVA 0x2D896) =
  стандартный FailFast → TerminateProcess pattern.
- **Полный census after step103** не прогнан end-to-end (Regex
  блокирует suite).

## Next

- Step 104: Regex root cause (stack-overflow vs leaked-SEH) +
  Timer (1ms) hard-halt diagnostic.
- Phase E12-E13: ALC, audit.
