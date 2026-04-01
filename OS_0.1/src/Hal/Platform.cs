using OS.Boot;

namespace OS.Hal
{
    internal static unsafe class Platform
    {
        private static BootInfo s_bootInfo;
        private static bool s_initialized;

        public static PlatformCapabilities Capabilities => s_bootInfo.Capabilities;

        public static void Init(BootInfo bootInfo)
        {
            s_bootInfo = bootInfo;
            s_initialized = true;
        }

        public static BootInfo GetBootInfo() => s_bootInfo;

        public static bool HasCapability(PlatformCapabilities capability)
        {
            return (Capabilities & capability) == capability;
        }

        public static void WriteChar(char value)
        {
            if (!s_initialized)
                return;

            if (!HasCapability(PlatformCapabilities.TextOutput))
                return;

            if (s_bootInfo.WriteChar == null)
                return;

            s_bootInfo.WriteChar(value);
        }

        public static void Write(string text)
        {
            if (!s_initialized)
                return;

            fixed (char* p = text)
            {
                for (int i = 0; i < text.Length; i++)
                    WriteChar(p[i]);
            }
        }

        public static void WriteLine(string text)
        {
            Write(text);
            WriteChar('\n');
        }

        public static void Shutdown()
        {
            if (!s_initialized)
                return;

            if (!HasCapability(PlatformCapabilities.Shutdown))
            {
                Halt();
                return;
            }

            if (s_bootInfo.Shutdown == null)
            {
                Halt();
                return;
            }

            s_bootInfo.Shutdown();
        }

        public static void Halt()
        {
            while (true) ;
        }
    }
}
