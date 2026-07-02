# step128 — CLM → FullLanguage bypass + coverage-honesty round

## TL;DR

Два самостоятельных куска работы, оба идут одним шагом:

1. **CLM → FullLanguage flip** через managed bootstrap shim. Reflection-
   инжект `SystemPolicy.s_systemLockdownPolicy = None` **до** первого
   вызова `GetSystemLockdownPolicy()`. `[Math]::Sqrt(16) → 4`,
   `[Math]::Sin(13) → 0.42` — на bare metal PS 7.5.5.
2. **Coverage honesty round**: пробы + README-таблица.
   - Kernel probe `Probe_Enum` разбит на три (`enum.cast`,
     `enum.bitwise`, `enum.toString`) — видно что где красное.
   - CoreCLR-hosted пробы: lock / ValueTuple / DateTime / TimeSpan /
     Enum / Math.Sqrt/Sin/Cos/Tan/Atan/Pow/Log/… (16 функций).
   - Раскопан реальный `lm_atan` баг: Leibniz truncated к x¹³,
     `atan(1)=0.821` вместо `π/4=0.785` (**3.5% ошибка**).
   - README-таблица переделана: `Math.Sqrt/Abs` (SSE) ✅✅✅,
     транценденты 🔴🔴🟡 с пометкой "временные приближения",
     Floor/Ceil/Trunc/Round 🔴🔴✅ отдельно (bit-manipulation).
   - Прочие честные вердикты: `string.Format` 🟡🟡✅, `lock` 🔴🔴✅,
     `Enum` 🟡🟡✅, `ValueTuple/DateTime/TimeSpan` 🔴🔴✅.
3. **Launch-target рычаг:** `Probes.LaunchNormalHelloCensus` const
   bool переключает `coreclr_execute_assembly` между PS bootstrap и
   census-пробой NormalHello. ILC folds ненужную ветку.

## 1. CLM bypass

### Корень
PS 7.5 убрала env-var override `__PSLockdownPolicy` (был в PS 5.1,
security review). Наши WLDP / Safer / SHGetKnownFolderPath стабы не
могли **консистентно** отреагировать "no policy": хоть один сигнал
→ fail-secure CLM cached на runspace. Попытки step126 подкрутить
консенсус фейлилились либо тихим CLM либо `SecurityException →
FailFast`. Правильный путь — обход probe-машинерии полностью.

### Решение
Тonyкий managed shim `apps/PowerShellBootstrap/`:

```csharp
// PowerShellBootstrap Main:
var sma = Assembly.Load("System.Management.Automation");
var policy = sma.GetType("...SystemPolicy");
var mode = sma.GetType("...SystemEnforcementMode");
object none = Enum.ToObject(mode, 0);
var nullable = typeof(Nullable<>).MakeGenericType(mode);
object boxed = Activator.CreateInstance(nullable, none);
foreach (FieldInfo f in policy.GetFields(NonPublic|Static))
    if (f.FieldType == nullable) f.SetValue(null, boxed);
// Then forward to Microsoft.PowerShell.ManagedPSEntry.Main(args)
```

**Defensive enum** — не хардкодим `s_systemLockdownPolicy`, ищем
любое статическое `Nullable<SystemEnforcementMode>` поле в
`SystemPolicy`. Правильное решение: fork нашёл **два** таких поля
(`s_systemLockdownPolicy` **и** `s_cachedWldpSystemPolicy`), обнулены
оба. Если бы хардкодил имя — второе осталось бы CLM-cached, blocked
Roslyn/Sockets/etc.

Kernel `s_normalAppPath` → `\sharpos\PowerShellBootstrap.dll`.
Bootstrap → PS Main.

### Verification
```
[bootstrap] s_systemLockdownPolicy = None
[bootstrap] s_cachedWldpSystemPolicy = None
...
PS C:\sharpos> [Math]::Sqrt(16)
4
PS C:\sharpos> [Math]::Sin(13)
0.420167036829192
```

