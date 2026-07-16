namespace SharpOS.AppSdk
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

        // Kernel interface-dispatch bridge shellcode entry (raw address, not an
        // ABI thunk). The app patches its own RhpInitialDynamicInterfaceDispatch
        // stub with `mov rax,<this>; jmp rax` so interface dispatch resolves via
        // the kernel's shared resolver. See InterfaceDispatchTrampoline. Filled
        // unconditionally by the kernel; layout must match OS/.../AppServiceTable.cs.
        public ulong InterfaceDispatchBridgeAddress;

        // Kernel RhpThrowEx entry (raw address). The app tail-jumps its own
        // RhpThrowEx stub here so throw/catch share the kernel EH engine. See
        // ThrowExTrampoline. Layout must match OS/.../AppServiceTable.cs.
        public ulong RhpThrowExAddress;

        // GOP linear framebuffer handoff (step143) — raw DATA, not a service:
        // identity-mapped in the shared address space, the app draws directly.
        // Base==0 = no graphics. Stride in PIXELS per scanline; PixelFormat:
        // 0=RGBX8 1=BGRX8. Layout must match OS/.../AppServiceTable.cs.
        public ulong FramebufferBase;
        public uint FramebufferWidth;
        public uint FramebufferHeight;
        public uint FramebufferStride;
        public uint FramebufferPixelFormat;

        // HPET time-source handoff (step143) — identity-mapped MMIO main
        // counter + calibrated frequency. CounterAddress==0 = no HPET.
        // Layout must match OS/.../AppServiceTable.cs.
        public ulong HpetCounterAddress;
        public ulong HpetFrequencyHz;
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

    internal enum AppServiceAbi : uint
    {
        WindowsX64 = 0,
        SystemV = 1,
        Auto = 0xFFFFFFFF,
    }
}
