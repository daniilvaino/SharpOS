## D14 — Cross-runtime exception rule

**Status**: CLOSED — covered through D4

**Decision**: A (hard prohibition) — exceptions не пересекают C-ABI boundary
- SharpOSHost экспорты обёрнуты try/catch wrapper (D4)
- Catch только known exceptions → SystemError
- NativeAOT runtime FailFast гарантирует hard prohibition даже при ошибке discipline
- Variant B technically невозможен в NativeAOT environment

**No additional decision needed beyond D4.**