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
                if (bootInfo.ExecStubBuffer != null)
                    X64PageTable.SetExecBuffer(bootInfo.ExecStubBuffer, bootInfo.ExecStubBufferSize);
                if (bootInfo.JumpStubExecBuffer != null)
                    X64PageTable.SetJumpStubBuffer(bootInfo.JumpStubExecBuffer, bootInfo.JumpStubExecBufferSize);
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
