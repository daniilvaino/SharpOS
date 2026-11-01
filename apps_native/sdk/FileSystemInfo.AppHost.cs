// System.IO for the app tier, the rest of step208: listing directories,
// FileInfo / DirectoryInfo, File.Copy, last-write times, DriveInfo — over the
// kernel's FileManage service (AppFile.Manage) and its directory listing
// (AppHost.TryReadDirEntry). BCL shapes; each type names its cuts.

using System.Collections.Generic;
using SharpOS.AppSdk;

namespace System.IO
{
    public enum SearchOption
    {
        TopDirectoryOnly = 0,
        AllDirectories = 1,
    }

    // BCL's values (FileAttributes.cs), verbatim.
    [Flags]
    public enum FileAttributes
    {
        None = 0,
        ReadOnly = 0x0001,
        Hidden = 0x0002,
        System = 0x0004,
        Directory = 0x0010,
        Archive = 0x0020,
        Device = 0x0040,
        Normal = 0x0080,
        Temporary = 0x0100,
        SparseFile = 0x0200,
        ReparsePoint = 0x0400,
        Compressed = 0x0800,
        Offline = 0x1000,
        NotContentIndexed = 0x2000,
        Encrypted = 0x4000,
        IntegrityStream = 0x8000,
        NoScrubData = 0x20000,
    }

    public static unsafe partial class File
    {
        /// <summary>
        /// What a path is (step208): Directory, or Archive for a file — the
        /// FAT attribute bits themselves are not reported yet. A missing path
        /// throws as in BCL: FileNotFoundException, or DirectoryNotFoundException
        /// when a directory on the way is missing.
        /// </summary>
        public static FileAttributes GetAttributes(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            string full = SharpOS.AppSdk.Process.ResolvePath(path);
            bool root = true;
            foreach (char c in full) if (c != '/') root = false;
            if (root) return FileAttributes.Directory;
            if (AppFile.Manage(AppFile.ManageStat, full, out ulong kind, out _) == AppServiceStatus.Ok)
                return kind == 2 ? FileAttributes.Directory : FileAttributes.Archive;
            if (!Directory.Exists(Directory.ParentOf(full)))
                throw new DirectoryNotFoundException("Could not find a part of the path '" + full + "'.");
            throw new FileNotFoundException("Could not find file '" + full + "'.", full);
        }

        public static void Copy(string sourceFileName, string destFileName) => Copy(sourceFileName, destFileName, false);

        // BCL: the target must not exist unless overwrite; a directory is no
        // file. Streamed in pieces, so a file of any size copies.
        public static void Copy(string sourceFileName, string destFileName, bool overwrite)
        {
            if (sourceFileName == null) throw new ArgumentNullException(nameof(sourceFileName));
            if (destFileName == null) throw new ArgumentNullException(nameof(destFileName));
            string from = SharpOS.AppSdk.Process.ResolvePath(sourceFileName);
            string to = SharpOS.AppSdk.Process.ResolvePath(destFileName);
            if (!Exists(from)) throw new FileNotFoundException("Could not find file '" + from + "'.", from);
            if (Directory.Exists(to)) throw new IOException("The target file '" + to + "' is a directory, not a file.");
            if (Exists(to) && !overwrite) throw new IOException("The file '" + to + "' already exists.");
            using AppFile input = AppFile.Open(from, AppFile.ModeRead);
            using AppFile output = AppFile.Open(to, AppFile.ModeWrite);
            var piece = new byte[64 * 1024];
            while (true)
            {
                int n = input.Read(piece);
                if (n == 0) break;
                output.Write(new ReadOnlySpan<byte>(piece, 0, n));
            }
        }

        /// <summary>When the file was last written; 1601-01-01 when there is none, as in BCL.</summary>
        public static DateTime GetLastWriteTime(string path) => Directory.LastWrite(path);

        public static DateTime GetLastWriteTimeUtc(string path) => Directory.LastWrite(path).ToUniversalTime();

