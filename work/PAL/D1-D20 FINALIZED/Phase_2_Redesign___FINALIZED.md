## Phase 2 Redesign — FINALIZED

### Решение

**WSL primary spike retired. Windows-hosted TARGET_SHARPOS replacement PAL spike — primary.**

WSL spike findings архивированы как valuable prior measurement, не выбрасываются. Не primary anymore.

### Causes

1. **WSL pulls к Linux substrate** — libunwind, .eh_frame, signals, pthread, dlopen, POSIX locale. Каждое — что мы **не хотим** на bare metal.
2. **Windows mental model совпадает** с Phase 1 SharpOS (.pdata, RtlVirtualUnwind-style, Win64 calling convention).
3. **One EH mental model лучше** для solo developer чем два (Phase 1 .pdata vs Phase 6 .eh_frame).
4. **Hidden deps будут везде**, но на Windows они ближе к production model. На WSL они вреднее как архитектурный соблазн (легко принять libunwind как production dependency).
5. **WSL spike уже отошёл от D10** — `libcoreclr.so` через dlopen, `libsharposhost.a` не подключена. Если переделывать под D10 discipline — вопрос «WSL или Windows» открыт заново, и Windows побеждает.

### Phase 2A vs Phase 2B

**Production-shaped path с первого дня** (per sage 2 round 7 finalization):

```
CoreCLR → pal/sharpos/ → SharpOSHost_* → Windows shim → Win32
```

Boundary одинаковый в обеих фазах. Отличаются только **scope** и **fidelity** of shim implementations.

**Phase 2A — Quick surface discovery (3-5 дней)**:

- pal/sharpos/ **обязан** идти через SharpOSHost_* (D11 firewall enforced с первого дня)
- Может использовать **minimal/stub** SharpOSHost_* shim implementations (just enough для Hello World)
- Цель — быстрый первый trace, surface measurement
- Path remains production-shaped, не shortcut
- НЕТ direct Win32 в pal/sharpos/

**Phase 2B — Production-shaped boundary (полная функциональность)**:

- pal/sharpos/ через SharpOSHost_* (same as Phase 2A)
- SharpOSHost shim implementations расширены до full functionality
- Validates D9/D10/D11 boundaries полностью
- Final gate Phase 2

**Final gate = 2B**, не 2A. Но **boundary identical** в обеих фазах.

**Почему НЕ Phase 2A direct Win32 shortcut**:

Старая редакция разрешала pal/sharpos/ временно вызывать Win32 напрямую в Phase 2A для скорости. Per sage 2 round 7 — это **antipattern**:

1. **Bad precedent**: как только pal/sharpos/ начнёт включать `<windows.h>` и вызывать `VirtualAlloc/CreateFileW/Rtl*` напрямую, audit становится слабее. Придётся каждый раз доказывать "это временно, это не попало в Phase 2B".
2. **Solo developer cognitive load**: лишняя ментальная нагрузка отслеживать что временно vs production-shaped.
3. **Main redesign point**: цель — измерять **production-shaped** path. Direct Win32 shortcut измеряет неправильное.
4. **Минимальная экономия**: вызвать `SharpOSHost_AllocPages` в pal/sharpos/memory.cpp вместо `VirtualAlloc` примерно того же объёма кода. Создание `sharpos_host_windows_shim/memory.cpp` со stub `VirtualAlloc`-wrapper — несколько строк.

### Допустимое исключение

Только diagnostic-only, **отдельно от PAL path**:

```
diagnostics/windows_unwind_compare.cpp
```

под explicit флагом:

```
SHARPOS_DIAGNOSTIC_COMPARE_WITH_WINDOWS_UNWINDER
```

И это **не входит** в production-shaped Phase 2B build. Diagnostic build только для validation Phase 1 unwinder против Windows native (per D13 oracle pattern).

### Strict rules

**Не разрешено**:

- Stock Windows CoreCLR spike (не валидирует наш PAL)
- TARGET_UNIX wholesale на Windows (тащит POSIX substrate)
- POSIX emulation layer
- Windows `RtlVirtualUnwind` как production implementation в pal/sharpos/

**Разрешено**:

- TARGET_SHARPOS condition с PAL pattern enforcement
- Windows-backed SharpOSHost bodies temporary для spike
- `RtlVirtualUnwind` как diagnostic oracle
- WSL как secondary diagnostic environment если нужно сравнение

### Link/Import audit обязателен

**На Windows**:

```
dumpbin /DEPENDENTS, /IMPORTS
link.exe /VERBOSE:LIB
DEFAULTLIB inspection
```

**На WSL** (если когда-то возвращаемся для comparison):

```
readelf -d, -Ws
objdump -p
nm -uC
ld --trace, -Wl,-Map=, -Wl,--trace-symbol=, -Wl,--no-undefined
```

**Allowlist подход**:

- Разрешено: минимальный runtime substrate для hosted spike
- Запрещено без классификации: libunwind, libdl, pthread, libgcc_s EH helpers, неожиданные libc calls, системный iconv/locale

### Timebox

**3-5 дней** до первого pal/sharpos trace на Windows.

Если не получается за 5 дней:

- Не продолжать вслепую
- Зафиксировать blocker
- Решить: либо вернуться к WSL, либо сузить Windows spike до D13-only oracle

### WSL findings что переносятся в Windows spike

**Platform-independent measurements** (ожидаются и на Windows):

- `MultiByteToWideChar` early trap (charset infrastructure требуется до Hello World)
- Eventing surface через upstream `dummyprovider`
- C++ ABI symbol pollution (`_CONTEXT::operator=` etc.)
- ~165 PAL link-time surface estimate
- libstdc++ link discovery

Useful baseline для Windows spike planning. Не нужно re-discover.

### Связь с другими decisions

- **D9** (структура pal/sharpos/): same domain split на Windows и bare metal
- **D10** (статическая линковка): static link на обеих платформах, no dlopen
- **D11** (extern "C"): direct calls SharpOSHost_* enforced в Phase 2B
- **D13** (.pdata canonical): Windows native соответствует выбранной EH модели

### Принципы установленные Phase 2 Redesign

(в дополнение к D1-D13)

33. **Hosted spike validates production pathway, не shortcut.** Выбор host environment matters — притягивает к specific assumptions. Выбираем то что ближе к bare metal mental model.
34. **Architectural soul disambiguation.** Кажется что host environment не матерь архитектурно — на самом деле матерь. Hidden deps направляют design decisions подсознательно.
35. **Link audit как infrastructure discipline.** Любой hosted spike требует explicit audit что подтянулось. Allowlist подход. Без audit — false sense of validation.
36. **Phase split bootstrap (X.A) vs production-shaped (X.B).** Quick discovery shortcut допустим для surface measurement, но financial gate всегда production-shaped boundary.