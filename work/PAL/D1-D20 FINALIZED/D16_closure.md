## D16 — Phase markers manual vs auto

**Status**: CLOSED — covered through D3 (add-when-needed principle)

**Decision**: A (manual) — IF phase markers actually needed
- Не реализуем upfront — добавляется только когда trace logs реально требуют segmentation
- Manual setters в bootstrap code + wrappers вокруг known transition points
- Auto-detect heuristics rejected (over-engineering, как JSONL logging)

**Trigger для добавления**:
- Trace logs становятся unreadable без context
- Cannot tell в какой фазе lifecycle какие events происходили
- Multiple fixes needed для phase context disambiguation

**No additional decision needed beyond D3.**