        /// <summary>
        /// Sets when a file was last written (release 0.1): FAT keeps local time
        /// to two seconds, from 1980 to 2107, and nothing else — it is an
        /// ArgumentOutOfRangeException outside that range, as BCL's on FAT.
        /// </summary>
        public static void SetLastWriteTime(string path, DateTime lastWriteTime) => Directory.SetWrite(path, lastWriteTime);

        public static void SetLastWriteTimeUtc(string path, DateTime lastWriteTimeUtc) =>
            Directory.SetWrite(path, DateTime.SpecifyKind(lastWriteTimeUtc, DateTimeKind.Utc));

        public static string ReadAllText(string path) => System.Text.Encoding.UTF8.GetString(ReadAllBytes(path));
    }

    public static unsafe partial class Directory
    {
        public static string[] GetFiles(string path) => GetFiles(path, "*", SearchOption.TopDirectoryOnly);
        public static string[] GetFiles(string path, string searchPattern) => GetFiles(path, searchPattern, SearchOption.TopDirectoryOnly);
        public static string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
            => List(path, searchPattern, searchOption, files: true, directories: false);

        public static string[] GetDirectories(string path) => GetDirectories(path, "*", SearchOption.TopDirectoryOnly);
        public static string[] GetDirectories(string path, string searchPattern) => GetDirectories(path, searchPattern, SearchOption.TopDirectoryOnly);
        public static string[] GetDirectories(string path, string searchPattern, SearchOption searchOption)
            => List(path, searchPattern, searchOption, files: false, directories: true);

        public static string[] GetFileSystemEntries(string path) => GetFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly);
        public static string[] GetFileSystemEntries(string path, string searchPattern) => GetFileSystemEntries(path, searchPattern, SearchOption.TopDirectoryOnly);
        public static string[] GetFileSystemEntries(string path, string searchPattern, SearchOption searchOption)
            => List(path, searchPattern, searchOption, files: true, directories: true);