`Sin(13)` — runtime arg, не const-fold. Реальный вызов `lm_sin`
через link path (см. §3 ниже).

### Файлы
- `apps/PowerShellBootstrap/PowerShellBootstrap.csproj` (new)
- `apps/PowerShellBootstrap/Program.cs` (new) — ~60 строк reflection
- `run_build.ps1` — build+deploy PowerShellBootstrap.dll, TPA entry
- `OS/src/Kernel/Diagnostics/CoreClrProbe.cs` — `s_normalAppPath`
  теперь тернарник над двумя путями (см. §4)

## 2. Coverage honesty round

### 2.1 Kernel probe split
Оригинальный `Probe_Enum` (line 208 в `NativeAotProbe.cs`) отдавал
один bit-vector `enum: ok val=<sig>`. Прятало какой именно бит красный.
Разбит на три `ReportProbe` вызова:

```
enum.cast:     ok val=2      ← IL-level int↔enum
enum.bitwise:  ok val=5      ← | & ^
enum.toString: FAIL val=<n>  ← пустой Enum stub в MinimalRuntime
```

`probe_report.ps1` — три отдельные gates. `EnumToString` показывает
честный `FAIL`, никакой "expected FAIL" магии (был вариант с `OK
(FAIL)` — путал).

### 2.2 CoreCLR-hosted пробы (new)
В `work/normal-hello/Program.cs` — секция "7. LANG/BCL SURFACE" (13
проб для lock/ValueTuple/DateTime/TimeSpan/Enum) + 16 Math-проб.

Пример:
```csharp
Probe("lock re-entrancy same object", () => {
    object gate = new(); int hits = 0;
    lock (gate) { lock (gate) { hits++; } hits++; }
    if (hits != 2) throw new Exception($"hits={hits}");
});
Probe("Enum.HasFlag on flags-enum", () => {
    var a = FileAttributes.ReadOnly | FileAttributes.Hidden;
    if (!a.HasFlag(FileAttributes.ReadOnly)) throw ...;
    if (a.HasFlag(FileAttributes.Archive)) throw ...; // false-positive
});
Probe("Math.Atan(1) == π/4", () => {
    double r = Math.Atan(1.0);
    if (!Close(r, Math.PI/4.0, 1e-9)) throw new Exception($"{r}");
});
```

### 2.3 `lm_atan` баг раскопан
Пробы Math.* выявили real-value FAIL для Atan/Atan2/Tan/Exp/Pow/Cbrt.
Проверил `crt_imp_stubs.cpp:448` — `lm_atan` это **truncated Leibniz
ряд**:

```cpp
static double lm_atan(double x){
    int neg=0,inv=0; if(x<0){x=-x;neg=1;} if(x>1.0){x=1.0/x;inv=1;}
    double x2=x*x;
    double a = x*(1.0+x2*(-1/3 + x2*(1/5 + x2*(-1/7 + x2*(1/9 +
                x2*(-1/11 + x2*1/13))))));
    if(inv) a=1.5707963267948966-a; return neg?-a:a;
}
```

Ряд `atan(x) = x - x³/3 + x⁵/5 - ...` (Leibniz), 7 членов, x^13 max.
В x=1 (после reduction |x|>1 → 1/|x|) сходится **катастрофически
медленно**. Ручной расчёт формулы при x=1 даёт **ровно 0.8209** — то
что видим в логе.

`atan2` наследует (`lm_atan(y/x)`), `asin/acos` тоже.

`lm_exp/lm_pow/lm_cbrt` — заявленная точность 1e-9, реальная 1e-7
(compound error через `exp∘log`).

**Правильное решение** — Cody-Waite reduction + Remez полином (musl
libc / glibc). Часть libm-port шага (plan.md §6 Tier D).

Memory: `reference_lm_atan_leibniz_bug.md`, `reference_libm_
transcendentals_are_trap_stubs.md` обновлены (последний был устаревшим
"trap-stub" вердиктом; сейчас честно — "работает через приближения").

