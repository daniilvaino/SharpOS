using OS.Boot;

namespace OS.Hal
{
    // FAT-backed implementations of the BootInfo file/dir delegates.
    // Post-mount, Platform repoints BootInfo.{FileExists,FileReadAll,
    // FileReadIntoBuffer,DirectoryReadEntry} at these, so EVERY raw
    // bootInfo.* caller (the app-service ABI in AppServiceBuilder, the
    // guest launcher that lists \EFI\BOOT through it, the kernel ELF
    // loader) transparently reads our FAT instead of dead UEFI — one
    // seam, no per-call-site edits. char* path is null-terminated
    // UTF-16; string.FromUtf16Z bridges it to the Fs API.
    internal static unsafe class FatBootBridge
    {
        private const int MaxPath = 260;

        public static uint FileExists(char* path)
        {
            if (Fs.Current == null || path == null)
                return (uint)BootFileStatus.InvalidParameter;
            string p = string.FromUtf16Z(path, MaxPath);
            return Fs.Current.Exists(p)
                ? (uint)BootFileStatus.Ok : (uint)BootFileStatus.NotFound;
        }

        public static uint FileReadAll(char* path, void** buffer, uint* size)
        {
            if (Fs.Current == null || path == null || buffer == null || size == null)
                return (uint)BootFileStatus.InvalidParameter;
            string p = string.FromUtf16Z(path, MaxPath);
            int probe = Fs.Current.ReadFile(p, null, 0, out uint fsz);
            if (probe < 0 || fsz == 0)
            {
                *buffer = null; *size = 0;
                return (uint)BootFileStatus.NotFound;
            }
            void* buf = SharpOS.Std.NoRuntime.GcHeap.AllocateRaw(fsz);
            if (buf == null) return (uint)BootFileStatus.DeviceError;
            if (Fs.Current.ReadFile(p, (byte*)buf, (int)fsz, out fsz) < 0)
                return (uint)BootFileStatus.NotFound;
            *buffer = buf; *size = fsz;
            return (uint)BootFileStatus.Ok;
        }

        public static uint FileReadIntoBuffer(char* path, void* buffer, uint cap, uint* bytesRead)
        {
            if (Fs.Current == null || path == null || bytesRead == null)
                return (uint)BootFileStatus.InvalidParameter;
            string p = string.FromUtf16Z(path, MaxPath);
            int got = Fs.Current.ReadFile(p, (byte*)buffer, (int)cap, out _);
            if (got < 0) { *bytesRead = 0; return (uint)BootFileStatus.NotFound; }
            *bytesRead = (uint)got;
            return (uint)BootFileStatus.Ok;
        }

        public static uint DirectoryReadEntry(char* dirPath, uint index,
            char* nameBuf, uint nameChars, uint* nameLen, ulong* attrs)
        {
            if (Fs.Current == null || dirPath == null || nameBuf == null ||
                nameChars == 0 || nameLen == null || attrs == null)
                return (uint)BootFileStatus.InvalidParameter;
            string p = string.FromUtf16Z(dirPath, MaxPath);
            if (!Fs.Current.EnumDir(p, index, nameBuf, nameChars,
                    out uint nl, out ulong at))
            {
                *nameLen = 0; *attrs = 0;
                return (uint)BootFileStatus.EndOfDirectory;
            }
            *nameLen = nl; *attrs = at;
            return (uint)BootFileStatus.Ok;
        }
    }
}
