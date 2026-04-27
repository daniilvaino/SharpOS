using OS.Boot;

namespace OS.Kernel
{
    // Kernel entry point. The actual boot ordering lives in
    // OS.Boot.BootSequence — this file is just the dispatch site for
    // EfiEntry → BootSequence.Run.
    //
    // For the boot sequence itself, see OS/src/Boot/BootSequence.cs.
    // For phase dependencies and reasoning, see docs/boot-order.md.
    internal static class KernelMain
    {
        public static void Start(BootInfo bootInfo)
        {
            BootSequence.Run(bootInfo);
        }
    }
}
