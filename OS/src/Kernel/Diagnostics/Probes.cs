namespace OS.Kernel.Diagnostics
{
    // Single source of truth for which boot-time probes/smoke-tests run.
    // BootSequence consults these flags before invoking each probe; flip
    // a flag to false to silence its output and skip its work entirely.
    //
    // All flags are `const bool`, so the C# compiler folds disabled probes
    // into dead code that ILC removes from the binary. There is no runtime
    // cost for probes left off.
    //
    // Probes that never return (IdtPanic, ExceptionThrow) live here too —
    // flip to true once per change to verify panic/throw paths, then back.
    internal static class Probes
    {
        // Phase 0
        public const bool KeyboardInput = false;

        // Phase 1
        public const bool KernelHeapSmoke = true;

        // Phase 2 diagnostics
        public const bool GcStaticsSummary = true;

        // Phase 3
        public const bool RtcSnapshot = true;        // dump CMOS wall-clock

        // Phase 4
        public const bool GcHeapSmoke = true;
        public const bool GcStress = true;
        public const bool NativeAotFeatures = true;
        public const bool Cctor = true;

        // EH probe gradient — flip each level on independently. L1+L2+L4
        // are expected to pass today (no throw, no unwinder). L3 throws
        // and currently halts; flipping it green is the Phase 1 closure
        // criterion.
        public const bool EhTryFinallyNoThrow = true;
        public const bool EhTryCatchNoThrow = true;
        public const bool EhTryCatchWithThrow = true;    // step 5.6 GATE: L8 == 801 with real dispatch
        public const bool EhExceptionShape = true;       // step 1 gate: full Exception + 6 derived types
        public const bool EhRootWalk = true;              // step 2 gate: .pdata lookup + funclet->ROOT walk
        public const bool EhDecode = true;                // step 3 gate: ehInfoRVA + varint clause decoder
        public const bool EhFrameWalk = true;             // step 4 gate: PAL/RegDisplay/SFI + 4-opcode unwind
        public const bool EhIngressThrow = false;         // step 5.5b verified: throw -> 5.1+5.2+5.4+5.5b chain (default off, halts when on)
        public const bool EhCatchFuncletProbe = false;    // step 5.5a verified: RhpCallCatchFunclet end-to-end OK (default off, halts when on)
        public const bool EhCatchFuncletReal = false;    // step 5.5b verified: real REGDISPLAY + fake handler OK (default off, requires EhIngressThrow too)
        public const bool EhRealDispatch = true;         // step 5.6: full real dispatch (DispatchEx + ILC catch funclet) — needed for L8 == 801
        public const bool EhRethrowChain = true;         // step 6 GATE: L9 == 901 (nested catch with throw;)
        public const bool EhTryCatchFinally = true;       // step 7 GATE: L10 == 111 (finally runs before catch)
        public const bool EhFilter = true;                 // step 8 GATE: L11 == 1101 (catch when filter)
        public const bool EhHwFault = true;                // step 10 GATE: L13 == 3 (null deref → catch)
        public const bool EhStackTrace = true;             // step 11 GATE: L14 == 1401 (Exception.StackTrace populated)
        public const bool EhCollidedUnwind = true;         // step 11 GATE: L15 == 1501 (rethrow inside finally — funclet-aware codeOffset)
        public const bool EhMultiFrameFinally = true;      // Phase 1 polish: L16 == 1616 (caller's finally runs on callee throw)
        public const bool EhMultiFrameStackTrace = true;   // Phase 1 polish: L17 == 1700+frames (stack trace records each frame)
        public const bool EhEnumLive = true;              // step 5.3 probe A: enum clauses on live frame inside try (non-halting)

        public const bool IdtPanic = false;          // never returns when on
        public const bool ExceptionThrow = false;    // never returns when on (legacy single-throw probe)

        // Phase 6 — CoreCLR integration probe. Calls coreclr_initialize
        // statically-linked from coreclr_static.lib (Phase 6.1.0). Expected
        // to halt at first unimplemented SharpOSHost_* / CrtAndEh stub —
        // tells us empirically what CoreCLR wants на init.
        public const bool CoreClrInit = true;

        // Phase B — own 16550 UART (COM1) driver bring-up. Inits the
        // chip directly via PortIo and self-tests via loopback, then
        // writes one line through the OWN driver. Pre-EBS this hits the
        // same physical COM1 the UEFI ConOut mirror reads, so the line
        // appearing proves the post-EBS serial substrate works. Permanent
        // regression oracle for the serial driver.
        public const bool SerialSmoke = true;

        // Phase B#2 — own GOP framebuffer renderer. Clears the screen,
        // draws colour bands + an 8x8-font banner straight to the mapped
        // FB MMIO, then emits a deterministic FNV-1a checksum of a fixed
        // rendered region to serial (headless-verifiable; the painted
        // screen is the eyeball proof under SHARPOS_GUI=1). Permanent
        // regression oracle for the framebuffer text path.
        public const bool FbRender = true;

        // Phase B#3 — own i8042/PS-2 keyboard bring-up. Non-destructive
        // STATUS presence read + a pure set-1 decoder self-test over a
        // fixed synthetic scancode script (headless-deterministic; real
        // keystroke proof is the interactive shell under SHARPOS_GUI=1).
        // Permanent regression oracle for the keyboard decode path.
        public const bool Ps2 = true;

        // Phase B#3 — line editor over the PS/2 decoder. Synthetic-script
        // self-test of the buffer logic (printable insert + Backspace +
        // Enter); headless-deterministic, real typing proof is the
        // interactive shell under SHARPOS_GUI=1.
        public const bool LineEdit = true;

        // Phase B#3 — native-tier shell engine. Drives Shell.Execute
        // with literal command lines, asserts dispatch + the mem data
        // path (headless-deterministic). The interactive REPL over this
        // engine is a separate default-off gate (would block headless).
        public const bool ShellEngine = true;

        // Phase B#3 — interactive shell REPL (real PS/2 keystrokes,
        // echoed to serial + FbTty). BLOCKS on input — keep false for
        // headless/regression runs; flip true and boot under
        // SHARPOS_GUI=1 to type at it. ILC dead-codes it when false.
        public const bool ShellInteractive = false;

        // Phase C experiment — physically call ExitBootServices and
        // prove the own UART+GOP+PS/2 substrate survives without UEFI.
        // NEVER-RETURNING and tears down UEFI — must stay false for the
        // headless regression battery; flip true to run the experiment
        // (best under SHARPOS_GUI=1 to watch the post-EBS FB banner,
        // serial also continues via the own 16550). ILC dead-codes it
        // when false.
        public const bool ExitBootServicesExperiment = false;

        // C-FS1 — PCI ECAM scan (adapted MOOS PCIExpress). Finds the
        // q35 AHCI controller (class 0x01/0x06, ABAR=BAR5) — foundation
        // for the own-substrate read-only FAT32 stack. Headless-
        // deterministic (fixed QEMU topology); permanent oracle.
        public const bool PciScan = true;

        // C-FS2/FS3 (AHCI Disk + RO-FAT) have NO Phase-4 gate: bringing
        // up AHCI reprograms the HBA the live UEFI firmware still owns,
        // so it runs POST-EBS only (ExitBootServicesProbe), gated by
        // ExitBootServicesExperiment. PciScan above is read-only and
        // safe pre-EBS.
    }
}
