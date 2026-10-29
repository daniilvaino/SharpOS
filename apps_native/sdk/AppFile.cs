using System;
using System.IO;

namespace SharpOS.AppSdk
{
    /// <summary>
    /// A file opened through the kernel (step197): read in pieces from the
    /// start, or written — created, cut or appended to — in pieces. What
    /// FileStream, File.WriteAllBytes and READ/WRITE stand on.
    /// </summary>
    public sealed unsafe class AppFile : IDisposable
    {
        public const int ModeRead = 0, ModeWrite = 1, ModeAppend = 2;

        /// <summary>Read and written in place, the content kept, made when missing (step208).</summary>
        public const int ModeUpdate = 3;

        private int _handle;
        private readonly string _path;

        private AppFile(int handle, string path)
        {
            _handle = handle;
            _path = path;
        }

        /// <summary>Opens <paramref name="path"/>; FileNotFoundException or IOException when it cannot.</summary>
        public static AppFile Open(string path, int mode)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            path = Process.ResolvePath(path);
            AppServiceTable* t = AppRuntime.Services;
            if (t == null || t->FileOpenAddress == 0)
                throw new IOException("cannot open '" + path + "': this kernel has no file service");
            byte* ascii = stackalloc byte[path.Length * 3 + 1];
            if (!ToAscii(path, ascii)) throw new IOException("cannot open '" + path + "': the path is not text");
            int handle;
            var status = (AppServiceStatus)((delegate* unmanaged<ulong, int, int*, int>)t->FileOpenAddress)((ulong)ascii, mode, &handle);
            if (status == AppServiceStatus.NotFound)
                throw new FileNotFoundException("Unable to find the specified file.", path);
            if (status == AppServiceStatus.InvalidParameter)
                throw new IOException("cannot open '" + path + "': " + (mode == ModeRead ? "it is a directory" : "FAT cannot hold the name, or it is a directory"));
            if (status == AppServiceStatus.LimitReached)
                throw new IOException("cannot create '" + path + "': no room in its directory or on the volume");
            if (status == AppServiceStatus.Busy)
                throw new IOException("cannot open '" + path + "' for writing: another program is writing it");
            if (status != AppServiceStatus.Ok)
                throw new IOException("cannot open '" + path + "' (status " + ((uint)status).ToString() + ")");
            return new AppFile(handle, path);
        }

        // FileManage ops (step208): AppServiceBuilder.FileManage*.
        internal const int ManageDelete = 1, ManageMakeDirectory = 2, ManageRemoveDirectory = 3, ManageStat = 4, ManageRename = 5, ManageVolume = 6;

        // The file system's reasons (OS.Hal.Fat32.Change*, negated): what a
        // refused change was refused for.
        internal const ulong ReasonNotFound = 1, ReasonBadName = 2, ReasonNoRoom = 3, ReasonDiskError = 4, ReasonBusy = 5,
                             ReasonIsDirectory = 6, ReasonNotDirectory = 7, ReasonNotEmpty = 8, ReasonExists = 9;

        /// <summary>
        /// One FileManage call on <paramref name="path"/>, already resolved:
        /// the status; <paramref name="first"/> the reason of a refused change
        /// or, for ManageStat, 1 a file / 2 a directory. Unsupported from a
        /// kernel without the service (step208).
        /// </summary>
        internal static AppServiceStatus Manage(int op, string path, out ulong first, out ulong second)
            => Manage(op, path, null, out first, out second, out _);

        internal static AppServiceStatus Manage(int op, string path, string target, out ulong first, out ulong second)
            => Manage(op, path, target, out first, out second, out _);

