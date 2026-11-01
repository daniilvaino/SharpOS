# step 221 — интринсики x86 и векторы BCL целиком, `System.IO.Hashing`

## Почему

Релиз 0.1, раздел 2 — форматы в std. Первым — `System.IO.Hashing`: он нужен zip
и tar. Векторный путь BCL идёт через `System.Runtime.Intrinsics.X86`
(`Pclmulqdq`, `Sse2`) и полный `Vector128`, которых в std не было; первая версия
порта обходила их `#undef NET`. Пользователь: «так это правильный путь вообще?»
— векторный путь надо добить, а не вырезать.

## Как NativeAOT выбирает инструкции

Без набора инструкций ILC 8 (`InstructionSetHelpers.cs`) берёт для x64 SSE2 как
гарантированный и «оптимистичный» набор: SSE3–4.2, AES, PCLMULQDQ, POPCNT,
LZCNT, MOVBE, SERIALIZE. Ветки `if (X.IsSupported)` оптимистичных наборов он
компилирует обе, а `IsSupported` заменяет на `(g_cpuFeatures & флаг) != 0`
(`HardwareIntrinsicHelpers.Aot.cs`); стоковый рантайм заполняет
`g_cpuFeatures` при старте из CPUID (`minipal_getcpufeatures`). AVX/AVX2 в
оптимистичный набор по умолчанию не входят — их `IsSupported` ILC сворачивает в
`false`. Поднимать целевой набор не нужно.

## Что сделано

- **`System.Runtime.Intrinsics.X86`** из CoreLib **.NET 8** (под наш ILC 8 —
  классы .NET 10 он не знает и скомпилировал бы их рекурсивные тела как есть):
  `X86Base`, `Sse`…`Sse42`, `Ssse3`, `Aes`, `Pclmulqdq`, `Popcnt`, `Lzcnt`,
  `Bmi1/2`, `Avx`, `Avx2`, `Fma`, `X86Serialize`, `AvxVnni`, `Enums`. AVX-512 нет:
  XCR0 ядра — x87/SSE/AVX, состояние AVX-512 не включено и не сохраняется
  (флаг Iced `NO_EVEX` к этому отношения не имеет — он о нашем кодировщике).
- **Arm и Wasm** — `*.PlatformNotSupported.cs`, как BCL собирает их под x64:
  `IsSupported = false`; общий код называет их в своих ветках.
  `SYSTEM_PRIVATE_CORELIB` — только в двух файлах Arm (классы публичные, как в
  CoreLib).
- **`Scalar`, `Vector64/128/256/512`** из CoreLib .NET 8 целиком — вместо порта
  step165 (там были только `Create`, сравнения, `Load`/`Store`, а запасные пути
  отказывали). Вырез: преобразования в `System.Numerics.Vector<T>`,
  `Vector2–4`, `Plane`, `Quaternion` (`#if SHARPOS_NUMERICS_VECTORS`) — этих
  типов в std нет.
- **`g_cpuFeatures`.** Флаги считает ядро: порт `minipal_getcpufeatures`
  (`std/Runtime/HardwareIntrinsicFlags.Compute`) — только ядро знает XCR0.
  `X86Base.CpuId` в ядре — через `X64Asm.Cpuid` (указатель
  `HardwareIntrinsicFlags.CpuId`), в приложении — `PlatformNotSupportedException`.
  Ядро отдаёт флаги полем `AppServiceTable.IntrinsicFlags` (хвост, без смены
  ABI). Приложение пишет их в свой `g_cpuFeatures`: символ — `[CoffDataSymbol]`
  в `WinCrtStubs`, `/EXPORT:g_cpuFeatures,DATA`, адрес — по таблице экспорта
  собственного образа (`IntrinsicFlagsInstaller`) в начале
  `AppRuntime.Initialize`. Управляемый код не может назвать нативный символ
  данных, а `[CoffDataSymbol]` — отдельная память, не поле.
