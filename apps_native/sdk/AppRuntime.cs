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

            // The table this image was built to read. A different version means
            // a different shape (the version is bumped only for that), and
            // reading it as ours would call through whatever sits at our
            // offsets. Said and refused before anything else touches it; the
            // write and exit services are at the head of every version.
            if (s_services->AbiVersion != AppServiceTable.CurrentAbiVersion)
            {
                AppHost.WriteError("[app] service table ABI ");
                AppHost.WriteUInt(s_services->AbiVersion);
                AppHost.WriteError(", this program was built for ");
                AppHost.WriteUInt(AppServiceTable.CurrentAbiVersion);
                AppHost.WriteError("\n");
                Fatal("service table ABI mismatch");
            }

            // Publish this image's TypeManager into its TypeManagerIndirection
            // slots (needs no GC, no services). The kernel resolver reads
            // MT -> TM -> DispatchMapTable on the shared-generic/variant
            // fallback path; with the slots left null the first generic
            // instantiation dispatch dies (see AppTypeManagerInit).
            SharpOS.Std.NoRuntime.GcStaticsInit.ImageBase = startup->ImageBase;
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
            // RhpByRefAssignRef. Our GC has no card table, so the plain helper is a
            // copy the app writes itself; a kernel with the region write barrier
            // offers a shared one that checks stores into the exchange heap, and
            // the stub jumps there instead.
            if (s_services->RegionByRefBarrierAddress == 0
                || !ByRefAssignRefStub.PatchToBarrier(s_services->RegionByRefBarrierAddress))
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
            SharpOS.Std.NoRuntime.GcHeap.s_heapTag = "app";

            // The allocator's critical section: the kernel preempts app code,
            // and two of this app's threads must not be inside the heap at
            // once. The kernel heap does the same with Preemption.Suppress.
            AppPreemption.Install(s_services->PreemptionDepthAddress);
            if (s_services->PreemptionPendingAddress != 0 && s_services->YieldAddress != 0)
                AppPreemption.InstallDeferred(s_services->PreemptionPendingAddress, s_services->YieldAddress);

            // The exchange heap, for the write barrier: stores into a region
            // are checked, everything else passes on one compare.
            if (s_services->ExchangeArenaSpan != 0)
                SharpOS.Std.Exchange.ExchangeArena.Install(s_services->ExchangeArenaLow,
                    s_services->ExchangeArenaSpan, (uint*)s_services->ExchangePageTable);
            SharpOS.Std.NoRuntime.GcHeap.s_enterCritical = &AppPreemption.Suppress;
            SharpOS.Std.NoRuntime.GcHeap.s_leaveCritical = &AppPreemption.Allow;
            SharpOS.Std.NoRuntime.GcStress.HeapBroken = &OnHeapBroken;

            // Every object on this heap has its type in this image, so the
            // marker can refuse a candidate whose "MethodTable" is elsewhere —
            // which a word-by-word scan of an interrupted thread will offer
            // (pipe_plan.md, item 2). The kernel has done this for its heap
            // since step169.
            SharpOS.Std.NoRuntime.GcMark.MethodTableLow = (nint)startup->ImageBase;
            SharpOS.Std.NoRuntime.GcMark.MethodTableHigh = (nint)startup->ImageEnd;
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

                // A 32-bit counter needs the kernel's epoch to be read at
                // all. Both fields are checked: a kernel that predates them
                // leaves the width zero, and a width without a latch would be
                // worse than reading 64 bits — it would silently truncate.
                if (s_services->HpetCounterBits == 32 &&
                    s_services->HpetLatchAddress != 0)
                {
                    System.Diagnostics.Stopwatch.s_latchAddress = s_services->HpetLatchAddress;
                    System.Diagnostics.Stopwatch.s_counterIsNarrow = true;
                }

                // And into DateTime, which had no source at all: Clock.Install
                // was called from nowhere in the tree, so UtcNow answered
                // MinValue forever.
                //
                // A constant clock is not a cosmetic fault. Terminal.Gui times
                // its timeouts by comparing UtcNow.Ticks against a deadline
                // computed from UtcNow.Ticks, so with a clock that never moves
                // the deadline is never reached and *no* timeout in any program
                // here has ever fired - which is how a security key's
                // registration came to send nothing at all while the button
                // that started it reported success.
                //
                // Monotonic, not correct: the epoch is MinValue plus uptime,
                // because nothing hands an application the real date. That is
                // deliberate - year one plus twenty minutes is obviously not a
                // date, where a plausible wrong one would be believed. Anything
                // that measures an interval works; anything that wants today
                // still needs a service that does not exist yet.
                System.DateTime.Clock.Install(&UtcTicks);
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

            // What this program was started with (needs the heap: strings).
            AppHost.LoadArguments(s_services);

            // Hardware faults in this image raise this image's exception types.
            // After the heap and the statics: the factory allocates.
            if (s_services->SetHwExceptionFactoryAddress != 0)
                ((delegate* unmanaged<void*, void>)(nint)s_services->SetHwExceptionFactoryAddress)(
                    (void*)(delegate* unmanaged<int, nint>)&CreateHardwareException);

            // And names them for the kernel's unhandled-exception report.
            if (s_services->SetExceptionNamerAddress != 0)
                ((delegate* unmanaged<void*, void>)(nint)s_services->SetExceptionNamerAddress)(
                    (void*)(delegate* unmanaged<nint, nint>)&NameException);

            // Who this actually is. Every app goes through here, so no app has
            // to remember to say it, and it is said before the app can take
            // over the screen. The kernel prints its own id in the banner; an
            // app built from a different tree used to be indistinguishable
            // from one built with it, and on 2026-09-24 that cost an evening.
            Process.Mark(0);
            AppHost.WriteString("[app] ");
            AppHost.WriteString(AppBuildInfo.Name);
            AppHost.WriteString(" build ");
            AppHost.WriteString(AppBuildInfo.Id);
            AppHost.WriteChar('\n');
            Process.Mark(1);
        }

        /// <summary>Ticks of 100 ns since DateTime.MinValue, from the HPET.</summary>
        /// <remarks>
        /// Uptime wearing a date's clothes. Every consumer of DateTime here
        /// measures an interval - a timeout, an elapsed time, a rate - and an
        /// interval only needs the difference to be right.
        ///
        /// The division is by (frequency / 10_000_000) rather than a multiply
        /// first, because the HPET counter is already tens of billions of ticks
        /// a few hours in and multiplying by ten million overflows long long
        /// before that.
        /// </remarks>
        private static long UtcTicks()
        {
            ulong hz = System.Diagnostics.Stopwatch.s_frequencyHz;
            if (hz == 0) return 0;

            ulong counter = System.Diagnostics.Stopwatch.ReadCounter();

            // 10 MHz is DateTime's own unit. A faster counter divides down; a
            // slower one (the 14.3 MHz HPETs are faster, but a 1 MHz virtual
            // timer is not) multiplies up, and doing it in that order keeps
            // both from losing the whole value to integer truncation.
            const ulong TicksPerSecond = 10_000_000;
            return hz >= TicksPerSecond
                ? (long)(counter / (hz / TicksPerSecond))
                : (long)(counter * (TicksPerSecond / hz));
        }


        /// <summary>
        /// The exception object for a hardware fault in this image's code.
        /// </summary>
        /// <remarks>
        /// Called by the kernel from inside its fault handler, on the faulting
        /// thread's stack. Kinds as in AppServiceTable.SetHwExceptionFactoryAddress.
        /// A collection here is safe: the kernel links the fault's ExInfo
        /// before calling, and the root walk scans the faulting frames from it.
        /// </remarks>
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static nint CreateHardwareException(int kind)
        {
            object exception = kind == 0 ? new System.NullReferenceException()
                             : kind == 2 ? new System.DivideByZeroException()
                             : kind == 3 ? new SharpOS.Std.Pipes.RegionReferenceException()
                             : new System.AccessViolationException();
            return System.Runtime.CompilerServices.Unsafe.As<object, nint>(ref exception);
        }

        /// <summary>
        /// The name of an exception's type, for the kernel's report of an
        /// unhandled one: a literal of this image's, nothing allocated.
        /// </summary>
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static nint NameException(nint exception)
        {
            string name = SharpOS.Std.Runtime.ExceptionNames.NameOf(
                System.Runtime.CompilerServices.Unsafe.As<nint, System.Exception>(ref exception));
            return System.Runtime.CompilerServices.Unsafe.As<string, nint>(ref name);
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

        // A heap walk around a collection found a block that is not an object
        // (GcStress.VerifyHeap). Nothing after this can be trusted, so the app
        // ends here, with the address and which side broke it.
        private static void OnHeapBroken(nint at, int phase)
        {
            AppHost.WriteError("[gcstress] app heap broken at 0x");
            AppHost.WriteHex((ulong)at);
            // What lies there: a header, then what overwrote it.
            for (int i = 0; i < 6; i++)
            {
                AppHost.WriteError(i == 0 ? " bytes 0x" : " 0x");
                AppHost.WriteHex(*(ulong*)(at + i * 8));
            }
            // Why, the segment (start, objects, bump, end) and the object before.
            var seg = SharpOS.Std.NoRuntime.GcHeap.BrokenSegment;
            AppHost.WriteError(" why ");
            AppHost.WriteHex((ulong)SharpOS.Std.NoRuntime.GcHeap.BrokenReason);
            if (seg != null)
            {
                AppHost.WriteHex((ulong)seg->Start);
                AppHost.WriteHex((ulong)seg->ObjectStart);
                AppHost.WriteHex((ulong)seg->Current);
                AppHost.WriteHex((ulong)seg->End);
            }
            nint prev = SharpOS.Std.NoRuntime.GcHeap.BrokenPrevious;
            AppHost.WriteHex((ulong)prev);
            if (prev != 0)
                for (int i = 0; i < 3; i++) AppHost.WriteHex(*(ulong*)(prev + i * 8));
            AppHost.WriteHex((ulong)phase);
            AppHost.WriteError("\n");
            Fatal(phase == 1 ? "found before a mark: the program wrote it"
                             : "found after a sweep: the collector wrote it");
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
