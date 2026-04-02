using OS.Boot;
using OS.Hal;

namespace OS.Kernel
{
    internal static unsafe class SystemBanner
    {
        public static void Print(BootInfo bootInfo)
        {
            Log.Write(LogLevel.Info, "SharpOS 0.1");

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

            Log.Begin(LogLevel.Info);
            Console.Write("caps: ");
            WriteCapabilities(bootInfo.Capabilities);
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

        private static void WriteFirmwareVendor(ref BootInfo bootInfo)
        {
            if (bootInfo.FirmwareVendor == null)
            {
                Console.Write("unknown");
                return;
            }

            int i = 0;
            while (i < 63 && bootInfo.FirmwareVendor[i] != '\0')
            {
                Console.WriteChar(bootInfo.FirmwareVendor[i]);
                i++;
            }

            if (i == 0)
                Console.Write("unknown");
        }

        private static void WriteCapabilities(PlatformCapabilities capabilities)
        {
            if (capabilities == PlatformCapabilities.None)
            {
                Console.Write("None");
                return;
            }

            bool first = true;
            WriteCapability(capabilities, PlatformCapabilities.TextOutput, "TextOutput", ref first);
            WriteCapability(capabilities, PlatformCapabilities.Shutdown, "Shutdown", ref first);
            WriteCapability(capabilities, PlatformCapabilities.MemoryMap, "MemoryMap", ref first);
            WriteCapability(capabilities, PlatformCapabilities.Graphics, "Graphics", ref first);
            WriteCapability(capabilities, PlatformCapabilities.MonotonicTimer, "MonotonicTimer", ref first);
            WriteCapability(capabilities, PlatformCapabilities.ExternalElf, "ExternalElf", ref first);
            WriteCapability(capabilities, PlatformCapabilities.KeyboardInput, "KeyboardInput", ref first);
        }

        private static void WriteCapability(PlatformCapabilities all, PlatformCapabilities one, string name, ref bool first)
        {
            if ((all & one) != one)
                return;

            if (!first)
                Console.Write(" ");

            Console.Write(name);
            first = false;
        }
    }
}
