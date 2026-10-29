// System.IO file access for the app tier, backed by the AppHost service
// table. The kernel read-file service has no offset/range protocol (one
// whole-file read per call, AppReadFileRequest) and no write service, so:
//
//   - FileStream(read) loads the entire file into memory up front (grow +
//     retry on BufferTooSmall) and serves Read/Seek from that buffer.
//     WADs are a few MB — needs the app GC pool grown past its current
//     1 MB (GcMemorySource.AppStatic) before DOOM-sized files load.
//   - Writing (step197) goes through the kernel's open files (AppFile): a
//     FileStream for writing appends as it is written; File.WriteAllBytes /
//     WriteAllText / AppendAllText; StreamWriter(path) (UTF-8); long names
//     too, with an 8.3 alias. Before step197 writes threw IOException, and
//     before pipe_plan.md item 7 they were dropped silently.
//   - Deleting and directories (step208): File.Delete, Directory.Create-
//     Directory / Delete / Exists, through the kernel's FileManage service.
//
// API shapes mirror BCL; each member documents its cut where behaviour
// differs.

using SharpOS.AppSdk;

namespace System.IO
{
    public class FileStream : Stream
    {
        private byte[] _buffer;
        private int _length;
        private int _position;

        // Writing (step197), and since step208 reading and writing anywhere:
        // the kernel file, with its own position — Seek, SetLength and Read
        // go to it. A file only read stays the whole-file buffer above (what
        // DOOM's WAD reads with seeks).
        private readonly AppFile _out;
        private readonly bool _canRead;

        public FileStream(string path, FileMode mode, FileAccess access)
        {
            if (access != FileAccess.Read)
            {
                bool exists = File.Exists(path);
                if (mode == FileMode.CreateNew && exists)
                    throw new IOException("The file '" + SharpOS.AppSdk.Process.ResolvePath(path) + "' already exists.");
                if ((mode == FileMode.Open || mode == FileMode.Truncate) && !exists)
                    throw new FileNotFoundException("Could not find file '" + SharpOS.AppSdk.Process.ResolvePath(path) + "'.", path);
                int kernelMode = mode == FileMode.Append ? AppFile.ModeAppend
                               : mode == FileMode.Open || mode == FileMode.OpenOrCreate ? AppFile.ModeUpdate
                               : AppFile.ModeWrite;            // Create, CreateNew, Truncate
                _out = AppFile.Open(path, kernelMode);
                _canRead = access == FileAccess.ReadWrite;
                return;
            }

            _buffer = File.ReadAllBytes(path);
            _length = _buffer.Length;
        }

        public FileStream(string path, FileMode mode)
            : this(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite)
        {
        }

        public override bool CanRead => _out == null || _canRead;
        public override bool CanSeek => true;
        public override bool CanWrite => _out != null;

        public override long Length => _out != null ? _out.Length : _length;

        public override long Position
        {
            get => _out != null ? _out.Position : _position;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                if (_out != null) { _out.Seek(value); return; }
                if (value > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
                _position = (int)value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_out != null)
            {
                if (!_canRead) throw new NotSupportedException("Stream does not support reading.");
                return _out.Read(new Span<byte>(buffer, offset, count));
            }
            int n = _length - _position;
            if (n > count) n = count;
            if (n <= 0) return 0;

            Array.Copy(_buffer, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long current = Position, length = Length;
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => current + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentException("Invalid seek origin."),
            };
            if (target < 0) throw new IOException("An attempt was made to move the position before the beginning of the stream.");
            Position = target;
            return target;
        }

        public override void Write(byte[] buffer, int offset, int count)
            => Write(new ReadOnlySpan<byte>(buffer, offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_out == null) throw new NotSupportedException("Stream does not support writing.");
            _out.Write(buffer);
        }

        public override void SetLength(long value)
        {
            if (_out == null) throw new NotSupportedException("Stream does not support writing.");
            _out.SetLength(value);
        }

        public override void Flush() { }

        protected override void Dispose(bool disposing) => _out?.Dispose();
    }

