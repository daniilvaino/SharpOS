// System.IO file access for the app tier, backed by the AppHost service
// table. The kernel read-file service has no offset/range protocol (one
// whole-file read per call, AppReadFileRequest) and no write service, so:
//
//   - FileStream(read) loads the entire file into memory up front (grow +
//     retry on BufferTooSmall) and serves Read/Seek from that buffer.
//     WADs are a few MB — needs the app GC pool grown past its current
//     1 MB (GcMemorySource.AppStatic) before DOOM-sized files load.
//   - Writing — FileStream for writing, File.WriteAllText/WriteAllBytes,
//     StreamWriter(path) — throws IOException. Until pipe_plan.md item 7 these
//     accepted the data and dropped it: a savegame reported "saved" and was
//     gone. A program that tolerates a missing file catches the exception
//     (DOOM's config save does); one that does not now ends with the reason.
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

        public FileStream(string path, FileMode mode, FileAccess access)
        {
            if (access != FileAccess.Read)
                throw File.WritesNotSupported(path);

            _buffer = File.ReadAllBytes(path);
            _length = _buffer.Length;
        }

        public FileStream(string path, FileMode mode)
            : this(path, mode, mode == FileMode.Open ? FileAccess.Read : FileAccess.Write)
        {
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
                _position = (int)value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _length - _position;
            if (n > count) n = count;
            if (n <= 0) return 0;

            Array.Copy(_buffer, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
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
            => throw new NotSupportedException("Stream does not support writing.");

        public override void SetLength(long value)
            => throw new NotSupportedException("Stream does not support writing.");

        public override void Flush() { }
    }

    public static unsafe class File
    {
        public static bool Exists(string path)
        {
            if (path == null || path.Length == 0) return false;
            return AppHost.FileExists(path);
        }

        public static FileStream OpenRead(string path)
            => new FileStream(path, FileMode.Open, FileAccess.Read);

        // No kernel write service behind the app service table yet. These used
        // to return silently, on the reasoning that a program logging to a file
        // should not die because the log went nowhere; the cost was that every
        // write looked like it worked — savegames, state saves, config — and
        // nothing ever said otherwise. A program that can live without the file
        // catches the exception.
        public static void WriteAllText(string path, string contents)
            => throw WritesNotSupported(path);

        public static void WriteAllBytes(string path, byte[] bytes)
            => throw WritesNotSupported(path);

        internal static IOException WritesNotSupported(string path)
            => new IOException("cannot write '" + path + "': the kernel has no file write service for programs yet");

        // Whole-file load through the AppHost read service. The service has
        // no size query, so grow + retry: BufferTooSmall and exact-fit
        // (bytesRead == capacity, possibly truncated) both double and retry.
        public static byte[] ReadAllBytes(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));

            uint capacity = 256 * 1024;
            const uint MaxCapacity = 256u * 1024 * 1024;
            for (; ; )
            {
                byte[] buffer = new byte[capacity];
                AppServiceStatus status;
                uint bytesRead;
                fixed (byte* p = buffer)
                {
                    status = AppHost.TryReadFile(path, p, capacity, out bytesRead);
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
        public static string GetCurrentDirectory() => "\\";
    }

    // Text writer over the missing write service: refuses at construction,
    // like FileStream for writing.
    public class StreamWriter : IDisposable
    {
        public StreamWriter(string path) { throw File.WritesNotSupported(path); }

        public void Write(string value) { }

        public void WriteLine(string value) { }

        public void Dispose() { }
    }
}
