using OS.Hal;

namespace OS.Kernel.File
{
    internal static unsafe class FileDiagnostics
    {
        private const uint MaxDumpEntries = 64;

        public static void DumpDirectory(string path)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("dir ");
            Console.Write(path);
            Log.EndLine();

            uint index = 0;
            char* nameBuffer = stackalloc char[(int)FileInfoLite.MaxNameChars];

            while (index < MaxDumpEntries &&
                FileSystem.TryReadDirectoryEntry(path, index, nameBuffer, FileInfoLite.MaxNameChars, out uint nameLength, out uint isDirectory))
            {
                Log.Begin(LogLevel.Info);
                Console.Write(isDirectory != 0 ? "dir: " : "file: ");
                WriteName(nameBuffer, nameLength);
                Log.EndLine();

                index++;
            }

            uint listed = FileSystem.List(path);

            Log.Begin(LogLevel.Info);
            Console.Write("dir entries: ");
            Console.WriteUInt(listed);
            Log.EndLine();
        }

        private static void WriteName(char* name, uint nameLength)
        {
            for (uint i = 0; i < nameLength; i++)
                Console.WriteChar(name[i]);
        }
    }
}