    public static unsafe partial class File
    {
        // A file, not a directory, as in BCL (step208); an older kernel
        // without FileManage cannot tell them apart and says yes to both.
        public static bool Exists(string path)
        {
            if (path == null || path.Length == 0) return false;
            string full = SharpOS.AppSdk.Process.ResolvePath(path);
            AppServiceStatus status = AppFile.Manage(AppFile.ManageStat, full, out ulong kind, out _);
            if (status == AppServiceStatus.Unsupported) return AppHost.FileExists(full);
            return status == AppServiceStatus.Ok && kind == 1;
        }

        // BCL (step208): a missing file is no error; a missing directory on
        // the way is; a directory is not a file to delete.
        public static void Delete(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            string full = SharpOS.AppSdk.Process.ResolvePath(path);
            AppServiceStatus status = AppFile.Manage(AppFile.ManageDelete, full, out ulong reason, out _);
            if (status == AppServiceStatus.Ok) return;
            if (status == AppServiceStatus.NotFound)
            {
                if (!Directory.Exists(Directory.ParentOf(full)))
                    throw new DirectoryNotFoundException("Could not find a part of the path '" + full + "'.");
                return;
            }
            if (reason == AppFile.ReasonIsDirectory)
                throw new UnauthorizedAccessException("Access to the path '" + full + "' is denied.");
            if (status == AppServiceStatus.Busy)
                throw new IOException("The process cannot access the file '" + full + "' because it is being used by another process.");
            throw Directory.Failure("delete", full, status);
        }

        public static void Move(string sourceFileName, string destFileName) => Move(sourceFileName, destFileName, false);

        // BCL (step208): the target must not exist, unless overwrite — then a
        // file there goes first. A directory is no file to move.
        public static void Move(string sourceFileName, string destFileName, bool overwrite)
        {
            if (sourceFileName == null) throw new ArgumentNullException(nameof(sourceFileName));
            if (destFileName == null) throw new ArgumentNullException(nameof(destFileName));
            string from = SharpOS.AppSdk.Process.ResolvePath(sourceFileName);
            string to = SharpOS.AppSdk.Process.ResolvePath(destFileName);
            if (!Exists(from)) throw new FileNotFoundException("Could not find file '" + from + "'.", from);
            if (overwrite && Exists(to) && !SamePath(from, to)) Delete(to);
            Directory.Rename(from, to);
        }

        // The same path but for letter case: renaming a file to itself in
        // another case is a rename, not an overwrite.
        private static bool SamePath(string a, string b) => a.ToUpperInvariant() == b.ToUpperInvariant();

        public static FileStream OpenRead(string path)
            => new FileStream(path, FileMode.Open, FileAccess.Read);

