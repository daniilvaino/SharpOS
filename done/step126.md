# step126 — PowerShell cosmetics + memory plumbing + policy probes

## TL;DR

После step125 (cmdlets зарегистрированы и реально запускаются) — раунд
полировки: убрали двойное стирание на Backspace, добавили ANSI-цвета и
очистку экрана на framebuffer-консоли, размер файлов в `ls`, заработал
`cat <file>`. Параллельно вытащили два настоящих корня: (1) CoreCLR
LoaderHeap ловил `0xAFAFAFAF` poison потому что наши MEM_COMMIT
страницы не были zero-filled; (2) `PhysicalMemory` был bump-only, а
`VirtualMemory.Decommit` — no-op, поэтому длинная PS-сессия с 2 GiB VM
упиралась в OOM. Обе подсистемы переписаны. Попытка убрать
ConstrainedLanguage Mode — фейл, оставляем как известное ограничение
(объяснение ниже).

## Что починили

### 1. FbTty — ANSI-парсер на framebuffer

`OS/src/Hal/FbTty.cs` — добавлена state machine `Normal/Esc/Csi`,
маппинг SGR-кодов (30-37 + 90-97 bright) в нашу палитру, поддержка:

- `ESC[2J` — clear screen (для `Clear-Host` / `cls`)
- `ESC[H` — cursor home
- `ESC[<N>D` — cursor left N (нужно prompt'у)
- `ESC[K` — erase EOL
- `ESC[<n>m` — SGR (FG/BG, bold, reset)

До правки PS sequences прорывались сырыми ESC-байтами в UART лог, а на
framebuffer оседали как `^[[31m` — некрасиво.

### 2. ConsoleRead — Backspace стирает один символ

`OS/src/PAL/SharpOSHost/ConsoleRead.cs` эмиттил **обе** последовательности:
`BS-space-BS` **и** `ESC[D space ESC[D` — терминал интерпретировал
обе, символов стиралось два. Оставили только `BS-space-BS`. Заодно
добавили `Scheduler.Yield()` в busy-wait — простой цикл `while
(NoScancode)` хог'ал CPU и тормозил остальные таски.

### 3. Fat32 — размер файла в EnumDir

`OS/src/Hal/Fat32.cs` теперь пакует размер файла в верхние 32 бита
`attrs`:

```csharp
uint size = (uint)ent[28] | ((uint)ent[29] << 8) | ((uint)ent[30] << 16) | ((uint)ent[31] << 24);
attrs = (ulong)(ent[11] & 0x10) | ((ulong)size << 32);
```

`FileSystemQuery.SharpOSHost_FindDirEntry` сигнатура расширена `uint*
outSize`. PS теперь видит размер в `ls`.

### 4. NtQueryDirectoryFile / GetFileInformationByHandleEx

`crt_imp_stubs.cpp`:

- `CreateFileW` детектит `FILE_FLAG_BACKUP_SEMANTICS` (0x02000000) →
  возвращает `DirHandle` (sanity-range 0x100000..0x800000000000)
- `NtQueryDirectoryFile` пакует `FILE_FULL_DIR_INFORMATION` с настоящим
  `EndOfFile`/`AllocationSize` (раньше всегда 0 → BCL верил что файлы
  нулевые → `cat` возвращал пусто)
- `GetFileInformationByHandleEx` обслуживает `FileBasicInfo` /
  `FileStandardInfo` / `FileAttributeTagInfo`

После этого `cat C:\sharpos\readme.txt` реально читает контент.

### 5. ZeroPage в VirtualMemory.Commit

`OS/src/Kernel/Memory/VirtualMemory.cs` — добавили `ZeroPage(va)`
вызываемый в-цикле в `Commit` для **только-что-маппеных** страниц
(`!TryQueryKernel` ветка) и аналогично в `TryDemandCommit` после TLB
flush'а.

**Корень:** CoreCLR LoaderHeap по контракту Win32 `MEM_COMMIT`
рассчитывает что свежепокомиченная память — нули. Без zero-fill поле
`Module::m_pAvailableClasses` читалось как `0xAFAFAFAFAFAFAFAF`
(slab-freelist poison из предыдущего жителя физической рамки), CoreCLR
интерпретировал его как `EEClassHashTable*` и падал в AV при первом
`Assembly.GetExportedTypes()`.

### 6. PhysicalMemory — freelist

`OS/src/Kernel/PhysicalMemory.cs` — добавили `s_freeList[FreeListCapacity
= 256*1024]` (2 MiB массив), `FreePage(pa)` push'ит на freelist,
`AllocPage` сначала pop'ит, fallback к bump cursor'у. `VirtualMemory.
Decommit` теперь реально:

```csharp
for each page: TryQueryKernel(pa) → Unmap → FreePage(pa)
```

`Release == Decommit`. До этого PS с `-m 2048` упиралась в OOM посередине
runspace bootstrap — Module loading + JIT method chunks + GC chunks
накапливали невозвратные коммиты.

### 7. HandleTable — 4096 + диагностика

`OS/src/PAL/SharpOSHost/HandleTable.cs`: cap 256 → 4096, добавлены
счётчики `s_allocTotal/s_freeTotal/s_liveHighWater/s_allocFailures`,
`DumpStats` с per-type гистограммой (bypass `Console.Quiet` через
`Platform.WriteChar`). До правки PS-bootstrap зажирал 256 хэндлов на
`CreateEventEx` и `HT-Alloc-OOM` ловил `CreateEventEx` → NULL → halt.

### 8. Память VM 2 GiB

`run_build.ps1` / `run_vbox.ps1` — default 2048 MiB (было 512).
Реально требуется ~800 MiB на PS bootstrap + JIT всех модулей до
prompt'а.

## Что **не** починили — CLM остаётся

`[Math]::Sqrt(16)` всё ещё блокируется ConstrainedLanguage Mode.

Попытки (не сработали):

1. `WldpGetLockdownPolicy → DEFINED_FLAG` — PS не верит одному
   источнику, проверяет дальше.
2. `__PSLockdownPolicy="0"` env var через `EnvironmentPolicy.cs` — в
   PS 7.5 этот канал **удалён** (был security risk).
3. `WinVerifyTrust → S_OK` — сломал `TryGetProviderSigner` (тот ждёт
   объект-сигнатуру).
4. `WinVerifyTrust → TRUST_E_NOSIGNATURE` — PS интерпретировал как
   "unsigned untrusted" → CLM.
5. `SHGetKnownFolderPath → S_OK + "C:\sharpos"` — **сделал хуже**:
   `SystemPolicy::GetAppLockerPolicy` ушла рекурсивно в
   `SaferIdentifyLevel`, получила несогласованный handle, кинула
   `SecurityException(0x80070006)` который никто не ловит → `FailFast`
   → triple halt. Откатили на E_FAIL — PS живой, но CLM.

**Цепочка решения CLM в PS 7.5:**

```
GetSystemLockdownPolicy → GetLockdownPolicy → GetAppLockerPolicy
  ├─ WldpGetLockdownPolicy
  ├─ WldpQueryWindowsLockdownMode
  ├─ WldpCanExecuteFile
  ├─ SaferIdentifyLevel
  ├─ WinVerifyTrust
  ├─ SHGetKnownFolderPath
  └─ Environment.GetEnvironmentVariable("__PSLockdownPolicy") ← убран в 7.5
```

В PS 5.1 был один master switch (env var). В 7.5 решение распределено
по 5+ источникам и **PS смотрит на консенсус**: если хоть один сигнал
"что-то странное" → fail-secure → CLM. Любая попытка форсировать
FullLanguage через несогласованную пятёрку стабов либо игнорируется
(CLM), либо ломает `TryGetProviderSigner`/`SaferIdentify` →
`SecurityException` → FailFast.

**Правильное решение (отдельная задача):**

- **A.** Прогнать PS под отладкой, заfix'ить **каждую** ветку
  `SystemPolicy::GetAppLockerPolicy` так чтобы она консистентно ушла в
  FullLanguage. Risk: ~день работы, может всплыть SaferIdentifyLevel
  handle-protocol сюрприз.
- **B.** Managed-инжект `SystemPolicy.s_systemLockdownPolicy =
  SystemEnforcementMode.None` через reflection до первого
  `InitialSessionState.CreateDefault2` — обход всей probe-машинерии.

Откладываем до отдельного шага.

## Файлы

Kernel side:

- `OS/src/Hal/FbTty.cs` — ANSI parser
- `OS/src/Hal/Fat32.cs` — file size в EnumDir
- `OS/src/Hal/Console.cs` — gate tweaks
- `OS/src/Kernel/Memory/VirtualMemory.cs` — ZeroPage + real Decommit
- `OS/src/Kernel/PhysicalMemory.cs` — freelist
- `OS/src/PAL/SharpOSHost/ConsoleRead.cs` — single-erase BS + Yield
- `OS/src/PAL/SharpOSHost/HandleTable.cs` — cap 4096 + stats
- `OS/src/PAL/SharpOSHost/FileSystemQuery.cs` — outSize param
- `OS/src/PAL/SharpOSHost/ShellFolders.cs` — back to E_FAIL (the lesser evil)
- `OS/src/PAL/SharpOSHost/LockdownPolicy.cs` — WLDP stubs
- `OS/src/PAL/SharpOSHost/EnvironmentPolicy.cs` — **new**, kernel-side
  env var matcher per "logic in kernel" invariant
- `OS/src/PAL/SharpOSHost/Diagnostics.cs` — TraceGate per-category

Fork side:

- `dotnet-runtime-sharpos/src/coreclr/pal/sharpos/crt_imp_stubs.cpp` —
  WinVerifyTrust / NtQueryDirectoryFile / CreateFileW BACKUP_SEMANTICS
  / GetFileInformationByHandleEx / GetEnvironmentVariableW marshalling
  / sharpos_maybe_clear_screen / OEMCP/ACP/ConsoleCP stubs

Build:

- `run_build.ps1` — `-m 2048`, pwsh Modules deploy, TPA, tmp/system32
  dirs, C:\sharpos paths
- `run_vbox.ps1` — MemoryMb default 2048

## Что отложено

1. **CLM → FullLanguage** — отдельный шаг, ~день работы.
2. **Perf investigation** — следующий шаг. User отметил подозрительно
   медленный pwsh prompt vs Windows на той же QEMU. Гипотезы: (a)
   fork↔kernel string marshalling overhead (wchar↔UTF-8 на каждом
   Win32 API call), (b) HandleTable linear scan, (c) precise GC
   scanning при каждом JIT-helper call, (d) AOT хойстит MMIO-poll
   так что Yield() не yield'ит вовремя.
3. **VBox verification** — limits-таблицы и README не трогаем до
   проверки на bare metal (VBox). User не хочет коммитить таблицы под
   QEMU-only.

## Next step

step127 — perf investigation. Начинать с замера: bootstrap-to-prompt
timing breakdown через `Stopwatch` в KernelHeap/VirtualMemory/HandleTable
hot paths, ИЛИ tracing через QEMU `-d` (instruction count). Без цифр
гипотезы не валидируются.
