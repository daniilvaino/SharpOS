namespace SharpOS.AppSdk
{
    internal static unsafe class AppHost
    {
        private const int MaxTempTextChars = 512;
        private const int MaxTempPathChars = 260;

        public static void WriteString(byte* text)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null || text == null)
                return;

            delegate* unmanaged<ulong, void> write = (delegate* unmanaged<ulong, void>)services->WriteStringAddress;
            if (write == null)
                return;

            write((ulong)text);
        }

        public static void WriteString(string text)
        {
            if (text == null || text.Length == 0)
                return;

            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            delegate* unmanaged<uint, void> writeChar = (delegate* unmanaged<uint, void>)services->WriteCharAddress;
            if (writeChar != null)
            {
                fixed (char* source = text)
                {
                    for (int i = 0; i < text.Length; i++)
                        writeChar((uint)source[i]);
                }
                return;
            }

            // Fallback: ASCII-only path (WriteChar not available)
            byte* buffer = stackalloc byte[MaxTempTextChars];
            if (!TryEncodeAscii(text, buffer, MaxTempTextChars, out _))
                return;

            WriteString(buffer);
        }

        public static void WriteBuildId()
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            delegate* unmanaged<void> writeBuildId = (delegate* unmanaged<void>)services->WriteBuildIdAddress;
            if (writeBuildId == null)
                return;

            writeBuildId();
        }

        public static void WriteChar(char c)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            delegate* unmanaged<uint, void> writeChar = (delegate* unmanaged<uint, void>)services->WriteCharAddress;
            if (writeChar == null)
                return;

            writeChar((uint)c);
        }

        public static void WriteUInt(uint value)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            delegate* unmanaged<uint, void> write = (delegate* unmanaged<uint, void>)services->WriteUIntAddress;
            if (write == null)
                return;

            write(value);
        }

        public static void WriteHex(ulong value)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            delegate* unmanaged<ulong, void> write = (delegate* unmanaged<ulong, void>)services->WriteHexAddress;
            if (write == null)
                return;

            write(value);
        }

        public static uint GetAbiVersion()
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return 0;

            delegate* unmanaged<uint> getVersion = (delegate* unmanaged<uint>)services->GetAbiVersionAddress;
            if (getVersion == null)
                return 0;

            return getVersion();
        }

        public static void Exit(int exitCode)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            delegate* unmanaged<int, void> exit = (delegate* unmanaged<int, void>)services->ExitAddress;
            if (exit == null)
                return;

            exit(exitCode);
        }

        public static bool FileExists(byte* path)
        {
            return FileExistsEx(path) == AppServiceStatus.Ok;
        }

        public static bool FileExists(string path)
        {
            return FileExistsEx(path) == AppServiceStatus.Ok;
        }

        public static AppServiceStatus FileExistsEx(byte* path)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (path == null)
                return AppServiceStatus.InvalidParameter;

            if (!HasAbiV2Services(services))
                return AppServiceStatus.Unsupported;

            delegate* unmanaged<ulong, uint> fileExists = (delegate* unmanaged<ulong, uint>)services->FileExistsAddress;
            if (fileExists == null)
                return AppServiceStatus.Unsupported;

            AppFileExistsRequest request = default;
            request.PathAddress = (ulong)path;
            return (AppServiceStatus)fileExists((ulong)(&request));
        }

        public static AppServiceStatus FileExistsEx(string path)
        {
            byte* pathBuffer = stackalloc byte[MaxTempPathChars + 1];
            if (!TryEncodeAscii(path, pathBuffer, MaxTempPathChars + 1, out _))
                return AppServiceStatus.InvalidParameter;

            return FileExistsEx(pathBuffer);
        }

        public static AppServiceStatus TryReadFile(byte* path, byte* buffer, uint bufferCapacity, out uint bytesRead)
        {
            bytesRead = 0;

            AppServiceTable* services = AppRuntime.Services;
            if (path == null || buffer == null || bufferCapacity == 0)
                return AppServiceStatus.InvalidParameter;

            if (!HasAbiV2Services(services))
                return AppServiceStatus.Unsupported;

            delegate* unmanaged<ulong, uint> readFile = (delegate* unmanaged<ulong, uint>)services->ReadFileAddress;
            if (readFile == null)
                return AppServiceStatus.Unsupported;

            AppReadFileRequest request = default;
            request.PathAddress = (ulong)path;
            request.BufferAddress = (ulong)buffer;
            request.BufferCapacity = bufferCapacity;

            AppServiceStatus status = (AppServiceStatus)readFile((ulong)(&request));
            bytesRead = request.BytesRead;
            return status;
        }

        public static AppServiceStatus TryReadFile(string path, byte* buffer, uint bufferCapacity, out uint bytesRead)
        {
            byte* pathBuffer = stackalloc byte[MaxTempPathChars + 1];
            if (!TryEncodeAscii(path, pathBuffer, MaxTempPathChars + 1, out _))
            {
                bytesRead = 0;
                return AppServiceStatus.InvalidParameter;
            }

            return TryReadFile(pathBuffer, buffer, bufferCapacity, out bytesRead);
        }

        public static AppServiceStatus TryReadDirEntry(
            byte* directoryPath,
            uint entryIndex,
            byte* nameBuffer,
            uint nameBufferCapacity,
            out FileEntry entry)
        {
            entry = default;

            AppServiceTable* services = AppRuntime.Services;
            if (directoryPath == null || nameBuffer == null || nameBufferCapacity == 0)
                return AppServiceStatus.InvalidParameter;

            if (!HasAbiV2Services(services))
                return AppServiceStatus.Unsupported;

            delegate* unmanaged<ulong, uint> readDirEntry = (delegate* unmanaged<ulong, uint>)services->ReadDirEntryAddress;
            if (readDirEntry == null)
                return AppServiceStatus.Unsupported;

            AppReadDirectoryEntryRequest request = default;
            request.DirectoryPathAddress = (ulong)directoryPath;
            request.EntryIndex = entryIndex;
            request.NameBufferAddress = (ulong)nameBuffer;
            request.NameBufferCapacity = nameBufferCapacity;

            AppServiceStatus status = (AppServiceStatus)readDirEntry((ulong)(&request));
            entry.NameLength = request.NameLength;
            entry.IsDirectory = request.IsDirectory;
            return status;
        }

        public static AppServiceStatus TryReadDirEntry(
            string directoryPath,
            uint entryIndex,
            byte* nameBuffer,
            uint nameBufferCapacity,
            out FileEntry entry)
        {
            byte* pathBuffer = stackalloc byte[MaxTempPathChars + 1];
            if (!TryEncodeAscii(directoryPath, pathBuffer, MaxTempPathChars + 1, out _))
            {
                entry = default;
                return AppServiceStatus.InvalidParameter;
            }

            return TryReadDirEntry(pathBuffer, entryIndex, nameBuffer, nameBufferCapacity, out entry);
        }

        public static AppServiceStatus TryReadKey(out KeyInfo key)
        {
            key = default;

            AppServiceTable* services = AppRuntime.Services;
            if (!HasAbiV2Services(services))
                return AppServiceStatus.Unsupported;

            delegate* unmanaged<ulong, uint> tryReadKey = (delegate* unmanaged<ulong, uint>)services->TryReadKeyAddress;
            if (tryReadKey == null)
                return AppServiceStatus.Unsupported;

            AppReadKeyRequest request = default;
            AppServiceStatus status = (AppServiceStatus)tryReadKey((ulong)(&request));
            key.UnicodeChar = request.UnicodeChar;
            key.ScanCode = request.ScanCode;
            return status;
        }

        public static AppServiceStatus TryRunApp(
            byte* path,
            uint appAbiVersion,
            AppServiceAbi serviceAbi,
            out int exitCode)
        {
            exitCode = 0;

            AppServiceTable* services = AppRuntime.Services;
            if (path == null)
                return AppServiceStatus.InvalidParameter;

            if (!HasAbiV2Services(services))
                return AppServiceStatus.Unsupported;

            delegate* unmanaged<ulong, uint> runApp = (delegate* unmanaged<ulong, uint>)services->RunAppAddress;
            if (runApp == null)
                return AppServiceStatus.Unsupported;

            AppRunAppRequest request = default;
            request.PathAddress = (ulong)path;
            request.AppAbiVersion = appAbiVersion;
            request.ServiceAbi = (uint)serviceAbi;
            request.ExitCode = 0;

            AppServiceStatus status = (AppServiceStatus)runApp((ulong)(&request));
            exitCode = request.ExitCode;
            return status;
        }

        public static AppServiceStatus TryRunApp(byte* path, out int exitCode)
        {
            return TryRunApp(path, AppServiceTable.AutoSelectAbiVersion, AppServiceAbi.Auto, out exitCode);
        }

        public static AppServiceStatus TryRunApp(
            string path,
            uint appAbiVersion,
            AppServiceAbi serviceAbi,
            out int exitCode)
        {
            byte* pathBuffer = stackalloc byte[MaxTempPathChars + 1];
            if (!TryEncodeAscii(path, pathBuffer, MaxTempPathChars + 1, out _))
            {
                exitCode = 0;
                return AppServiceStatus.InvalidParameter;
            }

            return TryRunApp(pathBuffer, appAbiVersion, serviceAbi, out exitCode);
        }

        public static AppServiceStatus TryRunApp(string path, out int exitCode)
        {
            return TryRunApp(path, AppServiceTable.AutoSelectAbiVersion, AppServiceAbi.Auto, out exitCode);
        }

        public static bool TryEncodeAscii(string text, byte* destination, int destinationCapacity, out uint bytesWritten)
        {
            bytesWritten = 0;
            if (destination == null || destinationCapacity <= 0 || text == null)
                return false;

            int textLength = text.Length;
            if (textLength + 1 > destinationCapacity)
                return false;

            fixed (char* source = text)
            {
                for (int i = 0; i < textLength; i++)
                {
                    char value = source[i];
                    destination[i] = value <= 0x7F ? (byte)value : (byte)'?';
                }
            }

            destination[textLength] = 0;
            bytesWritten = (uint)textLength;
            return true;
        }

        private static bool HasAbiV2Services(AppServiceTable* services)
        {
            return services != null && services->AbiVersion >= AppServiceTable.AbiVersionV2;
        }
    }
}
