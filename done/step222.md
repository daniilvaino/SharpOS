# step 222 — `System.Formats.Tar`, файловый API SDK по форме BCL

## Почему

Релиз 0.1, раздел 2 — форматы в std. Tar после Hashing: дальше сжатие
(SharpCompress), а `.tar.gz` — оба вместе. Tar потребовал того, чего у SDK не
было: общего `FileSystemInfo`, `FileStreamOptions`, установки времени файла.
По правилу проекта — добить, а не вырезать.

## Что сделано

- **`System.Formats.Tar`** из BCL .NET 8, на ярусе приложений
  (`Std.App.props`: `File`/`Directory` есть только там). Платформенная часть —
  файлы Windows: прав Unix у FAT нет, атрибуты есть. `OperatingSystem.IsWindows()`
  у нас false, поэтому общий код идёт веткой Unix — каталог с режимом создаётся,
  режим не хранится (как Linux на vfat). Вырезы `SharpOS cut:`:
  - `ParseOctal<T>` без `INumber<T>` — в std нет обобщённой математики; Tar
    зовёт его только с `uint`/`ulong`, значение собирается в `ulong` с той же
    проверкой переполнения;
  - `FileSystemEnumerable` → `Directory.GetFileSystemEntries` (у FAT нет
    ссылок, всякий каталог — настоящий);
  - жёсткая ссылка при распаковке — копия цели (`CreateHardLink`);
  - `System.Text.Rune` названа полностью: порт Terminal.Gui объявляет свой
    `System.Rune`, и лаунчер не собирался.
  `SR.Tar.cs` — тексты `Strings.resx` Tar, сгенерированы.
- **SDK, файловый API:**
  - `FileSystemInfo` — общий предок `FileInfo`/`DirectoryInfo`, как в BCL:
    `Attributes`, времена, `LinkTarget` (null), `UnixFileMode`
    (`PlatformNotSupportedException`, как BCL на Windows), `Refresh`. Время
    создания и доступа драйвер не отдаёт — читается время записи.
    `ToString` — путь, как его дали (`OriginalPath`), как в BCL.
  - `File`/`Directory.SetLastWriteTime(Utc)`, `GetLastWriteTimeUtc`: ядро —
    `Fat32.SetWriteTime`, служба `FileManage` оп 7 (штамп в `result[0]`). FAT
    хранит местное время с шагом две секунды.
  - `FileOptions`, `FileStreamOptions`, `UnixFileMode`; конструкторы
    `FileStream` с `FileShare`/`bufferSize`/`FileOptions`/`FileStreamOptions`
    (`DeleteOnClose` удаляет при `Dispose`).
  - `Directory.CreateDirectory(path, UnixFileMode)`, `Path.Exists`,
    `Environment.ProcessId`.
- **std:** `Stream` — `IAsyncDisposable`/`DisposeAsync`, `ReadExactlyAsync`,
  `ValidateBufferArguments`; `CopyToAsync` с `ConfigureAwait(false)`, как в BCL
  (вырез step221 был лишним — `ConfigureAwait` в std есть с step198).
  `PathInternal` (Common, Unix; сравнение без учёта регистра — FAT). `Path` —
  `Join`, спан-перегрузки `GetFileName`/`GetDirectoryName`,
  `TrimEndingDirectorySeparator`, `EndsInDirectorySeparator`, `GetTempPath`.
  `MemoryExtensions` — `IndexOfAnyExcept(a, b)`, `LastIndexOfAny(values)`;
  `SR.Format(string, params object[])`.

## Проверка

Рабочая копия, WHPX, `-SkipCoreClr`. AOTTESTS (`FormatTests.CheckTar`):
- архивы GNU tar 1.35 с машины сборки (`AotTests/esp/sample-{gnu,pax}.tar`,
  путь длиннее ustar-ских 100 байт, время 2024-01-02 03:04:05 UTC) — записи,
  содержимое, время;
- записан здесь и прочитан обратно (PAX, 3000 байт, время);
- каталог → `TarFile.CreateFromDirectory` → `ExtractToDirectory` и
  `ExtractToDirectoryAsync` через ФС;
- `SetLastWriteTime` читается обратно; распакованный файл получает время
  записи — 03:04:04, шаг FAT две секунды.

Первая батарея поймала две ошибки тестов: время ожидалось точным (FAT
округляет вниз), и два AOTTESTS, которые PROCTEST запускает разом, делили
один каталог. Исправлено. Батарея после исправления: `ran=41 failed=1` —
PROCTEST 15 (порог ¼ от нагрузки хоста); AOTTESTS 341/341 ×3, DATATEST 239/0
и 236/0, PROCTEST 80/0, KQLTEST, PIPEPERF, JSONTEST, GVMTEST зелёные;
`-NoTests` 6/6; предупреждений 0.

## Не сделано

- `FileSystemEnumerable` (`System.IO.Enumeration`) и ленивые `Enumerate*`.
- Времена создания и доступа: драйвер их пишет (`UpdateEntry`), но не отдаёт.
- Права Unix и ссылки — нет у FAT.

## Файлы

std: `Formats/Tar/*.cs` (новые, `SR.Tar.cs`), `IO/PathInternal.cs` (новый),
`IO/Stream.cs`, `Bcl/Path.cs`, `Runtime/MemoryExtensions.Search.cs`,
`Number/SR.cs`, `Std.App.props`, `Std.props`. Ядро: `Hal/Fat32Remove.cs`,
`Kernel/Process/AppServiceBuilder.Files.cs`. SDK: `FileSystemInfo.AppHost.cs`,
`FileSystem.AppHost.cs`, `FileOptions.AppHost.cs` и `Path.AppHost.cs` (новые),
`Environment.AppHost.cs`, `AppFile.cs`, `FreestandingPe.props`. AOTTESTS:
`FormatTests.cs`, `esp/sample-gnu.tar`, `esp/sample-pax.tar`. Документы:
`release.md`, `README.md`, `docs/nativeaot-nostd-kernel-limits.md`.
