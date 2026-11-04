using OS.Boot;
using OS.Hal;

namespace OS.Kernel
{
    internal static unsafe class SystemBanner
    {
        // Written per build by ImageInfo.Generator (BuildId from OS.csproj).
        // Format: <git-short-sha>[-<tag-from-build-tag.txt>]. See run_build.ps1.
        internal static string BuildId => SharpOS.Generated.ImageInfo.BuildId;

        public static void Print(BootInfo bootInfo)
        {
            Log.Write(LogLevel.Info, "SharpOS 0.1");

            Log.Begin(LogLevel.Info);
            Console.Write("build: ");
            Console.Write(BuildId);
            Log.EndLine();

            // Numbers taken on a Debug fork and on a Release one are not
            // comparable; the log says which one this is.
            Log.Begin(LogLevel.Info);
            Console.Write("fork: ");
            Console.Write(SharpOS.Generated.ImageInfo.ForkConfig);
            Log.EndLine();

            // Same reason: tiering off changes every number the hosted tier
            // produces (Probes.HostedTieredCompilation).
            Log.Begin(LogLevel.Info);
            Console.Write("hosted tiering: ");
            Console.Write(OS.Kernel.Diagnostics.Probes.HostedTieredCompilation ? "on" : "off");
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("boot: ");
            Console.Write(BootModeName(bootInfo.BootMode));
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("fw: ");
            WriteFirmwareVendor(ref bootInfo);
            Console.Write(" / rev ");
            Console.WriteInt((int)bootInfo.FirmwareRevision);
            Log.EndLine();
        }

        private static string BootModeName(BootMode mode)
        {
            switch (mode)
            {
                case BootMode.Uefi: return "UEFI";
                default: return "UNKNOWN";
            }
        }

        // Character by character from the firmware's buffer: the banner runs
        // before the kernel heap, and there is nothing to allocate a string in.
        private static void WriteFirmwareVendor(ref BootInfo bootInfo)
        {
            char* vendor = bootInfo.FirmwareVendor;
            if (vendor == null || vendor[0] == '\0')
            {
                Console.Write("unknown");
                return;
            }

            for (int i = 0; i < 63 && vendor[i] != '\0'; i++)
                Console.WriteChar(vendor[i]);
        }

    }
}
