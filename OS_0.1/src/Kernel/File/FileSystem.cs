using OS.Boot;
using OS.Hal;

namespace OS.Kernel.File
{
    internal static unsafe class FileSystem
    {
        private const uint MaxEntryNameChars = FileInfoLite.MaxNameChars;
        private const ulong EfiFileAttributeDirectory = 0x0000000000000010UL;

        public static bool Init()
        {
            return Platform.HasCapability(PlatformCapabilities.ExternalElf);
        }

        public static bool Exists(string path)
        {
            return Platform.FileExists(path);
        }

        public static bool ReadAll(string path, out FileBuffer fileBuffer)
        {
            fileBuffer = default;
            if (!Platform.TryReadFile(path, out void* buffer, out uint length))
                return false;

            fileBuffer = new FileBuffer(buffer, length);
            return fileBuffer.IsValid;
        }

        public static uint List(string directoryPath)
        {
            uint count = 0;
            uint index = 0;
            char* nameBuffer = stackalloc char[(int)MaxEntryNameChars];

            while (TryReadDirectoryEntry(directoryPath, index, nameBuffer, MaxEntryNameChars, out _, out _))
            {
                count++;
                if (index == 0xFFFFFFFFU)
                    break;

                index++;
            }

            return count;
        }

        public static bool TryReadDirectoryEntry(
            string directoryPath,
            uint index,
            char* nameBuffer,
            uint nameBufferChars,
            out uint nameLength,
            out uint isDirectory)
        {
            nameLength = 0;
            isDirectory = 0;

            if (nameBuffer == null || nameBufferChars == 0)
                return false;

            if (!Platform.TryReadDirectoryEntry(
                directoryPath,
                index,
                nameBuffer,
                nameBufferChars,
                out nameLength,
                out ulong attributes))
            {
                return false;
            }

            if (nameLength == 0)
                return false;

            isDirectory = (attributes & EfiFileAttributeDirectory) == EfiFileAttributeDirectory ? 1U : 0U;
            return true;
        }
    }
}