### 2.4 README rewrite
Три категории Math вместо одной:
- **`Math.Sqrt/Math.Abs` (SSE intrinsics)** ✅✅✅ — RyuJIT lower в
  `sqrtsd`/`andpd`, не libm вообще.
- **Транценденты** 🔴🔴🟡 — hosted **жёлтый** с пометкой "временные
  приближения кустарные Taylor-ряды"; kernel красный (не объявлено).
- **`Math.Floor/Ceiling/Truncate/Round`** 🔴🔴✅ — bit-manipulation
  IEEE 754, работают точно.

Новые честные строки: `lock`, `Enum` (частичный), `ValueTuple/
DateTime/TimeSpan` (одной группой).

## 3. Что откуда приезжает (для памяти)

Уточнено что `Math.Sin/Cos/etc` на hosted **не** идут через libcmt/
ucrt — резолвятся линкером `/FORCE:MULTIPLE` в **наши** `lm_*` из
fork'а `crt_imp_stubs.cpp` (which is our own code contributed to
vendored dotnet-runtime tree). При libm-port из fork → kernel C#
через `[RuntimeExport("sin")]` — одна имплементация будет обслуживать
и kernel и fork. См. `feedback_new_low_level_in_csharp_even_in_fork`.

## 4. Launch-target рычаг

`Probes.LaunchNormalHelloCensus` const bool:
- `false` → `\sharpos\PowerShellBootstrap.dll` (интерактивный pwsh)
- `true` → `\sharpos\NormalHello.dll` (census probe, exit 42)

`s_normalAppPath` тернарник:
```csharp
private static readonly byte[] s_normalAppPath =
    Probes.LaunchNormalHelloCensus ? s_appPathNormalHello : s_appPathPwsh;
```
ILC folds на compile-time, ноль runtime cost. Оба DLL всегда
собираются и деплоятся, оба в TPA — переключение чисто target'а
execute_assembly.

## 5. Не сделано / отложено

- **libm proper** (Cody-Waite + Remez полином вместо Taylor-рядов) —
  plan.md §6 Tier D, вместе с port'ом в kernel C#.
- **Kernel-AOT Math.* пробы** — типы не объявлены в std/no-runtime,
  probe не соберётся. Отложено до libm-port.
- **preemption / IST / L18 XMM / L19 CollidedUnwind / CSharpRepl /
  FAT RW** — остаются в backlog из предыдущих обсуждений.

## Файлы

Kernel:
- `OS/src/Kernel/Diagnostics/CoreClrProbe.cs` — s_appPathPwsh + s_appPathNormalHello + selector
- `OS/src/Kernel/Diagnostics/NativeAotProbe.cs` — Probe_Enum split into 3
- `OS/src/Kernel/Diagnostics/Probes.cs` — LaunchNormalHelloCensus flag
- `OS/src/Hal/Console.cs`, `OS/src/PAL/SharpOSHost/Diagnostics.cs` — minor tweaks

New app:
- `apps/PowerShellBootstrap/PowerShellBootstrap.csproj`
- `apps/PowerShellBootstrap/Program.cs`

Build:
- `run_build.ps1` — PowerShellBootstrap build+deploy+TPA

Tests / gates:
- `work/normal-hello/Program.cs` — 29 new probes (Sec "7. LANG/BCL SURFACE" + Math.*)
- `tools/probe_report.ps1` — EnumCast/Bitwise/ToString gates

Docs:
- `README.md` — table honesty pass (Math split, lock/Enum/ValueTuple/
  DateTime/TimeSpan rows, string.Format 🟡)
- `plan.md` — Tier D добавлен libm-port task, roadmap sync
- `done/step128.md` — this file

## Next step

Из backlog Tier A/B: IST → L18 → L19 → preemption → CSharpRepl → FAT RW.
Приоритетный — либо IST (страховка от silent triple-fault), либо
preemption (visible UX win — залип ввода). Решаем в step129.
