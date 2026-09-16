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

        // V3 adds threads. Terminal.Gui keeps its input decoding and its resize
        // watch on background loops, so a task there is a thread and nothing
        // else — without these an app can only ever do one thing at a time, and
        // Task.Run has nowhere to run.
        public const uint AbiVersionV3 = 3;
        public const uint CurrentAbiVersion = AbiVersionV3;
        public const uint AutoSelectAbiVersion = 0xFFFFFFFF;

        /// <summary>
        /// Clamps a requested version to one this kernel actually publishes.
        ///
        /// Lives here, beside the constants, because it was written three times
        /// — in AppServiceBuilder, in ProcessImageBuilder, and in the launcher
        /// path — and when V3 landed only some of them moved. The result was
        /// apps that failed a version compare against a table the same kernel
        /// had just filled. A rule duplicated per caller is a rule that will
        /// disagree with itself.
        /// </summary>
        public static uint Normalize(uint requested)
        {
            if (requested <= AbiVersionV1) return AbiVersionV1;
            if (requested == AbiVersionV2) return AbiVersionV2;
            return CurrentAbiVersion;
        }

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

        // Precise stack-root walk (raw address). The app calls this to have the
        // kernel spill registers, walk the call chain across BOTH images and
        // report every live managed root to a callback the app supplies.
        //
        // The app keeps its own heap and its own sweep — it borrows the walker,
        // not the memory. That is deliberate: sharing a heap between kernel and
        // apps would make one app's garbage everyone's pause, and would undo
        // the isolation that SMP and preemption will need. Sharing the walker
        // costs nothing, because the machinery is image-aware already.
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

        // Runs a managed assembly on the hosted CoreCLR and waits for it.
        //
        // Not the same thing as RunAppAddress: that one loads a PE into the
        // address space and jumps to it. A managed assembly has no image of its
        // own here — it is handed to a runtime that already exists, on the
        // stack that runtime was brought up on.
        //
        // Appended without a version bump; zero is the "not published" answer,
        // which is also the honest answer on a build with CoreCLR left out.
        public ulong RunManagedAppAddress;

        // The application's error stream: a NUL-terminated UTF-8 string, like
        // WriteStringAddress, but on its own channel (step 167) — COM4 and
        // last_err.log where the machine has the port. Offered at every ABI
        // version, as WriteString is; appended without a version bump, zero is
        // the "not published" answer.
        public ulong WriteErrorAddress;

        // Win32 WaitOnAddress / WakeByAddressAll for app threads (step174):
        // what Task.Wait and ManualResetEventSlim block on instead of polling
        // with Sleep(1). Published directly as [UnmanagedCallersOnly] kernel
        // functions, no thunk — same as GcWalkRootsAddress — and only to Win64
        // apps; zero is the "not published" answer.
        public ulong WaitOnAddressAddress;
        public ulong WakeByAddressAllAddress;

        // Diagnostics that must not reach the screen (step175): heap censuses
        // and the like, from an application drawing a full-screen interface.
        // Ordinary output and error output both paint, so a report printed
        // through them lands in the middle of the interface it is describing.
        // This one goes where measurements go — the serial port and the log.
        public ulong WriteDiagnosticAddress;
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
}
