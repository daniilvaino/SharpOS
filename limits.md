# Известные проблемы и временные ограничения

Не таблица фич — она осталась в [`README.md`](README.md). Здесь сводный реестр
того, что **сломано / висит / ждёт hardening**. Подробности - в [`docs/coreclr-hosted-limits.md`](docs/coreclr-hosted-limits.md), [`docs/open-symptoms.md`](docs/open-symptoms.md), активные риски R1-R5 в [`plan.md`](plan.md).

| Проблема | Tier | Статус | Источник / комментарий |
|---|---|---|---|
| `GC.WaitForPendingFinalizers` зависает | CoreCLR-hosted | 🔴 hang | SYM-003: finalizer-thread completion event не wired; `GC.Collect` сам работает |
| `DateTime.Now` (local timezone) | все | 🔴 | нет tz DB; `DateTime.UtcNow` через CMOS+HPET ✅ |
| `Process.Start` | CoreCLR-hosted | 🔴 | отсутствует `CreateProcessW` (лог 2026-08-21). Ярус решает по Windows-пути; порождения процессов у нас нет вовсе |
| `GZipStream` / `System.IO.Compression` | все | 🔴 | `libSystem.IO.Compression.Native` отсутствует |
| Hosted GC suspend/resume cooperation | CoreCLR-hosted | ⏳ R4 | cooperative safepoints + RetainVM/decommit policy не production-complete |
| Strong-fallback аудит `SharpOSHost_*` | Fork/PAL | ⏳ R1 / D10-D11 | fallback'и в той же TU обязаны быть `weak`, иначе Release clang-fold подменяет до линковки |
| IST / emergency fault stacks (#PF/#DF/NMI) | Kernel | 🔴 R2 | stack overflow → silent triple-fault; panic path должен не аллоцировать |
| FH4 catch-object construction (`dispCatchObj` / copy-ctor) | Fork EH | ⏳ | паритет с FH3 (тоже без него); вся EH-батарея зелёная без него, но `catch(Exception&)` by-value не построится |
| `CultureInfo.GetCultureInfo("ru-RU")` non-invariant | CoreCLR-hosted | ⏳ | runtimeconfig прибит к `InvariantGlobalization=true`; ICU/icudt.dat не пакуется, `System.Globalization.Native` PAL не реализован. Не архитектурный запрет - отложено до конкретной потребности |
| Self-modifying shellcode без cpuid-serializer | Kernel | ⏳ | патчеры пишут template из `.rdata` (через `BootAsm.Generator`) и сразу зовут без cpuid serializing; QEMU forgiving, реальное железо может выполнить stale prefetch |
| AOT хойстит non-volatile MMIO-poll | Kernel | ⚠️ контракт | ILC LICM выносит MMIO-чтение из spin-петли (compile-time); все HW-poll **обязаны** идти через `NoInlining` Rd-барьер или `volatile` |

**Легенда**: 🔴 - известно сломано, ⚠️ - действующий контракт/ограничение, ⏳ - отложено / в работе.

**Текущий roadmap:** единый план ведётся в [`plan.md`](plan.md) и [`donext.md`](donext.md).

**Состояние на 2026-08-22.** Три яруса зелёные на своих батареях: ядро (~120 именованных проб), PE-приложения (`AOTTESTS.EXE`, 55 проверок), CoreCLR-hosted (ценз, OK=155). DOOM играбелен, PowerShell доходит до prompt'а. Лаунчер — приложение на Terminal.Gui, запускает и нативные PE, и управляемые сборки.

**Открыто и воспроизводится:** многопоточный JIT под вытеснением роняет размещённый рантайм на настоящем железе (на эмуляторах не повторяется) — разбор в `donext.md`; второй запуск одной управляемой сборки падает на статике контекста по умолчанию; выделение при исчерпании пула приложения возвращает `null` вместо исключения.
