using OS.Boot;
using OS.Kernel.Elf;
using OS.Hal;
using OS.Kernel.Diagnostics;
using OS.Kernel.Input;
using OS.Kernel.Memory;
using OS.Kernel.Paging;
using OS.TestApp;

namespace OS.Kernel
{
    internal static unsafe class KernelMain
    {
        public static void Start(BootInfo bootInfo)
        {
            Panic.Mode = PanicMode.Shutdown;

            SystemBanner.Print(bootInfo);
            Log.Write(LogLevel.Info, "kernel start");
            RunKeyboardDiagnostics();

            if ((bootInfo.Capabilities & PlatformCapabilities.MemoryMap) != PlatformCapabilities.MemoryMap)
            {
                Log.Write(LogLevel.Warn, "memory map not implemented");
            }
            else
            {
                PrintMemorySummary(bootInfo);
                PhysicalMemory.Init(bootInfo.MemoryMap);
                Log.Write(LogLevel.Info, "early allocator ready");

                PrintAllocatedPage(PhysicalMemory.AllocPage());
                PrintAllocatedPage(PhysicalMemory.AllocPage());
                PrintAllocatedPage(PhysicalMemory.AllocPage());

                InitializeHeap();
                RunHeapSmokeTest();

                // Install exec-memory stubs BEFORE the GC test — the smoke
                // test calls MarkAll through the register-spill trampoline.
                if (bootInfo.ExecStubBuffer != null)
                {
                    X64PageTable.SetExecBuffer(bootInfo.ExecStubBuffer, bootInfo.ExecStubBufferSize);
                    if (!GcStackSpill.TryInitialize(bootInfo.ExecStubBuffer, bootInfo.ExecStubBufferSize))
                        Log.Write(LogLevel.Warn, "gc spill trampoline unavailable");
                }
                if (bootInfo.JumpStubExecBuffer != null)
                    X64PageTable.SetJumpStubBuffer(bootInfo.JumpStubExecBuffer, bootInfo.JumpStubExecBufferSize);

                RunGcHeapNoNewTest();
                GcStressTest.Run();
                InitializePager();
                RunPagerValidation();
                RunElfValidation(bootInfo);
            }

            DemoApp.Run();
        }

