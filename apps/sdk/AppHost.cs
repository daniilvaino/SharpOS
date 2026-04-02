namespace SharpOS.AppSdk
{
    internal static unsafe class AppHost
    {
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
            return TryRunApp(path, AppStartupBlock.AbiVersionV1, AppServiceAbi.WindowsX64, out exitCode);
        }

        private static bool HasAbiV2Services(AppServiceTable* services)
        {
            return services != null && services->AbiVersion >= AppServiceTable.AbiVersionV2;
        }
    }
}