        // Cut: BCL enumerates lazily; these read the directory at once.
        public static IEnumerable<string> EnumerateFiles(string path) => GetFiles(path);
        public static IEnumerable<string> EnumerateFiles(string path, string searchPattern) => GetFiles(path, searchPattern);
        public static IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) => GetFiles(path, searchPattern, searchOption);
        public static IEnumerable<string> EnumerateDirectories(string path) => GetDirectories(path);
        public static IEnumerable<string> EnumerateDirectories(string path, string searchPattern) => GetDirectories(path, searchPattern);
        public static IEnumerable<string> EnumerateDirectories(string path, string searchPattern, SearchOption searchOption) => GetDirectories(path, searchPattern, searchOption);
        public static IEnumerable<string> EnumerateFileSystemEntries(string path) => GetFileSystemEntries(path);
        public static IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern) => GetFileSystemEntries(path, searchPattern);

        public static DateTime GetLastWriteTime(string path) => LastWrite(path);

        public static DateTime GetLastWriteTimeUtc(string path) => LastWrite(path).ToUniversalTime();

        public static void SetLastWriteTime(string path, DateTime lastWriteTime) => SetWrite(path, lastWriteTime);

        public static void SetLastWriteTimeUtc(string path, DateTime lastWriteTimeUtc) =>
            SetWrite(path, DateTime.SpecifyKind(lastWriteTimeUtc, DateTimeKind.Utc));

        // A time of either kind onto the path, as FAT stores it: local, to two seconds.
        internal static void SetWrite(string path, DateTime time)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            string full = SharpOS.AppSdk.Process.ResolvePath(path);
            DateTime local = time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time;
            if (local.Year < 1980 || local.Year > 2107)
                throw new ArgumentOutOfRangeException(nameof(time), "The file system keeps times from 1980 to 2107.");
            uint stamp = ToFat(local);
            AppServiceStatus status = AppFile.SetWriteTime(full, stamp);
            if (status == AppServiceStatus.NotFound)
                throw new FileNotFoundException("Could not find file '" + full + "'.", full);
            if (status != AppServiceStatus.Ok)
                throw new IOException("The time of '" + full + "' could not be set.");
        }

        // FromFat's reverse: date in the high 16 bits, time in the low.
        internal static uint ToFat(DateTime local)
        {
            uint date = (uint)(((local.Year - 1980) << 9) | (local.Month << 5) | local.Day);
            uint time = (uint)((local.Hour << 11) | (local.Minute << 5) | (local.Second / 2));
            return (date << 16) | time;
        }

        // The names in a directory, each as Path.Combine(path, name) — path
        // as the caller wrote it, as in BCL. A pattern of * and ? matches the
        // name without regard to case.
        private static string[] List(string path, string pattern, SearchOption option, bool files, bool directories)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            if (!Exists(path)) throw new DirectoryNotFoundException("Could not find a part of the path '" + SharpOS.AppSdk.Process.ResolvePath(path) + "'.");
            var found = new List<string>();
            Walk(path, pattern, option == SearchOption.AllDirectories, files, directories, found);
            return found.ToArray();
        }

        private static void Walk(string path, string pattern, bool deep, bool files, bool directories, List<string> found)
        {
            var below = new List<string>();
            foreach (Entry e in Entries(SharpOS.AppSdk.Process.ResolvePath(path)))
            {
                string full = Path.Combine(path, e.Name);
                if ((e.IsDirectory ? directories : files) && Matches(e.Name, pattern)) found.Add(full);
                if (deep && e.IsDirectory) below.Add(full);
            }
            foreach (string d in below) Walk(d, pattern, true, files, directories, found);
        }

        internal struct Entry
        {
            public string Name;
            public bool IsDirectory;
        }

        // The entries of a resolved directory path, "." and ".." left out.
        internal static List<Entry> Entries(string full)
        {
            var entries = new List<Entry>();
            byte* name = stackalloc byte[256];
            for (uint index = 0; ; index++)
            {
                if (AppHost.TryReadDirEntry(full, index, name, 256, out FileEntry entry) != AppServiceStatus.Ok) break;
                int length = (int)(entry.NameLength < 256 ? entry.NameLength : 256);
                string text = System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(name, length));
                if (text == "." || text == "..") continue;
                entries.Add(new Entry { Name = text, IsDirectory = entry.IsDirectory != 0 });
            }
            return entries;
        }

        // * any run, ? one character; case does not matter (FAT's names do not).
        internal static bool Matches(string name, string pattern)
        {
            if (pattern == "*" || pattern == "*.*") return true;
            return Match(name, 0, pattern, 0);
        }

        private static bool Match(string s, int i, string p, int j)
        {
            while (j < p.Length)
            {
                char c = p[j];
                if (c == '*')
                {
                    for (int k = i; k <= s.Length; k++)
                        if (Match(s, k, p, j + 1)) return true;
                    return false;
                }
                if (i >= s.Length) return false;
                if (c != '?' && char.ToUpperInvariant(c) != char.ToUpperInvariant(s[i])) return false;
                i++;
                j++;
            }
            return i == s.Length;
        }

        internal static DateTime LastWrite(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            string full = SharpOS.AppSdk.Process.ResolvePath(path);
            if (AppFile.Manage(AppFile.ManageStat, full, null, out _, out _, out ulong written) != AppServiceStatus.Ok)
                return new DateTime(1601, 1, 1);
            return FromFat((uint)written);
        }

        // FAT's date (high 16 bits: years since 1980, month, day) and time
        // (low: hours, minutes, two-second units); 1980-01-01 when unset.
        internal static DateTime FromFat(uint stamp)
        {
            int date = (int)(stamp >> 16), time = (int)(stamp & 0xFFFF);
            int year = 1980 + (date >> 9), month = (date >> 5) & 15, day = date & 31;
            int hour = time >> 11, minute = (time >> 5) & 63, second = (time & 31) * 2;
            if (month < 1 || month > 12 || day < 1 || day > 31 || hour > 23 || minute > 59 || second > 59)
                return new DateTime(1980, 1, 1);
            if (day > DateTime.DaysInMonth(year, month)) day = DateTime.DaysInMonth(year, month);
            return new DateTime(year, month, day, hour, minute, second);
        }
    }

    /// <summary>
    /// FileInfo's and DirectoryInfo's base, as in BCL (release 0.1). Cuts: the
    /// driver reports no creation or access time — both read the last write;
    /// FAT has no Unix modes (UnixFileMode throws, as BCL on Windows), no
    /// links (LinkTarget is null), and Attributes are Directory or Archive.
    /// </summary>
    public abstract class FileSystemInfo
    {
        protected string FullPath = string.Empty;
        protected string OriginalPath = string.Empty;

        protected FileSystemInfo() { }

        public virtual string FullName => FullPath;
        public abstract string Name { get; }
        public abstract bool Exists { get; }
        public string Extension => Path.GetExtension(FullPath);

        public FileAttributes Attributes
        {
            get => File.GetAttributes(FullPath);
            set => throw new PlatformNotSupportedException("The file system keeps no attributes beyond a directory's.");
        }

        public DateTime LastWriteTime
        {
            get => System.IO.Directory.LastWrite(FullPath);
            set => System.IO.Directory.SetWrite(FullPath, value);
        }

        public DateTime LastWriteTimeUtc
        {
            get => LastWriteTime.ToUniversalTime();
            set => System.IO.Directory.SetWrite(FullPath, DateTime.SpecifyKind(value, DateTimeKind.Utc));
        }

        // SharpOS cut: no creation or access time from the driver — the last write.
        public DateTime CreationTime { get => LastWriteTime; set => LastWriteTime = value; }
        public DateTime CreationTimeUtc { get => LastWriteTimeUtc; set => LastWriteTimeUtc = value; }
        public DateTime LastAccessTime { get => LastWriteTime; set => LastWriteTime = value; }
        public DateTime LastAccessTimeUtc { get => LastWriteTimeUtc; set => LastWriteTimeUtc = value; }

        /// <summary>FAT has no links: always null.</summary>
        public string LinkTarget => null;

        public FileSystemInfo ResolveLinkTarget(bool returnFinalTarget) => null;

        public void CreateAsSymbolicLink(string pathToTarget) =>
            throw new PlatformNotSupportedException("The file system has no links.");

        public UnixFileMode UnixFileMode
        {
            get => throw new PlatformNotSupportedException("The file system keeps no Unix modes.");
            set => throw new PlatformNotSupportedException("The file system keeps no Unix modes.");
        }

        public abstract void Delete();

        /// <summary>Every property asks the kernel anew: nothing to refresh.</summary>
        public void Refresh() { }

        public override string ToString() => OriginalPath;
    }

    /// <summary>
    /// A file by its path (step208). Cut: Open* other than through File.
    /// </summary>
    public sealed class FileInfo : FileSystemInfo
    {
        public FileInfo(string fileName)
        {
            if (fileName == null) throw new ArgumentNullException(nameof(fileName));
            OriginalPath = fileName;
            FullPath = SharpOS.AppSdk.Process.ResolvePath(fileName);
        }

        public override string Name => Path.GetFileName(FullName);
        public string DirectoryName => System.IO.Directory.ParentOf(FullName);
        public DirectoryInfo Directory => new DirectoryInfo(DirectoryName);
        public override bool Exists => File.Exists(FullName);

        /// <summary>The size in bytes; FileNotFoundException when there is no such file.</summary>
        public long Length
        {
            get
            {
                if (AppFile.Manage(AppFile.ManageStat, FullName, out ulong kind, out ulong size) != AppServiceStatus.Ok || kind != 1)
                    throw new FileNotFoundException("Could not find file '" + FullName + "'.", FullName);
                return (long)size;
            }
        }

        public FileInfo CopyTo(string destFileName) => CopyTo(destFileName, false);
        public FileInfo CopyTo(string destFileName, bool overwrite)
        {
            File.Copy(FullName, destFileName, overwrite);
            return new FileInfo(destFileName);
        }

        public void MoveTo(string destFileName) => File.Move(FullName, destFileName);
        public override void Delete() => File.Delete(FullName);
    }

    /// <summary>
    /// A directory by its path (step208). Cut: EnumerateFiles' laziness.
    /// </summary>
    public sealed class DirectoryInfo : FileSystemInfo
    {
        public DirectoryInfo(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            OriginalPath = path;
            FullPath = SharpOS.AppSdk.Process.ResolvePath(path);
        }

        public override string Name
        {
            get
            {
                int end = FullName.Length;
                while (end > 1 && FullName[end - 1] == '/') end--;
                int start = end;
                while (start > 0 && FullName[start - 1] != '/') start--;
                return end <= start ? FullName : FullName.Substring(start, end - start);
            }
        }

        public override bool Exists => System.IO.Directory.Exists(FullName);

        /// <summary>The directory above; null for the root.</summary>
        public DirectoryInfo Parent
        {
            get
            {
                foreach (char c in FullName) if (c != '/') return new DirectoryInfo(System.IO.Directory.ParentOf(FullName));
                return null;
            }
        }

        public void Create() => System.IO.Directory.CreateDirectory(FullName);
        public override void Delete() => System.IO.Directory.Delete(FullName);
        public void Delete(bool recursive) => System.IO.Directory.Delete(FullName, recursive);
        public void MoveTo(string destDirName) => System.IO.Directory.Move(FullName, destDirName);

        public FileInfo[] GetFiles() => GetFiles("*");
        public FileInfo[] GetFiles(string searchPattern)
        {
            string[] paths = System.IO.Directory.GetFiles(FullName, searchPattern);
            var result = new FileInfo[paths.Length];
            for (int i = 0; i < paths.Length; i++) result[i] = new FileInfo(paths[i]);
            return result;
        }

        public DirectoryInfo[] GetDirectories() => GetDirectories("*");
        public DirectoryInfo[] GetDirectories(string searchPattern)
        {
            string[] paths = System.IO.Directory.GetDirectories(FullName, searchPattern);
            var result = new DirectoryInfo[paths.Length];
            for (int i = 0; i < paths.Length; i++) result[i] = new DirectoryInfo(paths[i]);
            return result;
        }
    }

    /// <summary>
    /// The volume (step208): one, the boot disk, named "/". Free space is
    /// counted in the FAT on every question. Cut: volume label, drive type
    /// other than Fixed.
    /// </summary>
    public sealed class DriveInfo
    {
        public DriveInfo(string driveName)
        {
            if (driveName == null) throw new ArgumentNullException(nameof(driveName));
            Name = "/";
        }

        public string Name { get; }
        public string DriveFormat => "FAT32";
        public bool IsReady => Space(out _, out _);
        public DriveType DriveType => DriveType.Fixed;
        public DirectoryInfo RootDirectory => new DirectoryInfo("/");

        public long TotalSize
        {
            get { Need(out ulong total, out _); return (long)total; }
        }

        public long TotalFreeSpace
        {
            get { Need(out _, out ulong free); return (long)free; }
        }

        public long AvailableFreeSpace => TotalFreeSpace;

        public static DriveInfo[] GetDrives() => new[] { new DriveInfo("/") };

        public override string ToString() => Name;

        private static bool Space(out ulong total, out ulong free)
            => AppFile.Manage(AppFile.ManageVolume, "/", out total, out free) == AppServiceStatus.Ok;

        private static void Need(out ulong total, out ulong free)
        {
            if (!Space(out total, out free)) throw new IOException("The volume cannot say how much room it has.");
        }
    }

    public enum DriveType
    {
        Unknown = 0,
        NoRootDirectory = 1,
        Removable = 2,
        Fixed = 3,
        Network = 4,
        CDRom = 5,
        Ram = 6,
    }
}
