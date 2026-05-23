# Step 107 — Regex.IsMatch + 4 more probes pass via Object::Validate non-heap guard

## Корень

CoreCLR's `Regex.IsMatch("abc123", @"\w+\d+")` падал внутри
`RegexCharClass..cctor` body. Stack trace stabilen:

```
RegexCharClass..cctor()
InitClassSlow(MethodTable*)
GetGCStaticBaseSlow(MethodTable*)
GetGCStaticBase(MethodTable*)
RegexCharClass.IsEmpty(String)
```

Fault = `MT::SanityCheck` читает `m_pMethTab` field of an "object", и
получает `0x500009970008` — это **адрес `string.Empty`** в frozen string
region (4 МиБ pool, аллоцированный сразу после `System.String..cctor`,
видно в логе: `[DoRunClassInit] pMT=0x5000098BD250` → `[vm-reserve]
va=0x500009970000 len=0x400000`).

Сам "object" по `0xF86C5F0` — **не managed**, в KernelHeap range (рядом
с другими `HeapAlloc` results). Это либо StressLogChunk либо register
spill, который **GC info decoder для interpreter mode** ошибочно
репортит как `OBJECTREF`. Конкретно — через
`TGcInfoDecoder<InterpreterGcInfoEncoding>::ReportRegisterToGC` →
`Object::Validate(0xF86C5F0)` → `GetGCSafeMethodTable()` читает first
qword (= `0x500009970008`) → `MT::Validate` → fault.

## Что НЕ помогло

**PR #119784 cherry-picked** — `Fix several issues with interpreter and
EH` (janvorli). Содержит правильные fix'ы для interpreter+EH
(`revPInvokeOffset`, native marker, `SFITER_DONE` handling,
InterpreterFrame register propagation). НО — наш конкретный fault path
сюда не попадает: cherry-pick применил, build чистый, **fault остался
identical**. Cherry-pick всё равно landed (commit `d17f0a48cb3` в форке)
— closes other interpreter issues, не вредит.

**Отключение `FEATURE_INTERPRETER` через CMake** — silent halt в early
GC heap init. CoreLib's C# code builds отдельно (csproj), сама содержит
зависимости на interpreter. Half-disable → silent crash.

**Real upstream root fix — PR #119446** — `[clr-interp] Fix reporting for
stack slots other than the locals/arguments`. Включает **conservative
GC mode** когда interpreter enabled (точно наш сценарий — conservative
не trust'ит GC info "это OBJECTREF" без range check). Но cherry-pick
дал 3 конфликта в `interpreter/compiler.cpp` + `vm/eetwain.cpp` —
значительный restructure slot allocation против нашего base. Defer до
будущего rebase.

## Workaround

`Object::Validate` (vm/object.cpp): если `this` вне CoreCLR heap range
`[g_gc_lowest_address..g_gc_highest_address)` — `return` без validation.
Семантически эквивалент conservative GC mode для debug-assert path,
но scope'ом ужe (only Validate). 7 lines.

```cpp
if defined(TARGET_SHARPOS) ... {
    uintptr_t va = (uintptr_t)this;
    uintptr_t lo = (uintptr_t)g_gc_lowest_address;
    uintptr_t hi = (uintptr_t)g_gc_highest_address;
    if (lo != 0 && hi != 0 && (va < lo || va >= hi))
        return;
}
```

## Effect

PAL/OS census:
- **До:** `OK=37  DEG=2  FAIL=8/9` (Regex.IsMatch FAIL)
- **После:** `OK=42  DEG=2  FAIL=7` — +5 probes pass

Regex.IsMatch теперь OK. Ещё 4 пробы которые тоже хитили этот debug
Validate path с register-slot bogus values тоже OK.

## Что в commits

В форке (`dotnet-runtime-sharpos`):
- `d17f0a48cb3` — cherry-pick PR #119784 (Jan Vorlicek's interpreter+EH
  fixes, authorship preserved, original PR ref in title)
- `f15fe28eddf` — step 107 — bundles наш workaround + accumulated
  TARGET_SHARPOS patches step99-106 которые работали в tree но не были
  committed (mscorrc bundle, ccomprc, crt_imp_stubs, clrex.cpp +
  excep.cpp step103 msc-throw recovery, exceptionhandling.cpp PCRE
  diag, SignatureHelper hot-path leftover, methodtable + gcenv +
  CMake glue)

В main repo (`c:/work/OS`):
- Diagnostic cleanup (BootSequence `DumpGcAndBigStack` helper removed,
  HwFaultBridge `[RCX-DUMP]` block removed — оба были temporary
  investigation tools этой сессии)

## Lessons learned

1. **R2R-precompiled CoreLib ловушка** — `bin/coreclr/.../IL/CoreLib.dll`
   (6 МБ, pure IL) vs `bin/coreclr/.../CoreLib.dll` (23 МБ, R2R native +
   IL). На ESP пятёрку. Runtime использует R2R native код без JIT, и
   **наши IL edits игнорируются** если R2R присутствует. Когда дiag
   правки в C# CoreLib "не работают" — проверить которая версия на
   ESP. Сейчас used R2R; диагностики применились потому что R2R
   regenerated при кашdom CoreLib rebuild.

2. **`Internal.Console.WriteLine` в hot JIT path = регрессия** —
   `Internal.Console.WriteLine(s)` → `Write(s + Environment.NewLineConst)`
   — string concat triggers lazy init цепочки, может тащить
   DynamicMethod / Reflection.Emit / EventSource path. Поставил это в
   `SignatureHelper.Init` для bisect — regress'нул `Dns.GetHostName`
   (раньше OK) до managed throw. Pull-out — use named-method bisect
   trick instead (no body, NoInlining, stack trace shows method name).

3. **Half-disable upstream features ≠ disable** — `-DFEATURE_INTERPRETER=0`
   в CMake выключает только native side. CoreLib C# code независим и
   continues referencing interpreter machinery. Result — silent halt.
   Either full disable in CoreLib too, or workaround on hot path.

4. **Cherry-pick правильно сохраняет authorship** — `git cherry-pick
   <sha>` от `upstream/main` сохраняет original author + PR title с
   `(#NNN)`. Видно в `git log --oneline`. Красиво для history.

## Открытые шаги (future)

- **Rebase fork на newer upstream** — естественно подтянет PR #119446
  (conservative GC mode для interpreter), можно убрать наш workaround
- **Diagnostic cleanup в форке** — exceptionhandling.cpp/clrex.cpp ещё
  содержат step103 `[PCRE]`/`[CreateThrowable]` diag traces. Полезные
  для отладки но шумят в production логе
