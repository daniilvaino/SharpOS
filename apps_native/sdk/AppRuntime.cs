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

            // Wire throw/catch into the kernel's shared EH engine: tail-jump our
            // RhpThrowEx stub to the kernel's RhpThrowEx entry. No GC needed.
            ThrowExTrampoline.PatchToKernelThrow(
                s_services->RhpThrowExAddress);

            // Bring up the managed GC heap before any `new string` or `new object()`
            // hits its RhNewString / RhpNewFast export. GcMemorySource backing is
            // GcAppPool (64 MB in .bss), provided via GcMemorySource.AppStatic.cs.
            SharpOS.Std.NoRuntime.GcHeap.Init();

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
            if (!SharpOS.Std.NoRuntime.GcStaticsInit.Materialize())
            {
                AppHost.WriteString("gcstatics: init FAILED code=");
                AppHost.WriteUInt((uint)SharpOS.Std.NoRuntime.GcStaticsInit.FailedCount);
                AppHost.WriteChar('\n');
            }
        }

        public static AppStartupBlock* Startup => s_startup;

        public static AppServiceTable* Services => s_services;

        public static bool IsInitialized => s_startup != null && s_services != null;
    }
}
