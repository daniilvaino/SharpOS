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

        // Raw entry of the kernel's interface-dispatch bridge shellcode
        // (InterfaceDispatchBridge.ShellcodeStart). NOT an ABI-thunked managed
        // function: it is the target of a `mov rax,imm64; jmp rax` the app
        // patches into its own RhpInitialDynamicInterfaceDispatch stub, so the
        // app shares the kernel's (pure, major-9) dispatch resolver instead of
        // carrying its own. Filled unconditionally, independent of AbiVersion.
        public ulong InterfaceDispatchBridgeAddress;

        // Raw entry of the kernel's RhpThrowEx (ThrowExStub.GetMethodAddress —
        // patched at boot to the throw shellcode). The app tail-jumps its own
        // RhpThrowEx stub here so `throw`/`catch` share the kernel's managed EH
        // engine (DispatchEx). Requires the app's .pdata to be registered
        // (PeLoader) so the unwinder can walk app frames. Filled unconditionally.
        public ulong RhpThrowExAddress;

        // GOP linear framebuffer handoff (step143) — raw DATA, not a service:
        // the FB is identity-mapped in the shared address space (Hal.Framebuffer
        // maps it into the pager at boot), so the app draws directly. Base==0 =
        // no graphics (headless / BltOnly). Stride is in PIXELS per scanline;
        // PixelFormat: 0=RGBX8 1=BGRX8 (mirrors BootInfo/GOP). Appended fields —
        // old apps simply never read past RhpThrowExAddress; new apps gate on
        // FramebufferBase != 0. Filled unconditionally, independent of AbiVersion.
        public ulong FramebufferBase;
        public uint FramebufferWidth;
        public uint FramebufferHeight;
        public uint FramebufferStride;
        public uint FramebufferPixelFormat;

        // HPET time-source handoff (step143) — raw data like the FB fields:
        // the main-counter MMIO is identity-mapped in the shared address
        // space, apps read the free-running counter directly (Stopwatch /
        // frame pacing). CounterAddress==0 = no HPET.
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
}
