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

        // Phase F1: SharpOS delivers its own interrupts — legacy PIC masked,
        // local APIC enabled, periodic timer on vector 0x20. Runs only
        // post-ExitBootServices, where the firmware handlers we inherited are
        // code without an owner. Off leaves the system exactly as before:
        // interrupts disabled, every driver polling.
        public const bool OwnInterrupts = true;

        // Phase F2 gate: a collection must find roots on the stacks of threads
        // that are not running. Spawns a worker holding an array in a local,
        // parks it, collects, and has the worker check its own data.
        public const bool ThreadGcRoots = true;

        // Phase F prerequisite: the interrupt path must preserve FP/SIMD state
        // across a handler that returns. On by default — this is a correctness
        // invariant the preemptive timer will lean on, not a curiosity.
        public const bool FpFaultPreservesXmm = true;

        // Phase 4
        public const bool GcHeapSmoke = true;
        public const bool GcStress = true;
        public const bool NativeAotFeatures = true;

        // Terminal engine (vendor/XtermSharp) self-test + rich console front-end.
        // On by default: the engine is new in the image and its failures should be
        // loud, not opt-in.
        public const bool TerminalEngine = true;

        // Late-boot NativeAOT probes — run AFTER Phase E threading is up.
        // Tests thread-handoff with GC mid-transfer + OOM deterministic
        // behavior. Separate toggle so threading regressions don't mask
        // early-boot feature probes.
        public const bool NativeAotFeaturesLate = true;
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

        // Hosted-tier launch target selector — which managed assembly
        // coreclr_execute_assembly runs after CoreCLR init.
        //   true  → \sharpos\NormalHello.dll (probe/census — PAL/OS surface
        //           coverage, ~200 probes, exits with rc=42)
        //   false → \sharpos\PowerShellBootstrap.dll (managed shim → PS
        //           7.5.5 ManagedPSEntry, interactive pwsh prompt)
        // Both DLLs are always built and deployed by run_build.ps1 + added
        // to TPA; the toggle only chooses which one execute_assembly aims
        // at. Const bool so ILC folds the unused branch to nothing.
        public const bool LaunchNormalHelloCensus = false;

        // Mute kernel diagnostics while the hosted app owns the screen.
        //
        // ON: census lines stay intact, which is what the report parser needs —
        // the [seh-*]/[host]/[stub-reg] chatter otherwise lands INSIDE probe
        // lines and turns half of them into unparseable fragments.
        //
        // OFF: that chatter stays visible, which is what you want while chasing
        // a hang — the missing-symbol lines name the next unimplemented API.
        // Safe again since step 152; it used to triple-fault, but that was the
        // MXCSR bug faulting inside exception dispatch, not the output itself.
        //
        // Either way this also gates the SERIAL LOG: Console.Quiet is checked
        // before anything reaches Platform.Write.
        public const bool HostedAppQuietConsole = false;

        // Phase E2 — TEB facade swap. Allocates a fresh TebFacade, swaps
        // gs base to it (under CLI), reads gs:[Self] and gs:[StackLimit]
        // back, restores original gs base. Proves the per-switch primitive
        // works before Phase E4 wires it into a cooperative context
        // switch. Independent of CoreCLR (runs against own X64Asm stubs).
        public const bool TebFacadeSwap = true;

        // Phase E3 — atomic primitives (lock cmpxchg / xchg / mfence).
        // Stack-resident ulong slot, four mini scenarios (hit, miss,
        // xchg, mfence). Regression oracle for the X64Asm shellcode
        // bytes — multi-core stress is E4+ business.
        public const bool Atomics = true;

        // Phase E4 — cooperative two-thread ping-pong. Wraps current
        // execution as a Thread, spawns T1 and T2 (each yielding N
        // times), drives Scheduler.Yield until both children exit.
        // Asserts iteration counts + boot-thread resume. First real
        // multi-thread artefact in the project — commit milestone.
        public const bool ThreadPingPong = true;

        // Phase E5 — Scheduler.Sleep accuracy. Spawns one worker that
        // performs 3 × Sleep(50ms), measures elapsed via HPET around
        // each sleep, asserts [45..80] ms. Lower bound enforces "we
        // actually slept"; upper bound absorbs spin-yield wake latency
        // (no IRQ-driven HPET wake yet).
        public const bool ThreadSleep = true;

        // Phase E5 — Event.Wait/Set round trip with manual-reset event.
        // Main spawns a waiter, Sleep(30ms), Set, then measures
        // (set -> wake) latency. Asserts < 30 ms.
        public const bool ThreadEvent = true;

        // Phase E5 — Semaphore Wait + Release(n). Spawns 3 waiters on a
        // 0-count semaphore; Release(2) should wake exactly 2; Release(1)
        // wakes the last. Asserts residualCount == 0.
        public const bool ThreadSemaphore = true;

        // Phase E6 acceptance — 4 worker threads × 10000 Alloc/Free
        // cycles on KernelHeap, with a Yield between write and verify
        // of an own-thread-id pattern. Surfaces any cross-thread
        // bookkeeping corruption around the allocator lock.
        public const bool AllocStress = true;

        // Phase E7 acceptance — two Processes launched concurrently,
        // each running a worker through 3 yielding iterations then
        // Process.Exit(code). Asserts state machine, exit codes,
        // distinct PIDs.
        public const bool ProcessSpawn = true;

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



        // Phase C — physically call ExitBootServices and run the rest
        // of the boot (FAT mount, CoreCLR session, native launcher) on
        // the own UART+GOP+PS/2+AHCI substrate, no UEFI. This is the
        // DEFAULT BOOT as of step 91 (Phase C4): the post-EBS substrate
        // is canonical; the legacy pre-EBS path is dead code (retained
        // for diagnostic comparison if a regression demands it, flip
        // false). Reversal note: tears down UEFI Boot Services
        // irreversibly, so any pre-EBS-only diagnostics must run BEFORE
        // ExitBootServicesProbe.
        public const bool ExitBootServicesExperiment = true;

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

        // Step 110 part 1 — resolve a few known IPs to their per-method
        // gcInfo blob via CoffMethodGcInfo.TryResolve, dump first bytes.
        // Sanity check that .pdata + UNWIND_INFO walk lands at a plausible
        // varint stream before we write the decoder. Headless-deterministic.
        public const bool CoffGcInfoDump = true;

        // Step 110 part 6 — synthetic-CONTEXT smoke test for the slot
        // resolver. Builds a fake Context with sentinel values, runs
        // resolver on Marker3 live slots, prints resolved pointer values.
        // Headless-deterministic.
        public const bool GcInfoResolverSmoke = true;

        // Step 110 part 7 — GcContextSpill shellcode smoke. Captures real
        // CONTEXT via shellcode, verifies Rip resolves through .pdata.
        // First exercise of the live-CONTEXT capture path that the precise
        // GC walker will use.
        public const bool GcContextSpillSmoke = true;

        // Step 110 part 8 — full precise GC walk + sweep. Holds a sentinel
        // string live across CollectPrecise, verifies it survives (i.e. the
        // walker found it and marked it before sweep ran). Headless-
        // deterministic; flips ReclamationDisabled locally for the test.
        // Step 123: temporarily off — VA 0 unmap guard exposed a null-deref at
        // Run+0x276 (CR2=8). Probe needs targeted fix, not blocker on CoreCLR
        // managed NRE bring-up. Flip back true after addressing.
        public const bool KernelGcPreciseSmoke = false;

        // Lists the USB host controllers the machine exposes. The first step of
        // the USB stack: which controller is present decides what to write, and
        // it differs between QEMU and the test machines.
        public const bool UsbScan = true;

        // Takes the xHCI controller from the firmware and resets it. DMA-free:
        // register reads, one ownership handshake, one reset — so it can be
        // trusted on hardware before any ring exists.
        public const bool XhciInit = true;

        // After configuring a HID device, wait a few seconds for reports so a
        // keypress can be seen end to end. Costs boot time and needs someone
        // at the keyboard, so it is off for headless regression runs.
        public const bool UsbHidPoll = true;

        // Times a full-screen fill and a full-screen scroll, and prints the
        // page attributes behind the framebuffer. Overwrites the screen, so it
        // runs before anything worth reading is on it.
        public const bool FbPerf = true;

        // Largest GOP mode the kernel will switch to. The firmware's own
        // choice is unpredictable and cannot be influenced from outside, so
        // the mode is picked here instead.
        //
        // Raising this to 3840x2160 is supported and worth trying: the cost
        // is that a full-screen repaint moves four times the pixels of 1080p
        // through a software renderer with no blitter, and the 8x8 font gets
        // very small. Set both to 0 to accept whatever the firmware left.
        public const uint DisplayMaxWidth = 1920;
        public const uint DisplayMaxHeight = 1080;

        // Creates a file: allocates clusters and edits a directory, so a bug
        // damages the volume rather than one file's contents. Separate switch
        // from FatWrite for exactly that reason.
        public const bool FatCreate = true;

        // Writes a pattern to the last sector of the USB stick and reads it
        // back. Safe against the QEMU stick (a copy rebuilt every run) and
        // destructive against a real one — keep off outside QEMU.
        // OFF: this writes to the last sector of whatever USB medium is
        // attached. Against the QEMU stick (a copy rebuilt every run) that is
        // free; against a real one it is somebody's drive, and it went out to
        // a test machine switched on and scribbled there. Turn it on only for
        // a QEMU run, never in anything that ships to hardware.
        public const bool UsbStorageWrite = false;

        // Stop the machine right after printing the USB list. On the test
        // hardware there is no serial, no log (the boot medium is USB and we
        // cannot read it post-EBS) and the screen scrolls past the answer
        // before anyone can read it — so freeze it while it is still there.
        public const bool UsbScanHalt = false;

        // First filesystem write: overwrites the head of a staged file in
        // place. On real hardware this writes to the machine's own ESP, so
        // keep it off there until the QEMU image proves it correct.
        public const bool FatWrite = true;

        // Traces the key-event path that PSReadLine reads through
        // (SharpOSHost_ReadConsoleInput). Bounded to the first few events so a
        // stuck read is distinguishable from keys that never arrive at all.
        public const bool ConsoleInputTrace = false;
    }
}
