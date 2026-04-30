# Step 64 — Phase 1 closure: drop --resilient ILC flag

## Контекст

Финальный item Phase 1 per `plan.md`. `--resilient` ILC flag was the last
блокировщик "полноценного" cctor pipeline.

## Что делает --resilient

ILC flag instructs compiler к silently emit fallback stubs (sentinel pointers
типа `0xFFFFF0000000000E`) для missing helpers вместо erroring. Это hides
problems: managed `ClassConstructorRunner` оставался unreachable даже после
port в `std/no-runtime/shared/Runtime/`. Lazy patterns (`if (s == null)
s = new T()`) returned non-canonical sentinels, dereferencing → #PF.

С --resilient dropped:
- ILC errors на missing helpers вместо silent substitution
- Cctor checks routed через managed `ClassConstructorRunner.CheckStaticClassConstruction`
- Combined с `GcStaticsMaterializer` (step 41) — full canonical preinit
  pattern works.

## Решение

`OS/src/OS.csproj` — `DropResilient` MSBuild target:

```xml
<Target Name="DropResilient" AfterTargets="WriteIlcRspFileForCompilation" BeforeTargets="IlcCompile">
  <ItemGroup>
    <IlcArg Remove="--resilient" />
  </ItemGroup>
  <WriteLinesToFile File="%(ManagedBinary.IlcRspFile)" Lines="@(IlcArg)" Overwrite="true" />
</Target>
```

Runs между ILC's response file generation и actual compile invocation.
Removes `--resilient` from `@(IlcArg)` и пере-эмитит `OS.ilc.rsp`.

Step 40 originally added equivalent target но reverted с misleading comment
("ILC still routed cctor checks through internal fallback stubs"). Real
issue was missing `GcStaticsMaterializer` companion (added в step 41).
Re-enable now that materializer is in place.

## История

- Step 40: ClassConstructorRunner port + initial DropResilient attempt.
  Result: cctor probe still failed (because materializer not yet implemented).
  Reverted DropResilient с stale comment claiming "fallback stubs".
- Step 41: `GcStaticsMaterializer` added — preinit blob walks RTR section,
  materializes objects + writes к descriptor cells. Canonical pattern
  works WITH `--resilient`.
- Step 64 (this): re-enable DropResilient. Combined с already-working
  GcStaticsMaterializer = full Phase 1 pipeline.

## Результат

ILC compile clean (no missing helper errors). Boot sequence:
- All 4 cctor probe entries green (`val=7/42/42/42`).
- All 50+ NativeAOT probes green.
- All 17 EH gates green (L1-L17).
- GC + GcStress green.
- ELF apps + launcher boot normally.
- Hello.elf prints "hello from hello app" → exit code 10.
- HelloCS.elf launches stringexp → ABI=2.
- Launcher reaches с все ELF apps listed.

**No regression.** Lazy patterns (`if (s == null) s = new T()`) теперь
in scope — могут use'ить'ся в std/kernel code без workaround'ов.

## Phase 1 final status

Per `plan.md` Phase 1 deliverables:

| Item | Status |
|---|---|
| Полноценное managed exception handling | ✅ 17/17 gates (incl. multi-frame finally, collided unwind, multi-frame stack trace) |
| ClassConstructorRunner портирование | ✅ canonical + lazy patterns; --resilient dropped |
| ACPI parsing (RSDP/XSDT/MADT/HPET/MCFG) | ✅ step 38-40 era |
| RTC + HPET/TSC для timekeeping | ✅ HpetTimer + Stopwatch |
| Критерий готовности (`try/throw/catch` + ACPI/HPET/Stopwatch) | ✅ verified |

**Phase 1 закрыт.** Следующая фаза per `plan.md` — **Phase 2: PAL design +
Linux spike**. Goal: каталог CoreCLR PAL functions + 1-2 week experiment
where managed Hello World runs через CoreCLR + наши stub-PAL functions
prepared on Linux host.

Open EH polish (deferred — non-blocking):
- RFLAGS.IF restoration after HW fault (X64Asm needs proper EfiLoaderCode
  exec buffer plumbing — Phase 2 work).
- Rich stack trace formatting (real method names требуют symbol table или
  PE PDB walking).

## Файлы

### Изменённые

- `OS/OS.csproj` — `DropResilient` MSBuild target restored.
- `done/step064.md` — этот файл.
