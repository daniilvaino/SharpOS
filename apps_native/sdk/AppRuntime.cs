namespace SharpOS.AppSdk
{
    internal static unsafe class AppRuntime
    {
        private static AppStartupBlock* s_startup;
        private static AppServiceTable* s_services;

        public static void Initialize(AppStartupBlock* startup)
        {
            // FIRST, before anything that might own a page-crossing frame:
            // turn RhpStackProbe into a plain `ret` (no guard pages on fully
            // premapped app stacks — see StackProbeStub). Needs no services.
            StackProbeStub.PatchToRet();

            s_startup = startup;
            if (startup == null)
            {
                s_services = null;
                return;
            }

            s_services = (AppServiceTable*)startup->ServiceTableAddress;

            // Publish this image's TypeManager into its TypeManagerIndirection
            // slots (needs no GC, no services). The kernel resolver reads
            // MT -> TM -> DispatchMapTable on the shared-generic/variant
            // fallback path; with the slots left null the first generic
            // instantiation dispatch dies (see AppTypeManagerInit).
            SharpOS.Std.NoRuntime.AppTypeManagerInit.Initialize();

            // Wire interface dispatch (needs no GC): trampoline our
            // RhpInitialDynamicInterfaceDispatch stub into the kernel's shared
            // bridge shellcode (handed over in the service table). Until this
            // runs, any interface call (EqualityComparer, Dictionary, IEquatable)
            // hits the inert fallback body and halts — so patch before anything
            // else that might dispatch.
            InterfaceDispatchTrampoline.PatchToKernelBridge(
                s_services->InterfaceDispatchBridgeAddress);

            // Byref struct copies (List<T> element moves, Dictionary entries) go through
            // RhpByRefAssignRef; unlike the dispatch bridge this one needs nothing from the
            // kernel — our GC has no write barrier, so the app writes the 15 bytes itself.
            ByRefAssignRefStub.TryInstall();

            // Real compare-and-swap for std's Interlocked. Apps have threads
            // now and the kernel preempts them, so the managed fallback — read,
            // compare, write as three statements — can lose an update to a
            // timer tick landing between them. Everything that locks is built
            // on this, so it goes in before anything that might.
            AtomicStub.Install();

            // Wire throw/catch into the kernel's shared EH engine: tail-jump our
            // RhpThrowEx stub to the kernel's RhpThrowEx entry. No GC needed.
            ThrowExTrampoline.PatchToKernelThrow(
                s_services->RhpThrowExAddress);

            // And `throw;` inside a catch, which is a different helper: it
            // resumes the dispatch already in flight rather than starting one.
            RethrowTrampoline.PatchToKernelRethrow(
                s_services->RhpRethrowAddress);

            // Bring up the managed GC heap before any `new string` or `new object()`
            // hits its RhNewString / RhpNewFast export. GcMemorySource backing is
            // GcAppPool (64 MB in .bss), provided via GcMemorySource.AppStatic.cs.
            // Without it every allocation fails, so the app stops here with the
            // reason instead of running on into its first `new`.
            SharpOS.Std.NoRuntime.GcHeap.s_fatal = &Fatal;
            SharpOS.Std.NoRuntime.GcHeap.s_diagnostic = &OomDiagnostic;
            if (!SharpOS.Std.NoRuntime.GcHeap.Init())
                Fatal("app GC heap init failed");

            // First thing on the new heap: the OutOfMemoryException that
            // allocation failures throw. When one is needed there is no room
            // left to make it.
            SharpOS.Std.NoRuntime.GcHeap.PrepareOutOfMemory();

            // Wire the HPET time source into Stopwatch (step143). Raw data
            // from the table — safe before the heap is up.
            if (s_services->HpetCounterAddress != 0 && s_services->HpetFrequencyHz != 0)
            {
                System.Diagnostics.Stopwatch.s_counterAddress = s_services->HpetCounterAddress;
                System.Diagnostics.Stopwatch.s_frequencyHz = s_services->HpetFrequencyHz;
            }

            // Materialize the app image's GCStaticRegion (lazy `static readonly`
            // blocks — ILC TypePreinit). Needs the heap (allocates the statics
            // objects) and pairs with the DropResilient target in
            // FreestandingPe.props; without both, the first static touch #GPs
            // on the ILC sentinel. See std GcStaticsInit + limits doc §1.
            // A failure is fatal: a static left unmaterialized keeps the ILC
            // placeholder, which every later access reads as the address of
            // the statics.
            if (!SharpOS.Std.NoRuntime.GcStaticsInit.Materialize())
            {
                AppHost.WriteString("gcstatics: init FAILED code=");
                AppHost.WriteUInt((uint)SharpOS.Std.NoRuntime.GcStaticsInit.FailedCount);
                AppHost.WriteChar('\n');
                Fatal("app GC statics not materialized");
            }

            // Route GC.Collect() to our own collector. Last, because it marks
            // from the static roots the step above just materialised — before
            // that there is nothing to keep alive and nothing to sweep.
            AppGC.Install();
        }

        // Exit code of an app stopped by a failure it cannot survive, the
        // number an aborted process gets on Unix.
        private const int FatalExitCode = 134;

        // Ends the app with its reason on the error stream. Allocates nothing:
        // it is where allocation failures end up.
        private static void Fatal(string message)
        {
            AppHost.WriteError("[fatal] ");
            AppHost.WriteError(message);
            AppHost.WriteError("\n");
            AppHost.Exit(FatalExitCode);
            while (true) { }
        }

        public static AppStartupBlock* Startup => s_startup;

        public static AppServiceTable* Services => s_services;

        public static bool IsInitialized => s_startup != null && s_services != null;
        // The refusal report from the allocator, routed where the app's other
        // diagnostics go. Silent without the service rather than painting over
        // the interface it would be describing.
        private static void OomDiagnostic(byte* utf8)
        {
            if (utf8 == null) return;
            if (!AppHost.HasDiagnosticStream) return;
            AppHost.WriteDiagnostic(utf8);
        }

    }
}