        private static void RunKeyboardDiagnostics()
        {
            if (false)
            {
                InputDiagnostics.Run();
            }
            else
            {
                Console.WriteLine("skipped keyboard demo");
            }
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

        private static void InitializeHeap()
        {
            if (!KernelHeap.Init())
                Panic.Fail("kernel heap init failed");

            Log.Write(LogLevel.Info, "heap init ok");
            HeapDiagnostics.DumpSummary();
        }

        private static void RunHeapSmokeTest()
        {
            void* alloc16 = KernelHeap.Alloc(16);
            PrintHeapAddress("heap alloc 16", alloc16);

            void* alloc64 = KernelHeap.Alloc(64);
            PrintHeapAddress("heap alloc 64", alloc64);

            void* alloc256 = KernelHeap.Alloc(256);
            PrintHeapAddress("heap alloc 256", alloc256);

            if (alloc16 != null)
            {
                KernelHeap.Free(alloc16);
                PrintHeapAddress("heap free", alloc16);
            }

            void* alloc8 = KernelHeap.Alloc(8);
            PrintHeapAddress("heap alloc 8", alloc8);

            HeapDiagnostics.DumpSummary();
            HeapDiagnostics.DumpBlocks();
        }

        // Static fields to keep allocated objects alive (prevents DCE).
        private static object s_keep1;
        private static object s_keep2;
        private static object s_keep3;

        private static void RunGcHeapNoNewTest()
        {
            Log.Write(LogLevel.Info, "---- gc heap test begin ----");

            if (!SharpOS.Std.NoRuntime.GcHeap.Init())
            {
                Log.Write(LogLevel.Error, "gc heap init failed");
                return;
            }

            // Test managed allocation via NativeAOT `new` keyword.
            // All three paths go through our [RuntimeExport] stubs into GcHeap.
            Log.Write(LogLevel.Info, "gc: new object()...");
            s_keep1 = new object();
            Log.Write(LogLevel.Info, "gc: new object() ok");

            Log.Write(LogLevel.Info, "gc: new int[5]...");
            s_keep2 = new int[5];
            Log.Write(LogLevel.Info, "gc: new int[5] ok");

            Log.Write(LogLevel.Info, "gc: new string(x,3)...");
            s_keep3 = new string('x', 3);
            Log.Write(LogLevel.Info, "gc: new string(x,3) ok");

            Log.Begin(LogLevel.Info);
            Console.Write("gc final: count=");
            Console.WriteULongRaw(SharpOS.Std.NoRuntime.GcHeap.AllocCount);
            Console.Write(" bytes=");
            Console.WriteULongRaw(SharpOS.Std.NoRuntime.GcHeap.AllocBytes);
            Log.EndLine();

            // Phase 3.4 + 3.5 smoke-test: register s_keep1 as a static root;
            // others remain unregistered and should be swept. MarkAll runs
            // through the register-spill trampoline so refs in callee-saved
            // regs become visible to the conservative stack scan.
            SharpOS.Std.NoRuntime.GcRoots.CaptureStackTop();
            SharpOS.Std.NoRuntime.GcRoots.Register(ref s_keep1);

            Log.Begin(LogLevel.Info);
            Console.Write("roots: registered=");
            Console.WriteUIntRaw((uint)SharpOS.Std.NoRuntime.GcRoots.Count);
            Console.Write(" stackTop=0x");
            Console.WriteHexRaw((ulong)SharpOS.Std.NoRuntime.GcRoots.StackTop, 16);
            Log.EndLine();

            SharpOS.Std.NoRuntime.GcMark.Begin();

            // Call MarkAll through the register-spill trampoline so refs
            // living in callee-saved regs (RBX, R12..R15, etc.) get pushed
            // onto the stack and become visible to the conservative scan.
            delegate* unmanaged<void> markFn = &SharpOS.Std.NoRuntime.GcRoots.MarkAllUnmanaged;
            GcStackSpill.Invoke(markFn);

            Log.Begin(LogLevel.Info);
            Console.Write("mark: marked=");
            Console.WriteUIntRaw(SharpOS.Std.NoRuntime.GcMark.LastMarkedCount);
            Log.EndLine();

            SharpOS.Std.NoRuntime.GcSweep.Run();

            Log.Begin(LogLevel.Info);
            Console.Write("sweep: kept=");
            Console.WriteUIntRaw(SharpOS.Std.NoRuntime.GcSweep.LastKeptCount);
            Console.Write(" swept=");
            Console.WriteUIntRaw(SharpOS.Std.NoRuntime.GcSweep.LastSweptCount);
            Log.EndLine();

            Log.Write(LogLevel.Info, "---- gc heap test end ----");
        }

        // Helper: extract raw address from a managed reference.
        // `ref` + `fixed` pattern — takes address of local, casts to nint*, reads.
        private static nint GetObjectAddress(object obj)
        {
            if (obj == null) return 0;
            // The local `obj` is a managed reference (pointer to object).
            // Read its value via address-of-local cast.
            return *(nint*)&obj;
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

            // TablePages now includes cloned kernel page-table hierarchy + reserve pages.
            // The requirements contract is about pre-reserved spare pages for later mappings.
            if (summary.SpareTablePages < requirements.InitialPageTablePages)
                Panic.Fail("pager requirements mismatch: initial spare table pages");

            Log.Write(LogLevel.Info, "pager requirements applied");
        }

        private static void RunPagerValidation()
        {
            PagingValidation.Run();
            PagingDiagnostics.DumpSummary();
        }

        private static void RunElfValidation(BootInfo bootInfo)
        {
            ElfValidation.Run(bootInfo);
        }

        private static void PrintHeapAddress(string label, void* pointer)
        {
            Log.Begin(LogLevel.Info);
            Console.Write(label);
            Console.Write(" -> ");

            if (pointer == null)
            {
                Console.Write("none");
            }
            else
            {
                Console.Write("0x");
                Console.WriteHex((ulong)pointer, 8);
            }

            Log.EndLine();
        }
    }
}