- **`System.IO.Hashing`** из BCL .NET 8 как есть, с векторным путём: CRC32/64 —
  PCLMULQDQ, где он есть; xxHash32/64/3/128. Вырез: `XxHash128.HashToUInt128` и
  `GetCurrentHashAsUInt128` — в std нет `UInt128` (он тянет обобщённую
  математику BCL).
- **std:** `Stream.CopyToAsync` (BCL 8, без `ConfigureAwait`),
  `RequiresPreviewFeaturesAttribute`, `CompExactlyDependsOnAttribute`, члены
  `ThrowHelper` для векторов, `Unsafe.ReadUnaligned(ref readonly byte)` — как в
  BCL 8.
- **`.gitignore`:** правило `x86/` из шаблона Visual Studio прятало
  `Intrinsics/X86/` (на Windows без учёта регистра), `[Aa][Rr][Mm]/` спрятало
  бы `Arm/` — исключения добавлены.

## Проверка

Рабочая копия, WHPX, `-SkipCoreClr`.

- Детектор (AOTTESTS): qemu64 по умолчанию — флаги ядра `0x4000C04`,
  `Pclmulqdq.IsSupported` false; `-Cpu …,+pclmulqdq,+sse4.1,+sse4.2,+popcnt` —
  `0x4000C7E`, `IsSupported` true, PCLMULQDQ `3 × 3` без переносов = 5. Значения
  Hashing (CRC-32 `0xCBF43926`, CRC-64 `0x6C40DF5F0B497347`, xxHash на пустом
  входе, по кускам, из потока, `AppendAsync`) верны на обоих.
- Полная батарея на пересобранных приложениях: `ran=41 failed=0`, AOTTESTS
  330/330 ×3, DATATEST 239/0 и 236/0, PROCTEST 85/0 и 80/0, KQLTEST 7/0 и 9/0,
  PIPEPERF 20/0, JSONTEST 64/0, GVMTEST 10/0; проба ядра `[xml] vector arith`
  (TurboXml на `Vector128`) PASS. `-NoTests` 6/6. Предупреждений компилятора 0.

## Не сделано

- `System.Numerics.Vector<T>`, `Vector2/4`, `Plane`, `Quaternion` в std — тогда
  вернуть вырезанные преобразования.
- `UInt128` — с обобщённой математикой BCL.
- `X86Base.CpuId` в приложении: служба ядра или шеллкод.
- AVX/AVX2 для ILC: целевой набор или оптимистичность — решение отдельное.

## Файлы

std: `Runtime/Intrinsics/{Scalar,Vector64*,Vector128*,Vector256*,Vector512*}.cs`,
`Runtime/Intrinsics/X86/*.cs` (новые, `X86Base.SharpOS.cs`),
`Runtime/Intrinsics/Arm/*.cs`, `Runtime/Intrinsics/Wasm/*.cs`,
`Runtime/HardwareIntrinsicFlags.cs`, `Runtime/RequiresPreviewFeaturesAttribute.cs`,
`Runtime/CompilerServices/CompExactlyDependsOnAttribute.cs`, `Runtime/Unsafe.cs`,
`Bcl/ThrowHelper.Intrinsics.cs`, `Bcl/ThrowHelper.CoreLib.cs`,
`IO/Hashing/*.cs`, `IO/Stream.cs`, `Std.props`. Ядро: `Hal/CpuFeatures.cs`,
`Kernel/Process/AppServiceTable.cs`, `Kernel/Process/AppServiceBuilder.cs`.
SDK: `AppRuntime.cs`, `AppServiceTable.cs`, `WinCrtStubs.cs`,
`IntrinsicFlagsInstaller.cs` (новый), `FreestandingPe.props`. AOTTESTS:
`FormatTests.cs` (новый), `Program.cs`, `.csproj`. `.gitignore`, `release.md`,
`donext.md`, `docs/nativeaot-nostd-kernel-limits.md`.
