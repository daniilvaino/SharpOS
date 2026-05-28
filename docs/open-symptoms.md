# Open symptoms (under investigation)

Trail of observations that don't yet justify a step / commit but should
not be lost. Each entry: short title, date, exact log line(s) or repro,
hypothesis (if any), what would close it.

Append-only list. When an entry is closed (root identified + fixed or
explained), move it into the relevant `done/stepNN.md` and delete here.

---

_(none open — SYM-001 retracted and SYM-002 resolved in done/step113.md:
the silent-triple-fault root was an infinite `sqrt`↔`lm_sqrt` recursion
in the Debug fork build, fixed by emitting `sqrtsd` directly.)_