        // Through the kernel's open files (step197): the file is created or
        // cut, written, closed with its size.
        public static void WriteAllText(string path, string contents)
            => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(contents ?? ""));

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            using AppFile file = AppFile.Open(path, AppFile.ModeWrite);
            file.Write(bytes);
        }

        public static void AppendAllText(string path, string contents)
        {
            using AppFile file = AppFile.Open(path, AppFile.ModeAppend);
            file.Write(System.Text.Encoding.UTF8.GetBytes(contents ?? ""));
        }

        // As in BCL (step208): Create reads and writes; OpenWrite keeps the
        // content (it was Create before — a file cut where BCL would not).
        public static FileStream Create(string path) => new FileStream(path, FileMode.Create, FileAccess.ReadWrite);

        public static FileStream OpenWrite(string path) => new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write);

        public static FileStream Open(string path, FileMode mode)
            => new FileStream(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite);

        public static FileStream Open(string path, FileMode mode, FileAccess access) => new FileStream(path, mode, access);

        // The kernel's file services take no locks, so there is nothing for
        // share to permit or deny: every open behaves as FileShare.ReadWrite |
        // FileShare.Delete would.
        public static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share) => new FileStream(path, mode, access);

        // Whole-file load through the AppHost read service. The service has
        // no size query, so grow + retry: BufferTooSmall and exact-fit
        // (bytesRead == capacity, possibly truncated) both double and retry.
        public static byte[] ReadAllBytes(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            // Relative to the working directory (step197).
            string full = SharpOS.AppSdk.Process.ResolvePath(path);

            uint capacity = 256 * 1024;
            const uint MaxCapacity = 256u * 1024 * 1024;
            for (; ; )
            {
                byte[] buffer = new byte[capacity];
                AppServiceStatus status;
                uint bytesRead;
                fixed (byte* p = buffer)
                {
                    status = AppHost.TryReadFile(full, p, capacity, out bytesRead);
                }

                if (status == AppServiceStatus.NotFound)
                    throw new FileNotFoundException("Unable to find the specified file.", path);

                if (status == AppServiceStatus.BufferTooSmall ||
                    (status == AppServiceStatus.Ok && bytesRead == capacity))
                {
                    if (capacity >= MaxCapacity)
                        throw new IOException("File too large: " + path);
                    capacity *= 2;
                    continue;
                }

                if (status != AppServiceStatus.Ok)
                    throw new IOException("Read failed (" + (uint)status + "): " + path);

                byte[] result = new byte[bytesRead];
                Array.Copy(buffer, result, (int)bytesRead);
                return result;
            }
        }

        public static string[] ReadAllLines(string path)
        {
            byte[] data = ReadAllBytes(path);
            var lines = new Collections.Generic.List<string>();
            var sb = new Text.StringBuilder();
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                if (b == (byte)'\n')
                {
                    lines.Add(sb.ToString());
                    sb.Clear();
                }
                else if (b != (byte)'\r')
                {
                    sb.Append((char)b);
                }
            }
            if (sb.Length > 0) lines.Add(sb.ToString());
            return lines.ToArray();
        }

        // BCL returns a lazy IEnumerable<string>; ours reads eagerly. The
        // declared array type keeps `foreach` on the result an array walk
        // (arrays are not runtime-IEnumerable<T> in this std — see
        // docs/nativeaot-nostd-kernel-limits.md).
        public static string[] ReadLines(string path) => ReadAllLines(path);
    }

    public static unsafe partial class Directory
    {
        // Single-rooted SharpOS path model (see Bcl/Path.cs): the fake CWD
        // is the volume root.
        public static string GetCurrentDirectory() => SharpOS.AppSdk.Process.WorkingDirectory;

        // step208: through the kernel's FileManage service.
        public static bool Exists(string path)
        {
            if (path == null || path.Length == 0) return false;
            string full = SharpOS.AppSdk.Process.ResolvePath(path);
            if (IsRoot(full)) return true;
            return AppFile.Manage(AppFile.ManageStat, full, out ulong kind, out _) == AppServiceStatus.Ok && kind == 2;
        }

        /// <summary>Makes the directory and every missing one above it, as in BCL; one that exists is no error.</summary>
        public static DirectoryInfo CreateDirectory(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (path.Length == 0) throw new ArgumentException("Path cannot be the empty string or all whitespace.", nameof(path));
            string full = SharpOS.AppSdk.Process.ResolvePath(path);
            // Each prefix that ends a component, from the top.
            for (int i = 1; i <= full.Length; i++)
            {
                if (i < full.Length && full[i] != '/') continue;
                if (i > 0 && full[i - 1] == '/') continue;
                string prefix = full.Substring(0, i);
                AppServiceStatus status = AppFile.Manage(AppFile.ManageMakeDirectory, prefix, out ulong reason, out _);
                if (status == AppServiceStatus.Ok || reason == AppFile.ReasonExists) continue;
                if (reason == AppFile.ReasonNotDirectory)
                    throw new IOException("Cannot create '" + prefix + "' because a file or directory with the same name already exists.");
                throw Failure("create", prefix, status);
            }
            return new DirectoryInfo(full);
        }

        /// <summary>Removes an empty directory.</summary>
        public static void Delete(string path) => Delete(path, false);

        /// <summary>Removes the directory; with <paramref name="recursive"/>, everything in it first.</summary>
        public static void Delete(string path, bool recursive)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            string full = SharpOS.AppSdk.Process.ResolvePath(path);
            if (recursive && Exists(full))
            {
                // Names first, deletions after: the listing goes by position,
                // and a deleted entry would shift what comes after it.
                var files = new System.Collections.Generic.List<string>();
                var directories = new System.Collections.Generic.List<string>();
                byte* name = stackalloc byte[256];
                for (uint index = 0; ; index++)
                {
                    if (AppHost.TryReadDirEntry(full, index, name, 256, out FileEntry entry) != AppServiceStatus.Ok) break;
                    int length = (int)(entry.NameLength < 256 ? entry.NameLength : 256);
                    string text = System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(name, length));
                    if (text == "." || text == "..") continue;
                    (entry.IsDirectory != 0 ? directories : files).Add(full + "/" + text);
                }
                foreach (string f in files) File.Delete(f);
                foreach (string d in directories) Delete(d, true);
            }

            AppServiceStatus status = AppFile.Manage(AppFile.ManageRemoveDirectory, full, out ulong reason, out _);
            if (status == AppServiceStatus.Ok) return;
            if (status == AppServiceStatus.NotFound)
                throw new DirectoryNotFoundException("Could not find a part of the path '" + full + "'.");
            if (reason == AppFile.ReasonNotDirectory)
                throw new IOException("The directory name is invalid. : '" + full + "'");
            if (reason == AppFile.ReasonNotEmpty)
                throw new IOException("The directory is not empty. : '" + full + "'");
            throw Failure("delete", full, status);
        }

        /// <summary>Moves or renames a directory, as in BCL: the target must not exist.</summary>
        public static void Move(string sourceDirName, string destDirName)
        {
            if (sourceDirName == null) throw new ArgumentNullException(nameof(sourceDirName));
            if (destDirName == null) throw new ArgumentNullException(nameof(destDirName));
            string from = SharpOS.AppSdk.Process.ResolvePath(sourceDirName);
            if (!Exists(from)) throw new DirectoryNotFoundException("Could not find a part of the path '" + from + "'.");
            Rename(from, SharpOS.AppSdk.Process.ResolvePath(destDirName));
        }

        // The kernel's rename, for File.Move and Directory.Move (step208).
        internal static void Rename(string from, string to)
        {
            AppServiceStatus status = AppFile.Manage(AppFile.ManageRename, from, to, out ulong reason, out _);
            if (status == AppServiceStatus.Ok) return;
            if (reason == AppFile.ReasonExists)
                throw new IOException("Cannot create '" + to + "' because a file or directory with the same name already exists.");
            if (status == AppServiceStatus.NotFound)
                throw new DirectoryNotFoundException("Could not find a part of the path '" + to + "'.");
            if (reason == AppFile.ReasonBadName)
                throw new IOException("The source and destination path must be different, and a directory cannot move into itself: '" + from + "' -> '" + to + "'");
            if (status == AppServiceStatus.Busy)
                throw new IOException("The process cannot access the file '" + from + "' because it is being used by another process.");
            throw Failure("move", from, status);
        }

        // The directory a resolved path lies in ("/" for one in the root).
        internal static string ParentOf(string full)
        {
            int cut = full.Length - 1;
            while (cut > 0 && full[cut] == '/') cut--;
            while (cut > 0 && full[cut] != '/') cut--;
            return cut <= 0 ? "/" : full.Substring(0, cut);
        }

        private static bool IsRoot(string full)
        {
            for (int i = 0; i < full.Length; i++)
                if (full[i] != '/') return false;
            return true;
        }

        // What the other refusals become.
        internal static Exception Failure(string what, string path, AppServiceStatus status)
            => status == AppServiceStatus.Unsupported ? new PlatformNotSupportedException("cannot " + what + " '" + path + "': this kernel cannot change directories")
             : status == AppServiceStatus.LimitReached ? new IOException("cannot " + what + " '" + path + "': no room in its directory or on the volume")
             : status == AppServiceStatus.InvalidParameter ? new IOException("cannot " + what + " '" + path + "': FAT cannot hold the name, or the path is not ASCII")
             : status == AppServiceStatus.Busy ? new IOException("cannot " + what + " '" + path + "': it is in use")
             : new IOException("cannot " + what + " '" + path + "': the disk failed (status " + ((uint)status).ToString() + ")");
    }

    // A text file written as UTF-8 through the kernel's open files (step197).
    public class StreamWriter : IDisposable
    {
        private readonly AppFile _file;

        public StreamWriter(string path) : this(path, false) { }

        public StreamWriter(string path, bool append)
            => _file = AppFile.Open(path, append ? AppFile.ModeAppend : AppFile.ModeWrite);

        public void Write(string value)
        {
            if (!string.IsNullOrEmpty(value)) _file.Write(System.Text.Encoding.UTF8.GetBytes(value));
        }

        public void WriteLine(string value) => Write((value ?? "") + "\n");

        public void Dispose() => _file.Dispose();
    }
}
