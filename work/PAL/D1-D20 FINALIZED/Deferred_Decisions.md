# Deferred Decisions

## D6 — Thread state ownership (vm/ vs host)
- **Reason**: нет threads на spike (D5 = ABORT_FATAL, Zero GC, finalizer skipped)
- **Reopen when**: Phase 6.2 (после Phase 3 + D5 переоткрыт)
- **Initial inclination**: vm/ владеет (стандартный CoreCLR pattern)

## D7 — TLS implementation pathway
- **Status**: COVERED через D2 (Phase 5.5 — Native TLS bring-up)
- Не переоткрывается отдельно

## D8 — GC thread suspension mechanism
- **Reason**: GC отключён (Zero GC) → нет stack scan → нет suspension
- **Reopen when**: Phase 6.2 (switch на standard GC)
- **Initial inclination**: signal-based SIGUSR1 + ucontext (стандартный Linux PAL pattern)

## Reopening protocol
1. Проверить dependencies готовы
2. Re-evaluate с актуальным контекстом
3. Применить D-series principles
4. Финализировать как D-decision
5. Удалить из deferred.md