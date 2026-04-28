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
        public const bool EhEnumLive = true;              // step 5.3 probe A: enum clauses on live frame inside try (non-halting)

        public const bool IdtPanic = false;          // never returns when on
        public const bool ExceptionThrow = false;    // never returns when on (legacy single-throw probe)
    }
}
