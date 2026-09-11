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

        // V3 adds threads. Terminal.Gui keeps its input decoding and its resize
        // watch on background loops, so a task there is a thread and nothing
        // else — without these an app can only ever do one thing at a time, and
        // Task.Run has nowhere to run.
        public const uint AbiVersionV3 = 3;
        public const uint CurrentAbiVersion = AbiVersionV3;
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

        // Precise stack-root walk (raw address) — see AppGC. The kernel spills
        // registers, walks the call chain across both images and reports every
        // live managed root to our callback; we mark into OUR heap and sweep it
        // ourselves. Layout must match OS/.../AppServiceTable.cs.
        public ulong GcWalkRootsAddress;

        // V3 — threads. At the END of the struct, which is the whole point:
        // the first attempt put them after WriteBuildIdAddress, which is the end
        // of the V2 *service* block but nowhere near the end of the table. Every
        // field below it shifted by 16 bytes, so an app built against the old
        // layout read InterfaceDispatchBridgeAddress from the wrong offset,
        // patched its dispatch stub with garbage and died before printing
        // anything. Appending is only appending if it is at the end.
        public ulong SpawnThreadAddress;
        public ulong SleepAddress;

        // Appended without bumping the version, deliberately.
        //
        // The table grows at the end and every address is zero until filled, so
        // "is this service here?" is answered by the field itself. The version
        // number is for binaries that travel separately from the kernel; ours
        // are built in the same run. Raising it for each new pointer costs a
        // migration everywhere and buys nothing — and a number that changes for
        // no reason is worse than no number, because it stops meaning anything.
        //
        // So: V3 stays frozen while the tail grows. Bump it when the SHAPE of
        // something already published changes, which is when an old app would
        // genuinely misread the table.
        public ulong CurrentThreadIdAddress;

        // Console size in cells, packed: columns in the low 16 bits, rows in the
        // high 16. Appended without a version bump — the table grows at the end
        // and an unfilled service reads as zero, which is the same question.
        //
        // An app cannot work this out for itself: the size comes from the
        // framebuffer and the font, both of which live on the kernel side.
        public ulong ConsoleSizeAddress;

        // Kernel RhpRethrow entry, the partner of RhpThrowExAddress above.
        // ILC emits a call to it for a bare `throw;` inside a catch — a
        // different helper from `throw expr`, and one an app cannot carry
        // itself for the same reason: rethrow resumes a dispatch the kernel's
        // EH engine started.
        //
        // Appended without a version bump; the address being zero is the
        // "not published" answer.
        public ulong RhpRethrowAddress;

        // Runs a managed assembly on the kernel's hosted CoreCLR. Zero when the
        // kernel published none — a build without CoreCLR, or an older one.
        public ulong RunManagedAppAddress;

        // The error stream: NUL-terminated UTF-8, like WriteStringAddress, on
        // its own channel. Zero on a kernel that predates it — AppHost.WriteError
        // falls back to ordinary output. Layout must match OS/.../AppServiceTable.cs.
        public ulong WriteErrorAddress;


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

    internal unsafe struct AppRunManagedRequest
    {
        public ulong PathAddress;
        public int ExitCode;
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
