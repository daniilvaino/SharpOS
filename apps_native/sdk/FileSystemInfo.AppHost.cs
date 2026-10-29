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
    /// A file by its path (step208). Cut: attributes, creation and access
    /// times, Open* other than through File, Refresh (every property asks
    /// the kernel anew).
    /// </summary>
    public sealed class FileInfo
    {
        public FileInfo(string fileName)
        {
            if (fileName == null) throw new ArgumentNullException(nameof(fileName));
            FullName = SharpOS.AppSdk.Process.ResolvePath(fileName);
        }

        public string FullName { get; }
        public string Name => Path.GetFileName(FullName);
        public string Extension => Path.GetExtension(FullName);
        public string DirectoryName => System.IO.Directory.ParentOf(FullName);
        public DirectoryInfo Directory => new DirectoryInfo(DirectoryName);
        public bool Exists => File.Exists(FullName);

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

        public DateTime LastWriteTime => System.IO.Directory.LastWrite(FullName);

        public FileInfo CopyTo(string destFileName) => CopyTo(destFileName, false);
        public FileInfo CopyTo(string destFileName, bool overwrite)
        {
            File.Copy(FullName, destFileName, overwrite);
            return new FileInfo(destFileName);
        }

        public void MoveTo(string destFileName) => File.Move(FullName, destFileName);
        public void Delete() => File.Delete(FullName);
        public override string ToString() => FullName;
    }

    /// <summary>
    /// A directory by its path (step208). Cut: attributes, creation and
    /// access times, EnumerateFiles' laziness.
    /// </summary>
    public sealed class DirectoryInfo
    {
        public DirectoryInfo(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            FullName = SharpOS.AppSdk.Process.ResolvePath(path);
        }

        public string FullName { get; }

        public string Name
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

        public bool Exists => System.IO.Directory.Exists(FullName);
        public DateTime LastWriteTime => System.IO.Directory.LastWrite(FullName);

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
        public void Delete() => System.IO.Directory.Delete(FullName);
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

        public override string ToString() => FullName;
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
