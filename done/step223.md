# step 223 — сжатие и криптография: SharpCompress, `System.IO.Compression`, `System.Security.Cryptography`

## Почему

Релиз 0.1, раздел 2 — форматы в std. Сжатие — по решению пользователя
«полностью вливать SharpCompress и делать на него прокидки из
System.IO.Compression». Библиотеке не хватило полутора десятков вещей std; по
правилу проекта они добавлены, а не вырезаны из библиотеки.

## Что сделано

- **SharpCompress 0.50.7 целиком** (`vendor/SharpCompress`, MIT, 874 файла):
  подключается одной строкой `SharpCompress.props`, ярус приложений. Вырезы —
  в `PROVENANCE.md`: лишние `using`, повторный `CLSCompliant`, имена томов по
  Regex (`SHARPOS_REGEX`), кодовые страницы имён (`SHARPOS_CODEPAGES`),
  `File.OpenHandle`, `[UnmanagedFunctionPointer]` у двух делегатов Zstd.
- **`System.IO.Compression` — BCL 8 как есть** (`std-no-runtime/IO/Compression`,
  входит через `SharpCompress.props`): `DeflateStream`, `GZipStream`,
  `ZLibStream`, `ZipArchive`, `ZipFile`. Заменён только нативный zlib: семь
  функций `Interop.ZLib` (`DeflateInit2_`, `Deflate`, `DeflateEnd`,
  `InflateInit2_`, `Inflate`, `InflateEnd`, `crc32`) — на C# поверх `ZlibCodec`
  SharpCompress (`Interop.ZLib.SharpCompress.cs`). Сверх кодека — то, что делает
  zlib: обёртка gzip (окна 24..31; 40..47 — gzip или zlib по первому байту),
  `Z_BUF_ERROR` без продвижения (кодек отвечает `Z_OK`, а `Deflater.Flush`
  крутится, пока слышит `Z_OK`), состояние за `GCHandle` в
  `z_stream.internalState`. `ZipFile` — обход каталога через
  `Directory.GetFileSystemEntries`, как Tar.
- **Криптография** (`std-no-runtime/Security/Cryptography`, оба яруса):
  `HashAlgorithm`, SHA-1/256/384/512, HMAC, `IncrementalHash`, PBKDF2, `Aes`,
  `CryptoStream` — управляемые пути BCL 8 (сборка для браузера). SHA-256 идёт в
  свою реализацию std (`Sha256HashProvider.SharpOS.cs`) — одна на систему,
  общая с мостом CoreCLR. Управляемого AES в .NET 8 нет: ядро —
  `RijndaelManagedTransform` из .NET Framework (microsoft/referencesource, MIT),
  подключён в `AesImplementation.SharpOS.cs`. MD5, SHA-3, `CryptoConfig` —
  `PlatformNotSupportedException`.
- **std, потоки и ожидание:** `Monitor.Wait`/`Pulse` (бросали `NotSupported`),
  `SemaphoreSlim`, `ConcurrentQueue`, `BlockingCollection`, `LazyInitializer`,
  `WaitHandle`/`ManualResetEvent`/`AutoResetEvent` над словом,
  `CancellationToken.WaitHandle`, `Task` как `IAsyncResult`,
  `Task.ConfigureAwait(ConfigureAwaitOptions)`, промис-конструктор `Task<T>`,
  `Thread(ThreadStart)`/`Start`/`Join` через `ThreadBackend.Spawn`,
  `Environment.TickCount`.
- **std, остальное:** `GCHandle` (своя таблица), `SafeHandle`, `NativeMemory`
  (массивы управляемой кучи), `MemoryStream` целиком из BCL 8,
  `Memory<T>.Pin`/`MemoryHandle`, `MemoryPool`, `IProgress`, `InlineArray`,
  `Tuple<T1,T2,T3>`, LINQ `GroupBy`/`ToLookup`, `Stream.BeginRead`/`EndRead`,
  таймауты `Stream`, `ReadAtLeast`, `RuntimeInformation`/`OSPlatform`,
  `Debugger`, `Encoding.CodePage`/`Default`, `Convert.ToInt64(object,
  provider)` с упакованным перечислением, `Buffer.BlockCopy` по любым массивам
  примитивов, `BitConverter.GetBytes`, `Path.GetPathRoot`/`GetInvalid*Chars`,
  `RuntimeImports.RhCheckArrayStore` (запись в `T[,]`), `CharSet`/
  `CallingConvention` из обоих `MinimalRuntime` в std, полный
  `MethodImplOptions` ядра. SDK: `FileInfo.Open*`, `DirectoryInfo.Enumerate*`,
  `FileStream.Name`, `AppContext.TryGetSwitch`, `Enum.IsDefined<T>`.
