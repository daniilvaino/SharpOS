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
//     WriteAllText / AppendAllText; StreamWriter(path) (UTF-8). New names
//     must be 8.3 (the FAT writer stores short names only). Before step197
//     writes threw IOException, and before pipe_plan.md item 7 they were
//     dropped silently.
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

        // Writing: the kernel file, and the bytes written (step197).
        private readonly AppFile _out;
        private long _written;

        public FileStream(string path, FileMode mode, FileAccess access)
        {
            if (access != FileAccess.Read)
            {
                if (mode == FileMode.Open || (mode == FileMode.CreateNew && File.Exists(path)))
                    throw new IOException("cannot write '" + path + "': only a new file, or a file cut or appended to, can be written");
                _out = AppFile.Open(path, mode == FileMode.Append ? AppFile.ModeAppend : AppFile.ModeWrite);
                return;
            }

            _buffer = File.ReadAllBytes(path);
            _length = _buffer.Length;
        }

        public FileStream(string path, FileMode mode)
            : this(path, mode, mode == FileMode.Open ? FileAccess.Read : FileAccess.Write)
        {
        }

        public override bool CanRead => _out == null;
        public override bool CanSeek => _out == null;
        public override bool CanWrite => _out != null;

        public override long Length => _out != null ? _written : _length;

        public override long Position
        {
            get => _out != null ? _written : _position;
            set
            {
                if (_out != null) throw new NotSupportedException("a file being written does not seek");
                if (value < 0 || value > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
                _position = (int)value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_out != null) throw new NotSupportedException("Stream does not support reading.");
            int n = _length - _position;
            if (n > count) n = count;
            if (n <= 0) return 0;

            Array.Copy(_buffer, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (_out != null) throw new NotSupportedException("a file being written does not seek");
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentException("Invalid seek origin."),
            };
            if (target < 0 || target > int.MaxValue) throw new IOException("Seek out of range.");
            _position = (int)target;
            return target;
        }

        public override void Write(byte[] buffer, int offset, int count)
            => Write(new ReadOnlySpan<byte>(buffer, offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_out == null) throw new NotSupportedException("Stream does not support writing.");
            _out.Write(buffer);
            _written += buffer.Length;
        }

        public override void SetLength(long value)
            => throw new NotSupportedException("Stream does not support SetLength.");

        public override void Flush() { }

        protected override void Dispose(bool disposing) => _out?.Dispose();
    }

    public static unsafe class File
    {
        public static bool Exists(string path)
        {
            if (path == null || path.Length == 0) return false;
            return AppHost.FileExists(SharpOS.AppSdk.Process.ResolvePath(path));
        }

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

        public static FileStream Create(string path) => new FileStream(path, FileMode.Create, FileAccess.Write);

        public static FileStream OpenWrite(string path) => new FileStream(path, FileMode.Create, FileAccess.Write);

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

    public static class Directory
    {
        // Single-rooted SharpOS path model (see Bcl/Path.cs): the fake CWD
        // is the volume root.
        public static string GetCurrentDirectory() => SharpOS.AppSdk.Process.WorkingDirectory;
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
