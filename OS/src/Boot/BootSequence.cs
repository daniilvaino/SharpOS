using OS.Hal;
using OS.Hal.Acpi;
using OS.Hal.Idt;
using OS.Kernel;
using OS.Kernel.Diagnostics;
using OS.Kernel.Elf;
using OS.Kernel.Input;
using OS.Kernel.Memory;
using OS.Kernel.Paging;
using OS.TestApp;
using SharpOS.Std.NoRuntime;
using HpetTimer = OS.Hal.Timer.Hpet;

namespace OS.Boot
{
    // Single source of truth for kernel boot ordering. Each phase has
    // explicit prerequisites (assumed ready when phase enters) and explicit
    // post-conditions (what becomes available for later phases).
    //
    // High-level reference: docs/boot-order.md.
    // Probe toggles: OS/src/Kernel/Diagnostics/Probes.cs.
    //
    // Phase boundaries are not just style — every dependency between
    // subsystems matters. Adding new init? Pick the earliest phase whose
    // post-conditions cover what your code uses, and put it there.
    //
    //   Phase0_Critical    panic mode, IDT (faults are diagnosable)
    //   Phase1_Memory      physical pages, kernel heap
    //   Phase2_Runtime     exec stubs + managed runtime + GC + cctor materialization
    //   Phase3_Platform    pager + ACPI + HPET (hardware abstractions)
    //   Phase4_Probes      smoke tests + diagnostics + verification
    //   Phase5_Apps        ELF validation, launcher
    internal static unsafe class BootSequence
    {
        public static void Run(BootInfo bootInfo)
        {
            Phase0_Critical(bootInfo);

            if ((bootInfo.Capabilities & PlatformCapabilities.MemoryMap) != PlatformCapabilities.MemoryMap)
            {
                Log.Write(LogLevel.Warn, "memory map not implemented — skipping memory-dependent phases");
                Phase5_Apps(bootInfo);   // DemoApp still runs (no memory needed)
                return;
            }

            Phase1_Memory(bootInfo);
            Phase2_Runtime(bootInfo);
            Phase3_Platform(bootInfo);
            Phase4_Probes(bootInfo);
            Phase5_Apps(bootInfo);
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase 0 — Critical. Must run first.
        //
        //   Pre:  CPU in long-mode, UEFI BootServices active, BootInfo populated.
        //   Post: Any kernel-side fault produces a readable PanicDump.
        //         Console output works (UEFI ConOut).
        // ─────────────────────────────────────────────────────────────────
        private static void Phase0_Critical(BootInfo bootInfo)
        {
            Panic.Mode = PanicMode.Shutdown;

            bool idtOk = Idt.Install(bootInfo);

            SystemBanner.Print(bootInfo);
            Log.Write(idtOk ? LogLevel.Info : LogLevel.Warn,
                idtOk ? "idt installed" : "idt install failed");
            Log.Write(LogLevel.Info, "kernel start");

            if (Probes.KeyboardInput)
                InputDiagnostics.Run();
            else
                Console.WriteLine("skipped keyboard demo");
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase 1 — Memory.
        //
        //   Pre:  Console + IDT.
        //   Post: PhysicalMemory.AllocPage works.
        //         KernelHeap.Alloc/Free works.
        //         NumberFormatting can allocate (KernelHeap-backed string allocator).
        // ─────────────────────────────────────────────────────────────────
        private static void Phase1_Memory(BootInfo bootInfo)
        {
            PrintMemorySummary(bootInfo);
            PhysicalMemory.Init(bootInfo.MemoryMap);
            Log.Write(LogLevel.Info, "early allocator ready");

            PrintAllocatedPage(PhysicalMemory.AllocPage());
            PrintAllocatedPage(PhysicalMemory.AllocPage());
            PrintAllocatedPage(PhysicalMemory.AllocPage());

            InitializeHeap();

            if (Probes.KernelHeapSmoke)
                KernelHeapSmokeTest.Run();
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase 2 — Runtime. Assembles the managed runtime.
        //
        //   Pre:  KernelHeap, console, IDT.
        //   Post: `new` works (objects, arrays, strings).
        //         Shared-generic interface dispatch works.
        //         RTR sections walked, TypeManager populated.
        //         Canonical `static readonly T x = new T()` works.
        // ─────────────────────────────────────────────────────────────────
        private static void Phase2_Runtime(BootInfo bootInfo)
        {
            // Exec-memory stubs first — InterfaceDispatchBridge / GcStackSpill
            // / ByRefAssignRef shellcodes need EfiLoaderCode buffer mapped.
            if (bootInfo.ExecStubBuffer != null)
            {
                X64PageTable.SetExecBuffer(bootInfo.ExecStubBuffer, bootInfo.ExecStubBufferSize);
                if (!GcStackSpill.TryInitialize(bootInfo.ExecStubBuffer, bootInfo.ExecStubBufferSize))
                    Log.Write(LogLevel.Warn, "gc spill trampoline unavailable");

                InstallInterfaceDispatchBridge(bootInfo);
                InstallByRefAssignRefShellcode();
                InstallPortIoShellcode();
                InstallCaptureContextShellcode();
                InstallThrowExShellcode();
                InstallCallCatchFuncletShellcode();
                InstallRethrowShellcode();
                InstallCallFinallyFuncletShellcode();
                InstallCallFilterFuncletShellcode();
                // Test harness (5.5a) wired separately from EhProbe so the
                // patcher addresses live in EhProbe statics at install time.
                OS.Kernel.Diagnostics.EhProbe.InstallStep5_5TestHarness();
            }
            if (bootInfo.JumpStubExecBuffer != null)
                X64PageTable.SetJumpStubBuffer(bootInfo.JumpStubExecBuffer, bootInfo.JumpStubExecBufferSize);

            // GC heap.
            if (!GcHeap.Init())
                Panic.Fail("gc heap init failed");

            // Force-init NativeAotModuleInit (RTR walking + TypeManager).
            // Anchor MT for the scan = `EETypePtrOf<object>()` intrinsic,
            // returns an MT pointer in our binary's data section.
            var anchor = (GcMethodTable*)System.EETypePtr.EETypePtrOf<object>().ToPointer();
            if (!NativeAotModuleInit.TryInitialize(anchor))
                Log.Write(LogLevel.Warn, "naot module init failed");

            // Coff RUNTIME_FUNCTION table — Phase 1 step 2. Locates the
            // PE image, parses .pdata, sets up binary search for IP ->
            // method resolution. Required by future EH dispatcher and
            // step 3 EH info decoder.
            if (!OS.Boot.EH.CoffRuntimeFunctionTable.TryInitialize((byte*)anchor))
                Log.Write(LogLevel.Warn, "coff method table init failed");

            // GC statics materialization. After this, canonical
            // `static readonly T x = new T()` works for any code that
            // runs in subsequent phases.
            if (!GcStaticsMaterializer.Materialize())
                Log.Write(LogLevel.Warn, "gc statics materialization failed");
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase 3 — Platform. Hardware abstractions.
        //
        //   Pre:  Runtime ready.
        //   Post: Pager (4-level page tables), ACPI tables parsed, HPET
        //         counter running, Stopwatch usable.
        // ─────────────────────────────────────────────────────────────────
        private static void Phase3_Platform(BootInfo bootInfo)
        {
            InitializePager();
            RunPagerValidation();
            InitializeAcpi(bootInfo);
            InitializeHpet();

            if (Probes.RtcSnapshot)
                DumpRtcSnapshot();
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase 4 — Probes. Self-tests and verification.
        //
        //   Pre:  All previous phases done.
        //   Post: Confidence that everything works.
        //
        // Toggle individual probes in Diagnostics/Probes.cs.
        // ─────────────────────────────────────────────────────────────────
        private static void Phase4_Probes(BootInfo bootInfo)
        {
            if (Probes.GcHeapSmoke)
                GcHeapSmokeTest.Run();

            if (Probes.GcStaticsSummary)
                GcStaticsMaterializer.DumpMaterializedSummary();

            if (Probes.GcStress)
                GcStressTest.Run();

            if (Probes.NativeAotFeatures)
                NativeAotProbe.Run();

            if (Probes.Cctor)
                CctorProbe.Run();

            // EH probe — three levels (try/finally no-throw, try/catch
            // no-throw, try/catch with throw). L3 currently halts.
            EhProbe.Run();

            // Never-returning probes — last so a regular boot still finishes.
            if (Probes.IdtPanic)
                IdtProbe.TriggerNullDeref();

            if (Probes.ExceptionThrow)
                ExceptionProbe.TriggerThrow();
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase 5 — Apps. User-visible workloads.
        //
        //   Pre:  Memory + runtime + platform + probes (or nothing if
        //         memory map missing — Phase 5 still runs DemoApp).
        //   Post: Process complete; kernel idles.
        // ─────────────────────────────────────────────────────────────────
        private static void Phase5_Apps(BootInfo bootInfo)
        {
            if ((bootInfo.Capabilities & PlatformCapabilities.MemoryMap) == PlatformCapabilities.MemoryMap)
            {
                ElfValidation.Run(bootInfo);
            }
            DemoApp.Run();
        }

        // ─────────────────────────────────────────────────────────────────
        // Per-subsystem initializers (extracted for readability).
        // ─────────────────────────────────────────────────────────────────

        private static void InitializeHeap()
        {
            if (!KernelHeap.Init())
                Panic.Fail("kernel heap init failed");

            Log.Write(LogLevel.Info, "heap init ok");
            HeapDiagnostics.DumpSummary();
        }

        private static void InstallInterfaceDispatchBridge(BootInfo bootInfo)
        {
            bool ok = InterfaceDispatchPatcher.TryInstall(
                bootInfo.ExecStubBuffer,
                bootInfo.ExecStubBufferSize,
                &InterfaceDispatchResolver.Resolve,
                &InterfaceDispatchResolver.Fail);

            Log.Write(ok ? LogLevel.Info : LogLevel.Warn,
                ok ? "iface dispatch bridge installed" : "iface dispatch bridge install failed");
        }

        private static void InstallByRefAssignRefShellcode()
        {
            bool ok = ByRefAssignRefPatcher.TryInstall();
            Log.Write(ok ? LogLevel.Info : LogLevel.Warn,
                ok ? "byref-assign shellcode installed" : "byref-assign shellcode install failed");
        }

        private static void InstallPortIoShellcode()
        {
            bool ok = PortIoPatcher.TryInstall();
            Log.Write(ok ? LogLevel.Info : LogLevel.Warn,
                ok ? "port-io shellcode installed" : "port-io shellcode install failed");
        }

        private static void InstallCaptureContextShellcode()
        {
            bool ok = OS.Boot.EH.CaptureContextPatcher.TryInstall();
            Log.Write(ok ? LogLevel.Info : LogLevel.Warn,
                ok ? "capture-context shellcode installed" : "capture-context shellcode install failed");
        }

        private static void InstallThrowExShellcode()
        {
            bool ok = OS.Boot.EH.ThrowExPatcher.TryInstall();
            Log.Write(ok ? LogLevel.Info : LogLevel.Warn,
                ok ? "throw-ex shellcode installed" : "throw-ex shellcode install failed");
        }

        private static void InstallCallCatchFuncletShellcode()
        {
            bool ok = OS.Boot.EH.CallCatchFuncletPatcher.TryInstall();
            Log.Write(ok ? LogLevel.Info : LogLevel.Warn,
                ok ? "call-catch-funclet shellcode installed" : "call-catch-funclet shellcode install failed");
        }

        private static void InstallRethrowShellcode()
        {
            bool ok = OS.Boot.EH.RethrowPatcher.TryInstall();
            Log.Write(ok ? LogLevel.Info : LogLevel.Warn,
                ok ? "rethrow shellcode installed" : "rethrow shellcode install failed");
        }

        private static void InstallCallFinallyFuncletShellcode()
        {
            bool ok = OS.Boot.EH.CallFinallyFuncletPatcher.TryInstall();
            Log.Write(ok ? LogLevel.Info : LogLevel.Warn,
                ok ? "call-finally-funclet shellcode installed" : "call-finally-funclet shellcode install failed");
        }

        private static void InstallCallFilterFuncletShellcode()
        {
            bool ok = OS.Boot.EH.CallFilterFuncletPatcher.TryInstall();
            Log.Write(ok ? LogLevel.Info : LogLevel.Warn,
                ok ? "call-filter-funclet shellcode installed" : "call-filter-funclet shellcode install failed");
        }

        private static void InitializePager()
        {
            PagingRequirements requirements = default;
            requirements.PageSize = X64PageTable.PageSize;
            requirements.DirectMapBase = 0xFFFF800000000000UL;
            requirements.InitialPageTablePages = 4;

            if (!Pager.Init(requirements))
                Panic.Fail("pager init failed");

            Log.Write(LogLevel.Info, "pager init ok");
            PagingDiagnostics.DumpSummary();

            Pager.GetSummary(out PagingSummary summary);
            if (summary.PageSize != requirements.PageSize)
                Panic.Fail("pager requirements mismatch: page size");

            if (summary.SpareTablePages < requirements.InitialPageTablePages)
                Panic.Fail("pager requirements mismatch: initial spare table pages");

            Log.Write(LogLevel.Info, "pager requirements applied");
        }

        private static void RunPagerValidation()
        {
            PagingValidation.Run();
            PagingDiagnostics.DumpSummary();
        }

        private static void InitializeAcpi(BootInfo bootInfo)
        {
            bool ok = Acpi.Init(bootInfo.SystemTable);
            if (!ok)
            {
                Log.Write(LogLevel.Warn, "acpi init failed");
                return;
            }

            Log.Begin(LogLevel.Info);
            Console.Write("acpi xsdt entries: ");
            Console.WriteUInt((uint)Acpi.XsdtEntryCount);
            Log.EndLine();

            if (Madt.IsAvailable)
            {
                Log.Begin(LogLevel.Info);
                Console.Write("acpi madt: lapic=0x");
                Console.WriteHex(Madt.LocalApicAddress, 8);
                Console.Write(" cpus=");
                Console.WriteUInt((uint)Madt.LocalApicCount);
                Console.Write(" ioapics=");
                Console.WriteUInt((uint)Madt.IoApicCount);
                Log.EndLine();
            }

            if (Hpet.IsAvailable)
            {
                Log.Begin(LogLevel.Info);
                Console.Write("acpi hpet: base=0x");
                Console.WriteHex(Hpet.Base, 8);
                Log.EndLine();
            }

            if (Mcfg.IsAvailable)
            {
                Log.Begin(LogLevel.Info);
                Console.Write("acpi mcfg: entries=");
                Console.WriteUInt((uint)Mcfg.EntryCount);
                if (Mcfg.TryGetEntry(0, out ulong baseAddr, out ushort seg, out byte startBus, out byte endBus))
                {
                    Console.Write(" seg0: base=0x");
                    Console.WriteHex(baseAddr, 8);
                    Console.Write(" bus=");
                    Console.WriteUInt(startBus);
                    Console.Write("..");
                    Console.WriteUInt(endBus);
                }
                Log.EndLine();
            }
        }

        private static void InitializeHpet()
        {
            if (!HpetTimer.Init())
            {
                Log.Write(LogLevel.Warn, "hpet init failed");
                return;
            }

            Log.Begin(LogLevel.Info);
            Console.Write("hpet: freq=");
            Console.WriteULong(HpetTimer.FrequencyHz);
            Console.Write(" Hz period=");
            Console.WriteULong(HpetTimer.PeriodFemtoseconds);
            Console.Write(" fs comparators=");
            Console.WriteUInt((uint)HpetTimer.NumComparators);
            Console.Write(" 64bit=");
            Console.Write(HpetTimer.Is64BitCounter ? "yes" : "no");
            Log.EndLine();

            ulong t0 = HpetTimer.ReadCounter();
            for (int i = 0; i < 100_000; i++) { /* burn cycles */ }
            ulong t1 = HpetTimer.ReadCounter();

            Log.Begin(LogLevel.Info);
            Console.Write("hpet counter delta: ");
            Console.WriteULong(t1 - t0);
            Console.Write(" ticks");
            Log.EndLine();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ulong target = HpetTimer.ReadCounter() + HpetTimer.FrequencyHz / 1000;
            while (HpetTimer.ReadCounter() < target) { /* busy */ }
            sw.Stop();

            Log.Begin(LogLevel.Info);
            Console.Write("stopwatch ~1ms spin: elapsed_us=");
            Console.WriteULong((ulong)sw.ElapsedMicroseconds);
            Console.Write(" elapsed_ms=");
            Console.WriteULong((ulong)sw.ElapsedMilliseconds);
            Log.EndLine();
        }

        // ─────────────────────────────────────────────────────────────────
        // Diagnostic output helpers.
        // ─────────────────────────────────────────────────────────────────

        private static void DumpRtcSnapshot()
        {
            if (!Rtc.TryRead(out Rtc.Snapshot snap))
            {
                Log.Write(LogLevel.Warn, "rtc read failed");
                return;
            }

            Log.Begin(LogLevel.Info);
            Console.Write("rtc: ");
            Console.WriteUInt(snap.Year);
            Console.Write("-");
            WriteTwoDigit(snap.Month);
            Console.Write("-");
            WriteTwoDigit(snap.Day);
            Console.Write(" ");
            WriteTwoDigit(snap.Hour);
            Console.Write(":");
            WriteTwoDigit(snap.Minute);
            Console.Write(":");
            WriteTwoDigit(snap.Second);
            Console.Write(" UTC dow=");
            Console.WriteUInt(snap.Weekday);
            Console.Write(" centuryReg=");
            Console.Write(snap.CenturyValid ? "yes" : "no");
            Log.EndLine();
        }

        private static void WriteTwoDigit(byte v)
        {
            if (v < 10) Console.Write("0");
            Console.WriteUInt(v);
        }

        private static void PrintMemorySummary(BootInfo bootInfo)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("memory regions: ");
            Console.WriteUInt(bootInfo.MemoryMap.RegionCount);
            Log.EndLine();

            ulong usablePages = MemoryDiagnostics.CountUsablePages(bootInfo.MemoryMap);
            Log.Begin(LogLevel.Info);
            Console.Write("usable pages: ");
            Console.WriteULong(usablePages);
            Log.EndLine();
        }

        private static void PrintAllocatedPage(ulong address)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("alloc page: ");
            if (address == 0)
            {
                Console.Write("none");
            }
            else
            {
                Console.Write("0x");
                Console.WriteHex(address, 8);
            }
            Log.EndLine();
        }
    }
}
