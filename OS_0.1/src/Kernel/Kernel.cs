using OS.Boot;
using OS.Hal;
using OS.TestApp;

namespace OS.Kernel
{
    internal static class KernelMain
    {
        public static void Start(BootInfo bootInfo)
        {
            SystemBanner.Print(bootInfo);
            Log.Write(LogLevel.Info, "kernel start");

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
            }

            DemoApp.Run();
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
