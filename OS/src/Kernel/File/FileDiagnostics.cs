using OS.Hal;

namespace OS.Kernel.File
{
    internal static unsafe class FileDiagnostics
    {
        private const uint MaxDumpEntries = 64;

        public static void DumpDirectory(string path)
        {
            DebugLog.Begin(LogLevel.Info);
            UiText.Write("dir ");
            UiText.Write(path);
            DebugLog.EndLine();

            uint index = 0;
            char* nameBuffer = stackalloc char[(int)FileInfoLite.MaxNameChars];

            while (index < MaxDumpEntries &&
                FileSystem.TryReadDirectoryEntry(path, index, nameBuffer, FileInfoLite.MaxNameChars, out uint nameLength, out uint isDirectory))
            {
                DebugLog.Begin(LogLevel.Info);
                UiText.Write(isDirectory != 0 ? "dir: " : "file: ");
                WriteName(nameBuffer, nameLength);
                DebugLog.EndLine();

                index++;
            }

            uint listed = FileSystem.List(path);

            DebugLog.Begin(LogLevel.Info);
            UiText.Write("dir entries: ");
            UiText.WriteUInt(listed);
            DebugLog.EndLine();
        }

        private static void WriteName(char* name, uint nameLength)
        {
            for (uint i = 0; i < nameLength; i++)
                UiText.WriteChar(name[i]);
        }
    }
}
