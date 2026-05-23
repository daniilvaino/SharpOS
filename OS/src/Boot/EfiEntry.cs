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

            // step104: switch off the UEFI-provided boot stack onto our
            // own .bss-resident BootStackPool BEFORE anything allocates
            // physical pages. Otherwise PhysicalMemory.Init would mark
            // the still-active boot stack region as "Usable" and hand
            // its pages out to KernelHeap / GC, leading to silent
            // overwrites of GC object m_pMT fields. See
            // done/step104.md (BootStackSwitch) and pal-minimal-audit.md.
            //
            // Activate() never returns — the continuation calls
            // KernelMain.Start and then Platform.Shutdown/Halt on the
            // owned stack.
            BootStackSwitch.Activate();

            // unreachable
            Platform.Halt();
        }
    }
}