- **COMPRESSTEST** (`apps_native/CompressTest`, категория tests): векторы FIPS
  180/197, RFC 2202/4231/6070, NIST SP 800-38A, вывод ключа 7z против Python;
  три потока BCL всеми уровнями, рамки RFC 1950/1952, битый CRC →
  `InvalidDataException`; gzip и zip Python; `ZipArchive` ↔ SharpCompress в обе
  стороны; `ZipFile` через ФС; zip, 7z, tar.gz/bz2/xz/zst от bsdtar 3.8; пять
  тестовых архивов апстрима — WinZip AES, 7z LZMA/LZMA2 с AES и без, RAR5 с AES.

## Находки

- **Ошибка SharpCompress.** `DeflateStream` с `leaveOpen: true` при сжатии не
  пишет ничего: `Dispose` пропускал освобождение `ZlibBaseStream`, а только оно
  дописывает последний блок; `leaveOpen` и так соблюдает `ZlibBaseStream`.
  Агент по истории: регрессия 0.45.0 (`8df92321`, PR #1132, переработка async),
  есть по master; issue нет; для `GZipStream` то же исправлено в PR #1346.
  Повтор на .NET 10 с пакетом NuGet: 0 байт против 423. У нас — `SharpOS fix`
  в `Dispose` и `DisposeAsync`.
- **ILC падал `KeyNotFoundException`** в `CompilerGeneratedState` на обобщённом
  async-методе SharpCompress: в std не было `AsyncStateMachineAttribute` и
  `IteratorStateMachineAttribute`. Roslyn ставит их, только если типы есть, а
  ILC связывает машину состояний с методом только по ним. Раньше не всплывало:
  ни у кого не было обобщённого async-метода, чья машина создаёт обобщённое
  замыкание.
- **`[UnmanagedFunctionPointer]` роняет ILC:** с ним он строит заглушки
  маршалинга, которым нужен `InteropHelpers` NativeAOT. Атрибут из std убран —
  код, которому нужен маршалинг, теперь не собирается, а не падает в ILC.
- Ошибки тестов по дороге, не кода: `Length` у закрытого `MemoryStream`
  (трижды), сжатые tar через `ArchiveFactory` (у SharpCompress это Reader API),
  пароль 7z-архивов апстрима (`testpassword`). Зашифрованный zip от bsdtar
  SharpCompress не читает по своему ограничению (запись с дескриптором данных
  после данных) — заменён архивом апстрима.

## Проверка

Рабочая копия, WHPX, `-SkipCoreClr`. Батарея с тестами: `ran=42 failed=0` —
AOTTESTS 341/341 ×3, PROCTEST 85/0 и 80/0, PIPEPERF 20/0, JSONTEST 64/0,
DATATEST 239/0 и 236/0, GVMTEST 10/0, KQLTEST, COMPRESSTEST 44/44; `-NoTests`
6/6; предупреждений 0. Батарея поймала два приложения, которые std перерос:
TriCNES держал свою заглушку `GCHandle` (удалена — теперь std), LuaCSharp
объявляет публичный `HexConverter` (в `Helpers.Cryptography.cs` имя std
написано полностью); оба пересобраны отдельно, прогоном не проверялись.

## Не сделано

- Regex и кодовые страницы — следующие пункты раздела 2; вернут тома и имена в
  SharpCompress.
- Маршалинг делегатов (`InteropHelpers`), `SafeFileHandle`/`File.OpenHandle`,
  слабые ручки, финализаторы.
- AES постоянного времени (AES-NI есть в оптимистичном наборе ILC).
- Отправить исправление `DeflateStream` в апстрим — решение пользователя
  (donext).

## Файлы

vendor: `SharpCompress/` (новый: `src/`, `LICENSE.txt`, `PROVENANCE.md`,
`SharpCompress.props`, `.editorconfig`). std: `IO/Compression/**` (новые),
`Security/Cryptography/**` (новые, кроме `RandomNumberGenerator.cs`),
`Security/Sha256.cs`, `Collections/Concurrent/*` (новые), `Threading.*`,
`IO/MemoryStream.cs`, `IO/Stream.cs`, `IO/Hashing/Crc32.SharpOS.cs`,
`Runtime/InteropServices/*`, `Runtime/*` (Buffer, Convert, Span, ReadOnlySpan,
Array.LongLength, Debugger, Padding, MemoryMarshal.TryGetArray,
RuntimeImports.ArrayStore, CompilerServices), `Bcl/*` (Memory, ReadOnlyMemory,
List, Path, Tuple, BitConverter, ThrowHelper, Obsoletions, IProgress,
AsyncCallback, IAsyncResult), `Buffers/*`, `Linq/Grouping.cs`, `Linq/Lookup.cs`,
`Text/*`, `GC/GcRuntimeExports.cs`, `SR.*.cs`, `Std.props`. Ядро:
`OS/src/Boot/MinimalRuntime.cs`. SDK: `MinimalRuntime.cs`,
`FileSystem.AppHost.cs`, `FileSystemInfo.AppHost.cs`. Приложение:
`apps_native/CompressTest/` (новое). Документы: `README.md`, `release.md`,
`donext.md`, `docs/nativeaot-nostd-kernel-limits.md`, `docs/build-warnings.md`.
