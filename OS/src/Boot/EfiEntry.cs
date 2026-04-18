using System;
using OS.Hal;
using OS.Kernel;

namespace OS.Boot
{
    internal static unsafe class EfiEntry
    {
        static void Main() { }

        [System.Runtime.RuntimeExport("EfiMain")]
        static long EfiMain(IntPtr imageHandle, EFI_SYSTEM_TABLE* systemTable)
        {
            BootContext context;
            context.ImageHandle = imageHandle;
            context.SystemTable = systemTable;

            Boot.Entry(context);
            return 0;
        }
    }

    internal static class Boot
    {
        public static void Entry(BootContext context)
        {
            BootInfo bootInfo = UefiBootInfoBuilder.Build(context);
            Platform.Init(bootInfo);
            KernelMain.Start(bootInfo);
            Platform.Shutdown();
            Platform.Halt();
        }
    }
}
