namespace OS.Kernel.Process
{
    internal enum AppServiceStatus : uint
    {
        Ok = 0,
        NoData = 1,
        NotFound = 2,
        EndOfDirectory = 3,
        BufferTooSmall = 4,
        InvalidParameter = 5,
        Unsupported = 6,
        DeviceError = 7,
    }

    internal unsafe struct AppServiceTable
    {
        public const uint AbiVersionV1 = 1;
        public const uint AbiVersionV2 = 2;
        public const uint CurrentAbiVersion = AbiVersionV2;
        public const uint AutoSelectAbiVersion = 0xFFFFFFFF;

        public uint AbiVersion;
        public uint Reserved;
        public ulong WriteStringAddress;
        public ulong WriteUIntAddress;
        public ulong WriteHexAddress;
        public ulong GetAbiVersionAddress;
        public ulong ExitAddress;
        public ulong FileExistsAddress;
        public ulong ReadFileAddress;
        public ulong ReadDirEntryAddress;
        public ulong TryReadKeyAddress;
        public ulong RunAppAddress;
        public ulong WriteCharAddress;
        public ulong WriteBuildIdAddress;
    }

    internal unsafe struct AppFileExistsRequest
    {
        public ulong PathAddress;
    }

    internal unsafe struct AppReadFileRequest
    {
        public ulong PathAddress;
        public ulong BufferAddress;
        public uint BufferCapacity;
        public uint BytesRead;
    }

    internal unsafe struct AppReadDirectoryEntryRequest
    {
        public ulong DirectoryPathAddress;
        public uint EntryIndex;
        public uint Reserved0;
        public ulong NameBufferAddress;
        public uint NameBufferCapacity;
        public uint NameLength;
        public uint IsDirectory;
        public uint Reserved1;
    }

    internal unsafe struct AppReadKeyRequest
    {
        public ushort UnicodeChar;
        public ushort ScanCode;
        public uint Reserved;
    }

    internal unsafe struct AppRunAppRequest
    {
        public ulong PathAddress;
        public uint AppAbiVersion;
        public uint ServiceAbi;
        public int ExitCode;
        public uint Reserved;
    }
}
