using OS.Hal;

namespace OS.Kernel.Process
{
    // Open files for programs (step197): READ streams a file of any size,
    // WRITE makes one. Published directly (no thunk), Win64:
    //   int FileOpen(byte* asciiPath, int mode, int* handle)   mode 0 read, 1 write, 2 append
    //   int FileRead(int handle, byte* dst, int cap, int* got) got 0 at the end
    //   int FileWrite(int handle, byte* src, int length)
    //   int FileClose(int handle)
    // A handle is its process's: another process's handle is refused, and a
    // process's end closes what it left open — a writer's file keeps what
    // came before it died.
    internal static unsafe partial class AppServiceBuilder
    {
        // Handle → the process that opened it (0: free).
        private static System.Collections.Generic.List<uint> s_fileOwners;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static int FileOpen(ulong pathAddress, int mode, int* handle)
        {
            if (handle == null || mode < Fat32.OpenRead || mode > Fat32.OpenAppend) return (int)AppServiceStatus.InvalidParameter;
            *handle = 0;
            if (!Fat32.Mounted) return (int)AppServiceStatus.Unsupported;
            char* path = stackalloc char[(int)MaxPathChars];
            if (!TryReadAsciiPath(pathAddress, path, MaxPathChars)) return (int)AppServiceStatus.InvalidParameter;
            int h = Fat32.Open(string.FromUtf16Z(path, (int)MaxPathChars), mode);
            if (h < 0)
                return (int)(h == -1 ? AppServiceStatus.NotFound
                           : h == -2 ? AppServiceStatus.InvalidParameter
                           : h == -3 ? AppServiceStatus.LimitReached
                           : h == -5 ? AppServiceStatus.Busy
                           : AppServiceStatus.DeviceError);
            Threading.Preemption.Suppress();
            s_fileOwners ??= new System.Collections.Generic.List<uint>();
            while (s_fileOwners.Count < h) s_fileOwners.Add(0);
            s_fileOwners[h - 1] = AppProcesses.Current?.Id ?? 0;
            Threading.Preemption.Allow();
            *handle = h;
            return (int)AppServiceStatus.Ok;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static int FileRead(int handle, byte* dst, int cap, int* got)
        {
            if (got == null || dst == null || cap < 0 || !IsMine(handle)) return (int)AppServiceStatus.InvalidParameter;
            int n = Fat32.Read(handle, dst, cap);
            *got = n < 0 ? 0 : n;
            return n < 0 ? (int)AppServiceStatus.DeviceError : (int)AppServiceStatus.Ok;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static int FileWrite(int handle, byte* src, int length)
        {
            if (src == null || length < 0) return (int)AppServiceStatus.InvalidParameter;
            if (!IsMine(handle))
            {
                LogFileRefusal("write: handle " + handle.ToString() + " is not this process's");
                return (int)AppServiceStatus.InvalidParameter;
            }
            Fat32.LastFailure = null;
            if (Fat32.Write(handle, src, length)) return (int)AppServiceStatus.Ok;
            LogFileRefusal("write: " + (Fat32.LastFailure ?? "the handle is not open for writing"));
            return (int)AppServiceStatus.DeviceError;
        }

        private static void LogFileRefusal(string what)
        {
            DebugLog.Begin(LogLevel.Info);
            UiText.Write("[file] ");
            UiText.Write(what);
            DebugLog.EndLine();
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static int FileClose(int handle)
        {
            if (!IsMine(handle)) return (int)AppServiceStatus.InvalidParameter;
            Threading.Preemption.Suppress();
            s_fileOwners[handle - 1] = 0;
            Threading.Preemption.Allow();
            return Fat32.Close(handle) ? (int)AppServiceStatus.Ok : (int)AppServiceStatus.DeviceError;
        }

        private static bool IsMine(int handle)
        {
            uint me = AppProcesses.Current?.Id ?? 0;
            return s_fileOwners != null && handle >= 1 && handle <= s_fileOwners.Count
                   && s_fileOwners[handle - 1] != 0 && s_fileOwners[handle - 1] == me;
        }

        /// <summary>A process's end: the files it left open close with what they have.</summary>
        internal static void CloseFilesOf(uint process)
        {
            if (s_fileOwners == null || process == 0) return;
            for (int i = 0; i < s_fileOwners.Count; i++)
            {
                if (s_fileOwners[i] != process) continue;
                s_fileOwners[i] = 0;
                Fat32.Close(i + 1);
            }
        }
    }
}
