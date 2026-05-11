**Covered через D3 + D9**:

**От D3**: tracing infrastructure уже определена:

- Plain text logs (no JSONL)
- Sink configurable (stdout на spike, serial port на bare metal)
- Trace-driven progressive classification

**От D9**: location уже определена:

- Functional split по domain в `pal/sharpos/`
- НЕТ preinit/forward разделения (rejected — over-engineering для thin PAL)

То есть формулировка варианта A с `preinit/trace.{h,cpp}` устарела — **нет такой директории**. Реальная location: `pal/sharpos/trace.cpp`.

Variant B (managed tracer в SharpOSHost) **тоже не подходит** — tracing нужен для функций которые могут быть вызваны до SharpOSHost ready, и не должен зависеть от C-ABI boundary который сам tracing'ом покрывает.

### D15 closure

markdown

```markdown
## D15 — Tracer infrastructure location

**Status**: CLOSED — covered through D3 + D9

**Decision**: pure C++ in pal/sharpos/, domain file
- Location: `pal/sharpos/trace.cpp` (per D9 functional split, no preinit/forward)
- Format: plain text (per D3, JSONL rejected)
- Sink: configurable (stdout spike, serial port bare metal)
- Storage: BSS ring buffer (no dynamic allocation, available early)

**Not in SharpOSHost (managed)**:
- Tracing нужен до SharpOSHost ready
- Не должен зависеть от C-ABI boundary который сам tracing'ом покрывает
- Pure C++ infrastructure для resilience

**No additional decision needed beyond D3 + D9.**
```