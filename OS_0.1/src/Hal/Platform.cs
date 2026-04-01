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

        public static bool FileExists(string path)
        {
            if (!s_initialized)
                return false;

            if (!HasCapability(PlatformCapabilities.ExternalElf))
                return false;

            if (s_bootInfo.FileExists == null)
                return false;

            fixed (char* pathPointer = path)
            {
                if (pathPointer == null || pathPointer[0] == '\0')
                    return false;

                return s_bootInfo.FileExists(pathPointer) == (uint)BootFileStatus.Ok;
            }
        }

        public static bool TryReadFile(string path, out void* buffer, out uint size)
        {
            buffer = null;
            size = 0;

            if (!s_initialized)
                return false;

            if (!HasCapability(PlatformCapabilities.ExternalElf))
                return false;

            if (s_bootInfo.FileReadAll == null)
                return false;

            fixed (char* pathPointer = path)
            {
                if (pathPointer == null || pathPointer[0] == '\0')
                    return false;

                void* readBuffer = null;
                uint readSize = 0;
                uint status = s_bootInfo.FileReadAll(pathPointer, &readBuffer, &readSize);
                if (status != (uint)BootFileStatus.Ok)
                    return false;

                buffer = readBuffer;
                size = readSize;
                return true;
            }
        }

        public static bool TryReadDirectoryEntry(
            string directoryPath,
            uint index,
            char* nameBuffer,
            uint nameBufferChars,
            out uint nameLength,
            out ulong attributes)
        {
            nameLength = 0;
            attributes = 0;

            if (!s_initialized)
                return false;

            if (!HasCapability(PlatformCapabilities.ExternalElf))
                return false;

            if (s_bootInfo.DirectoryReadEntry == null || nameBuffer == null || nameBufferChars == 0)
                return false;

            fixed (char* pathPointer = directoryPath)
            {
                if (pathPointer == null || pathPointer[0] == '\0')
                    return false;

                uint readLength = 0;
                ulong readAttributes = 0;
                uint status = s_bootInfo.DirectoryReadEntry(
                    pathPointer,
                    index,
                    nameBuffer,
                    nameBufferChars,
                    &readLength,
                    &readAttributes);

                if (status != (uint)BootFileStatus.Ok)
                    return false;

                nameLength = readLength;
                attributes = readAttributes;
                return true;
            }
        }

        public static void Halt()
        {
            while (true) ;
        }
    }
}
