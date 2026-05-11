## D18 — Spec scope

**Status**: CLOSED — covered through D3 (trace-driven progressive classification)

**Decision**: A (observed functions only, ~30-60)
- Spec покрывает функции что реально появляются в trace observations
- Link-time surface (~144-165) НЕ purpose покрывать целиком
- Spec follows implementation; implementation follows observation
- Sage 2 round 4 explicitly warned против Variant B

**Trigger для добавления функции в spec**:
- Функция appears в trace logs во время actual scenarios
- Не upfront speculation о что может понадобиться
- Не coverage целью per se

**No additional decision needed beyond D3.**