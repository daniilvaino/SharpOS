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
            if (handle == null || mode < Fat32.OpenRead || mode > Fat32.OpenUpdate) return (int)AppServiceStatus.InvalidParameter;
            *handle = 0;
            if (!Fat32.Mounted) return (int)AppServiceStatus.Unsupported;
            char* path = stackalloc char[(int)MaxPathChars];
            if (!TryReadUtf8Path(pathAddress, path, MaxPathChars)) return (int)AppServiceStatus.InvalidParameter;
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

        // int FileManage(int op, byte* asciiPath, ulong* result) (step208):
        // 1 delete a file, 2 make a directory, 3 remove an empty one, 4 what
        // the path is. See AppServiceTable.FileManageAddress.
        public const int FileManageDelete = 1, FileManageMakeDirectory = 2, FileManageRemoveDirectory = 3, FileManageStat = 4,
                         FileManageRename = 5, FileManageVolume = 6;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static int FileManage(int op, ulong pathAddress, ulong* result)
        {
            if (result == null || op < FileManageDelete || op > FileManageVolume) return (int)AppServiceStatus.InvalidParameter;
            // Rename: the new path's address comes in result[0].
            ulong targetAddress = op == FileManageRename ? result[0] : 0;
            result[0] = 0;
            result[1] = 0;
            result[2] = 0;
            result[3] = 0;
            if (!Fat32.Mounted) return (int)AppServiceStatus.Unsupported;
            if (op == FileManageVolume)
            {
                if (!Fat32.VolumeSpace(out ulong total, out ulong free)) return (int)AppServiceStatus.DeviceError;
                result[0] = total;
                result[1] = free;
                return (int)AppServiceStatus.Ok;
            }
            char* buffer = stackalloc char[(int)MaxPathChars];
            if (!TryReadUtf8Path(pathAddress, buffer, MaxPathChars)) return (int)AppServiceStatus.InvalidParameter;
            string path = string.FromUtf16Z(buffer, (int)MaxPathChars);

            if (op == FileManageStat)
            {
                if (!Fat32.StatEntry(path, out uint size, out bool isDir, out uint written)) return (int)AppServiceStatus.NotFound;
                result[0] = isDir ? 2UL : 1UL;
                result[1] = size;
                result[2] = written;
                return (int)AppServiceStatus.Ok;
            }

            int change;
            if (op == FileManageRename)
            {
                char* target = stackalloc char[(int)MaxPathChars];
                if (!TryReadUtf8Path(targetAddress, target, MaxPathChars)) return (int)AppServiceStatus.InvalidParameter;
                change = Fat32.Rename(path, string.FromUtf16Z(target, (int)MaxPathChars));
            }
            else
            {
                change = op == FileManageDelete ? Fat32.DeleteFile(path)
                       : op == FileManageMakeDirectory ? Fat32.CreateDirectory(path)
                       : Fat32.RemoveDirectory(path);
            }
            result[0] = (ulong)(uint)(-change);
            // Only a failing disk is worth a line: "exists" and "not found"
            // are the everyday answers (CreateDirectory walks existing
            // parents, File.Delete of a missing file is no error).
            if (change == Fat32.ChangeDiskError)
                LogFileRefusal((op == FileManageDelete ? "delete " : op == FileManageMakeDirectory ? "mkdir " : op == FileManageRename ? "rename " : "rmdir ")
                               + path + ": disk error");
            return (int)(change == Fat32.ChangeOk ? AppServiceStatus.Ok
                       : change == Fat32.ChangeNotFound ? AppServiceStatus.NotFound
                       : change == Fat32.ChangeNoRoom ? AppServiceStatus.LimitReached
                       : change == Fat32.ChangeBusy ? AppServiceStatus.Busy
                       : change == Fat32.ChangeDiskError ? AppServiceStatus.DeviceError
                       : AppServiceStatus.InvalidParameter);
        }

        // int FileControl(int handle, int op, long value, long* result)
        // (step208): see AppServiceTable.FileControlAddress.
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static int FileControl(int handle, int op, long value, long* result)
        {
            if (result == null || !IsMine(handle)) return (int)AppServiceStatus.InvalidParameter;
            *result = 0;
            switch (op)
            {
                case 1:
                    long moved = Fat32.Seek(handle, value);
                    if (moved < 0) return (int)AppServiceStatus.InvalidParameter;
                    *result = moved;
                    return (int)AppServiceStatus.Ok;
                case 2:
                    *result = Fat32.PositionOf(handle);
                    return (int)AppServiceStatus.Ok;
                case 3:
                    *result = Fat32.SizeOf(handle);
                    return (int)AppServiceStatus.Ok;
                case 4:
                    Fat32.LastFailure = null;
                    if (Fat32.SetLength(handle, value)) return (int)AppServiceStatus.Ok;
                    LogFileRefusal("set length: " + (Fat32.LastFailure ?? "not open for writing, or the volume refused"));
                    return (int)AppServiceStatus.DeviceError;
                default:
                    return (int)AppServiceStatus.InvalidParameter;
            }
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
