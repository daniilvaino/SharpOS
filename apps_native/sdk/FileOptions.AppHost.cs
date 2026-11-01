// FileOptions, FileStreamOptions, UnixFileMode (release 0.1, for
// System.Formats.Tar and the archives after it) — BCL's shapes and values.
// FileStream takes FileStreamOptions' mode and access; buffer size, share and
// the options themselves change nothing here (the kernel's open files are
// unbuffered and not shared), except DeleteOnClose, which deletes on Dispose.

namespace System.IO
{
    [Flags]
    public enum FileOptions
    {
        None = 0,
        WriteThrough = unchecked((int)0x80000000),
        Asynchronous = 0x40000000,
        RandomAccess = 0x10000000,
        DeleteOnClose = 0x04000000,
        SequentialScan = 0x08000000,
        Encrypted = 0x00004000,
    }

    [Flags]
    public enum UnixFileMode
    {
        None = 0,
        OtherExecute = 1,
        OtherWrite = 2,
        OtherRead = 4,
        GroupExecute = 8,
        GroupWrite = 16,
        GroupRead = 32,
        UserExecute = 64,
        UserWrite = 128,
        UserRead = 256,
        StickyBit = 512,
        SetGroup = 1024,
        SetUser = 2048,
    }

    public sealed class FileStreamOptions
    {
        private FileMode _mode = FileMode.Open;
        private FileAccess _access = FileAccess.Read;
        private FileShare _share = FileShare.Read;
        private FileOptions _options;
        private long _preallocationSize;
        private int _bufferSize = 4096;
        private UnixFileMode? _unixCreateMode;

        public FileMode Mode
        {
            get => _mode;
            set
            {
                if (value < FileMode.CreateNew || value > FileMode.Append) throw new ArgumentOutOfRangeException(nameof(value));
                _mode = value;
            }
        }

        public FileAccess Access
        {
            get => _access;
            set
            {
                if (value < FileAccess.Read || value > FileAccess.ReadWrite) throw new ArgumentOutOfRangeException(nameof(value));
                _access = value;
            }
        }

        public FileShare Share
        {
            get => _share;
            set => _share = value;
        }

        public FileOptions Options
        {
            get => _options;
            set => _options = value;
        }

        public long PreallocationSize
        {
            get => _preallocationSize;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _preallocationSize = value;
            }
        }

        public int BufferSize
        {
            get => _bufferSize;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _bufferSize = value;
            }
        }

        public UnixFileMode? UnixCreateMode
        {
            get => _unixCreateMode;
            set => _unixCreateMode = value;
        }
    }

    public static partial class File
    {
        /// <summary>FAT keeps no Unix modes: as BCL on Windows.</summary>
        public static UnixFileMode GetUnixFileMode(string path) =>
            throw new PlatformNotSupportedException("The file system keeps no Unix modes.");

        public static void SetUnixFileMode(string path, UnixFileMode mode) =>
            throw new PlatformNotSupportedException("The file system keeps no Unix modes.");
    }
}