        /// <summary>
        /// The same with the rename's new path in <paramref name="target"/>,
        /// and the third result — when a path was last written (ManageStat).
        /// </summary>
        internal static AppServiceStatus Manage(int op, string path, string target, out ulong first, out ulong second, out ulong third)
        {
            first = 0;
            second = 0;
            third = 0;
            AppServiceTable* t = AppRuntime.Services;
            if (t == null || t->FileManageAddress == 0) return AppServiceStatus.Unsupported;
            byte* ascii = stackalloc byte[path.Length * 3 + 1];
            if (!ToAscii(path, ascii)) return AppServiceStatus.InvalidParameter;
            ulong* result = stackalloc ulong[4];
            for (int i = 0; i < 4; i++) result[i] = 0;
            // Rename: the new path's address in result[0] (stackalloc outside
            // the branch — its memory must outlive the call).
            byte* asciiTarget = stackalloc byte[target == null ? 1 : target.Length * 3 + 1];
            if (target != null)
            {
                if (!ToAscii(target, asciiTarget)) return AppServiceStatus.InvalidParameter;
                result[0] = (ulong)asciiTarget;
            }
            var status = (AppServiceStatus)((delegate* unmanaged<int, ulong, ulong*, int>)t->FileManageAddress)(op, (ulong)ascii, result);
            first = result[0];
            second = result[1];
            third = result[2];
            return status;
        }

        // A path as the kernel takes it: UTF-8 (step208; before, ASCII only),
        // a NUL at the end. The file system takes either separator. `into`
        // holds three bytes a character and one more.
        private static bool ToAscii(string path, byte* into)
            => AppHost.TryEncodePath(path, into, path.Length * 3 + 1, out _);

        public string Path => _path;

        /// <summary>Bytes read into the span; 0 at the end.</summary>
        public int Read(Span<byte> into)
        {
            if (_handle == 0) throw new ObjectDisposedException("AppFile");
            int got;
            fixed (byte* p = into)
            {
                var status = (AppServiceStatus)((delegate* unmanaged<int, byte*, int, int*, int>)AppRuntime.Services->FileReadAddress)(_handle, p, into.Length, &got);
                if (status != AppServiceStatus.Ok) throw new IOException("cannot read '" + _path + "'");
            }
            return got;
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            if (_handle == 0) throw new ObjectDisposedException("AppFile");
            if (data.Length == 0) return;
            fixed (byte* p = data)
            {
                var status = (AppServiceStatus)((delegate* unmanaged<int, byte*, int, int>)AppRuntime.Services->FileWriteAddress)(_handle, p, data.Length);
                if (status != AppServiceStatus.Ok) throw new IOException("cannot write '" + _path + "': the volume is full or failed");
            }
        }

        // ---- step208: the position, the length (FileControl) ----

        /// <summary>Where the next read or write goes; past the end, a write fills the gap with zeros.</summary>
        public long Position
        {
            get => Control(2, 0);
            set => Seek(value);
        }

        public long Length => Control(3, 0);

        public long Seek(long position)
        {
            if (position < 0) throw new IOException("An attempt was made to move the position before the beginning of the stream.");
            return Control(1, position);
        }

        /// <summary>The file cut or grown (with zeros) to <paramref name="length"/> bytes; it must be open for writing.</summary>
        public void SetLength(long length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            Control(4, length);
        }

        private long Control(int op, long value)
        {
            if (_handle == 0) throw new ObjectDisposedException("AppFile");
            AppServiceTable* t = AppRuntime.Services;
            if (t->FileControlAddress == 0) throw new NotSupportedException("this kernel cannot move in a file");
            long result;
            var status = (AppServiceStatus)((delegate* unmanaged<int, int, long, long*, int>)t->FileControlAddress)(_handle, op, value, &result);
            if (status == AppServiceStatus.Ok) return result;
            if (op == 4) throw new IOException("cannot set the length of '" + _path + "': the volume is full or failed, or the file is not open for writing");
            throw new IOException("cannot move in '" + _path + "' (status " + ((uint)status).ToString() + ")");
        }

        /// <summary>Closes the file: a written one gets its size on the disk.</summary>
        public void Dispose()
        {
            int h = _handle;
            if (h == 0) return;
            _handle = 0;
            var status = (AppServiceStatus)((delegate* unmanaged<int, int>)AppRuntime.Services->FileCloseAddress)(h);
            if (status != AppServiceStatus.Ok) throw new IOException("cannot close '" + _path + "'");
        }
    }
}
