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
        LimitReached = 8,
        Busy = 9,
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

        // Block until the value at an address differs from the one given
        // (uint Wait(void* address, void* compare, uint size, uint timeoutMs),
        // 1 = woken or already different, 0 = timed out; timeout 0xFFFFFFFF
        // is infinite), and wake everyone blocked on an address. Win32
        // WaitOnAddress / WakeByAddressAll. Called directly, Win64 ABI; zero
        // on a kernel without them — std then waits by polling.
        public ulong WaitOnAddressAddress;
        public ulong WakeByAddressAllAddress;

        // Diagnostics that must not paint: they go to the serial port and the
        // log, never the screen. For a full-screen application, reporting
        // through ordinary or error output lands the report inside the
        // interface it describes. Zero on a kernel without it.
        public ulong WriteDiagnosticAddress;

        // The HPET counter's width, and where the kernel keeps the epoch for
        // a narrow one (step177). Handing over the raw counter address alone
        // was wrong on hardware whose counter is 32 bits: it wraps every
        // 300 s, and an app reading it as a 64-bit value sees time jump
        // backwards. Pacing built on "deadline = now + delta" then waits for
        // a moment that never comes.
        //
        // Zero bits means the kernel predates this and the counter is 64-bit,
        // which is what every app assumed before.
        public uint HpetCounterBits;
        public uint HpetReserved;
        public ulong HpetLatchAddress;

        // The USB bus, for an application that wants to look at it.
        //
        // Added at the tail and the ABI version left alone: a service is
        // present when its address is not zero, which is what an app has to
        // check anyway, and bumping the version would make every existing app
        // refuse to start on a kernel that gained a feature they do not use.
        //
        // Enumerate fills a caller's array with one node per controller,
        // device, interface and endpoint — a tree flattened into parent
        // indices. Ctap runs one CTAPHID exchange, framing and all.
        public ulong UsbEnumerateAddress;
        public ulong UsbCtapAddress;

        // Registers this image's factory for hardware-fault exceptions, so a
        // fault in app code raises an exception of the app's own type (see
        // AppRuntime.CreateHardwareException). Zero: an older kernel, and a
        // fault raises the kernel's type, which no catch here matches.
        public ulong SetHwExceptionFactoryAddress;

        // The kernel's preemption-suppression depth (a uint). The app's
        // allocator and collector bump it around their critical sections
        // (AppPreemption). Zero: an older kernel, which never preempts apps.
        public ulong PreemptionDepthAddress;

        // What this program was started with: records on its own stack
        // (AppRuntime reads the arguments from it). Zero: nothing was passed.
        public ulong StartupDataAddress;
        public uint StartupDataLength;
        public uint StartupDataReserved;

        // The exchange heap: blocks outside every collector, owned by this run
        // and returned when it ends. Zero: an older kernel.
        public ulong ExchangeAllocateAddress;
        public ulong ExchangeFreeAddress;

        // Experiment: hand a region to the kernel (AppHost.TryRegionToKernel).
        public ulong RegionToKernelAddress;

        // Experiment: a region from the kernel (AppHost.RegionFromKernel).
        public ulong RegionFromKernelAddress;

        // The exchange heap's arena and page table, for the write barrier
        // (SharpOS.Std.Exchange.ExchangeArena). Zero: an older kernel.
        public ulong ExchangeArenaLow;
        public ulong ExchangeArenaSpan;
        public ulong ExchangePageTable;

        // Native pipes (pipe spec Р6, Р27, Р30, Р44; OS.Kernel.Pipes.PipeServices
        // has the signatures): create a pair, connect by name, declare the
        // writer's type, send, receive with or without waiting, close, read the
        // declared description. Win64 only. Zero: an older kernel.
        public ulong PipeCreateAddress;
        public ulong PipeConnectAddress;
        public ulong PipeDeclareAddress;
        public ulong PipeSendAddress;
        public ulong PipeReceiveAddress;
        public ulong PipeCloseAddress;
        public ulong PipeSchemaAddress;

        // Test orchestration for the pipe probes (Probes.PipeProbe):
        // int (int op, ulong argument, ulong* answer). Zero: not published.
        public ulong PipeProbeAddress;

        // The shared RhpByRefAssignRef barrier (OS.Kernel.Memory.RegionBarrier):
        // the app points its own stub there with `mov rax, imm64; jmp rax`.
        // Zero: an older kernel, and the app keeps its plain copy.
        public ulong RegionByRefBarrierAddress;

        // The type an end of a pair (PipeCreate) is opened with, checked
        // against the other end's (step194 §5): int (ulong* request), see
        // OS.Kernel.Pipes.PipeServices.OpenEnd. Zero: an older kernel.
        public ulong PipeOpenEndAddress;

        // Processes (step194): int (int op, ulong* request) — start with pipe
        // ends, wait, has-exited, kill, release, own id; see
        // AppServiceBuilder.ProcessService. Zero: an older kernel.
        public ulong ProcessAddress;

        // Registers this image's namer for its exceptions: nint (nint
        // exception) answering a string of the image's, the type's name
        // (SharpOS.Std.Runtime.ExceptionNames). The unhandled-exception report
        // prints it (step194 §3). Zero: an older kernel.
        public ulong SetExceptionNamerAddress;

        // Waits until the other end of a pipe has come (step196): int (int
        // handle). A stage closing its output by name waits for its reader
        // first, or the end of the stream goes with the pipe. Zero: older kernel.
        public ulong PipeWaitPeerAddress;

        // A tick that came while the app suppressed switching (a byte, 1 =
        // pending), and the switch it asked for: void (). The app's critical
        // sections end with a look at the byte (step196). Zero: older kernel.
        public ulong PreemptionPendingAddress;
        public ulong YieldAddress;

        // The app's heap (step196): a range of addresses in the process's slot,
        // a budget of pages, and the calls that give pages under it and take
        // them back — int (ulong address, ulong bytes), 0 = done. The heap no
        // longer lives in the image. Zero: an older kernel.
        public ulong HeapBase;
        public ulong HeapBytes;
        public ulong HeapBudget;
        public ulong HeapCommitAddress;
        public ulong HeapReleaseAddress;

        // Open files (step197): int FileOpen(byte* asciiPath, int mode, int*
        // handle) — mode 0 read, 1 write (created or cut), 2 append; int
        // FileRead(int handle, byte* dst, int cap, int* got); int
        // FileWrite(int handle, byte* src, int length); int FileClose(int
        // handle). Zero: an older kernel.
        public ulong FileOpenAddress;
        public ulong FileReadAddress;
        public ulong FileWriteAddress;
        public ulong FileCloseAddress;

        // void SetExceptionExitCode(int (*code)(nint exception)) (step197):
        // the exit code for an unhandled exception, asked of the app; 0 the
        // default (134), 141 a broken standard end — the process ends without
        // a report.
        public ulong SetExceptionExitCodeAddress;

        // Settings for the app's own start (step197), bits; 0 from an older
        // kernel. SettingAnnounceBuild: print "[app] NAME build ID" — on for
        // the autorun battery, whose log needs it; off at a prompt.
        public ulong Settings;

        public const ulong SettingAnnounceBuild = 1;

        // CPU features beyond x86-64's baseline (step198): bits of
        // SharpOS.Std.NoRuntime.LibmPatcher; 0 from an older kernel.
        public ulong CpuFeatures;
    }

    /// <summary>
    /// One node of the USB tree. Kind decides which fields mean anything.
    /// </summary>
    /// <remarks>
    /// A flat array with parent indices rather than a real tree, because it
    /// crosses an ABI boundary: pointers between nodes would have to be
    /// rewritten for the app's address space, and an index does not care where
    /// the array lives.
    ///
    ///   Kind 0 controller: Number = index, Vendor/Product = PCI ids
    ///   Kind 1 device:     Number = slot, Class/Subclass/Protocol, Vendor,
    ///                      Product, Speed, Flags bit 0 = configured,
    ///                      bit 1 = the device descriptor did not come back
    ///                      (so Class/Vendor/Product are unknown, not zero),
    ///                      ManufacturerName/ProductName/SerialNumber as far
    ///                      as the device names itself (empty when it does not)
    ///   Kind 2 interface:  Number = interface number, Class/Subclass/Protocol,
    ///                      UsagePage/Usage when it is a HID we read,
    ///                      Flags bit 0 = a driver claimed it
    ///   Kind 3 endpoint:   Number = address, EndpointType, MaxPacket, Interval
    /// </remarks>
    internal unsafe struct AppUsbNode
    {
        public uint Kind;
        public uint Parent;          // index in the same array, 0xFFFFFFFF at a root
        public uint Number;
        public uint Class;
        public uint Subclass;
        public uint Protocol;
        public ushort Vendor;
        public ushort Product;
        public uint Speed;
        public ushort UsagePage;
        public ushort Usage;
        public ushort MaxPacket;
        public byte EndpointType;    // 0 control, 1 iso, 2 bulk, 3 interrupt
        public byte Interval;
        public uint Flags;

        /// <summary>Room for a name, ASCII, NUL-terminated.</summary>
        /// <remarks>
        /// Inline rather than a second service returning strings, because the
        /// listing wants them beside the numbers and a per-string call would
        /// mean one round trip per device per field. Thirty-two bytes fits the
        /// names devices actually carry, and a longer one is truncated rather
        /// than refused.
        ///
        /// ASCII, not UTF-16 as the wire carries it: the console drops what its
        /// font has no glyph for, so a name that arrived as anything else would
        /// print as holes. Folded at the source, where the bytes are.
        /// </remarks>
        public const int NameBytes = 32;

        public fixed byte ManufacturerName[NameBytes];
        public fixed byte ProductName[NameBytes];
        public fixed byte SerialNumber[NameBytes];
    }

    internal unsafe struct AppUsbEnumerateRequest
    {
        public ulong BufferAddress;  // AppUsbNode[]
        public uint Capacity;        // in nodes
        public uint Count;           // filled in: nodes written
        public uint Status;
        public uint Reserved;
    }

    /// <summary>One CTAPHID exchange with a security key.</summary>
    /// <remarks>
    /// Channel 0 on the way in means "introduce us" — the service runs
    /// CTAPHID_INIT and hands back the channel the key allocated. Any other
    /// value sends Command on that channel.
    /// </remarks>
    internal unsafe struct AppUsbCtapRequest
    {
        public uint Channel;         // in: 0 = INIT; out: the channel to use
        public uint Command;         // CTAPHID command, ignored when Channel is 0
        public ulong PayloadAddress;
        public uint PayloadLength;
        public uint TimeoutMs;
        public ulong ResponseAddress;
        public uint ResponseCapacity;
        public uint ResponseLength;
        public uint Status;
        public uint KeepAlives;      // how long the key made us wait
        public uint Error;           // CTAPHID error code, when it refused
        public uint Reserved;
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
        public const uint FlagHasArguments = 1;

        public ulong PathAddress;
        public uint AppAbiVersion;
        public uint ServiceAbi;
        public int ExitCode;
        public uint Reserved;                   // flags

        // With FlagHasArguments: UTF-8 strings, each ended by a NUL.
        public ulong ArgumentsAddress;
        public uint ArgumentsLength;
        public uint ArgumentsReserved;
    }

    internal enum AppServiceAbi : uint
    {
        WindowsX64 = 0,
        SystemV = 1,
        Auto = 0xFFFFFFFF,
    }
}
