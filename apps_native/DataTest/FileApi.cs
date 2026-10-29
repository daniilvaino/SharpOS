using System;
using System.Collections.Generic;
using System.IO;
using SharpOS.AppSdk;

namespace DataApps
{
    // Test 24 (step208): what programs see of the file system beyond open
    // files — listings with patterns, FileInfo and last-write times,
    // File.Copy, DriveInfo's free space; and the shell's ls -l, cp, df.
    internal static unsafe partial class AppEntry
    {
        private static void FileApi()
        {
            const string Root = "t24";
            foreach (string d in new[] { Root, "t24c" })
                if (Directory.Exists(d)) Directory.Delete(d, true);
            Directory.CreateDirectory(Root + "/sub");
            File.WriteAllText(Root + "/a.txt", "alpha");
            File.WriteAllText(Root + "/b.log", "beta");
            File.WriteAllText(Root + "/a longer name.txt", "gamma");
            File.WriteAllText(Root + "/sub/c.txt", "delta");

            Check("24: GetFiles: the files, each as path + name",
                  Same(Directory.GetFiles(Root), Root + "/a.txt", Root + "/b.log", Root + "/a longer name.txt"));
            Check("24: GetFiles with *.txt", Same(Directory.GetFiles(Root, "*.txt"), Root + "/a.txt", Root + "/a longer name.txt"));
            Check("24: GetFiles with ?.log", Same(Directory.GetFiles(Root, "?.LOG"), Root + "/b.log"));
            Check("24: GetFiles, all directories",
                  Same(Directory.GetFiles(Root, "*.txt", SearchOption.AllDirectories),
                       Root + "/a.txt", Root + "/a longer name.txt", Root + "/sub/c.txt"));
            Check("24: GetDirectories and GetFileSystemEntries",
                  Same(Directory.GetDirectories(Root), Root + "/sub") && Directory.GetFileSystemEntries(Root).Length == 4);
            // step215: '/' is the separator; a '\' handed in is taken for one, never given back.
            Check("24: a path with '\\' reaches the same file", File.ReadAllText(Root + "\\sub\\c.txt") == "delta");
            Check("24: ... and resolves with '/' (" + Process.ResolvePath(Root + "\\sub\\..\\sub") + ")",
                  Process.ResolvePath(Root + "\\sub\\..\\sub") == Process.ResolvePath(Root + "/sub")
                  && Process.ResolvePath("\\bin\\..\\tests") == "/tests" && Process.ResolvePath(Root).IndexOf('\\') < 0);
            Check("24: Path is the Unix one",
                  Path.DirectorySeparatorChar == '/' && Path.PathSeparator == ';'
                  && Path.Combine("/a", "b") == "/a/b" && Path.Combine("/a/", "/b") == "/b"
                  && Path.GetDirectoryName("/a") == "/" && Path.GetDirectoryName("/") == null
                  && Path.GetDirectoryName("a//b/c") == "a/b" && Path.GetFileName("/a/b.txt") == "b.txt"
                  && Path.GetFullPath("/a/./b/../c") == "/a/c" && Path.GetFullPath("/..") == "/"
                  && Path.GetFullPath(Root) == Process.ResolvePath(Root) && Path.IsPathRooted("/x") && !Path.IsPathRooted("x"));
            Check("24: GetFiles of a missing directory: DirectoryNotFoundException",
                  Throws(e => e is DirectoryNotFoundException, () => Directory.GetFiles(Root + "/none")));

            var info = new FileInfo(Root + "/a longer name.txt");
            Check("24: FileInfo: name, extension, directory, length",
                  info.Exists && info.Name == "a longer name.txt" && info.Extension == ".txt" && info.Length == 5
                  && info.DirectoryName == Process.ResolvePath(Root));
            DateTime now = DateTime.Now, written = info.LastWriteTime;
            double minutes = (now - written).TotalMinutes;
            Check("24: the last write is now (" + written.ToString() + ", now " + now.ToString() + ")", minutes > -2 && minutes < 2);
            string tz = Environment.GetEnvironmentVariable("TZ") ?? "";
            TimeSpan local = TimeZoneInfo.Local.BaseUtcOffset, apart = DateTime.Now - DateTime.UtcNow;
            Check("24: TZ=" + tz + " is the local zone (" + local.ToString() + "), Now - UtcNow the same",
                  (tz.Length == 0 ? local == TimeSpan.Zero : local.Ticks != 0 || tz.EndsWith("00:00") || tz == "UTC")
                  && Math.Abs((apart - local).TotalSeconds) < 5);
            Check("24: no such file: Length throws, the time is 1601",
                  Throws(e => e is FileNotFoundException, () => { long n = new FileInfo(Root + "/none").Length; })
                  && File.GetLastWriteTime(Root + "/none").Year == 1601);

            var big = new byte[200 * 1024 + 17];
            for (int i = 0; i < big.Length; i++) big[i] = (byte)(i * 13 + (i >> 8));
            File.WriteAllBytes(Root + "/big.bin", big);
            File.Copy(Root + "/big.bin", Root + "/sub/big copy.bin");
            Check("24: File.Copy of 200 KiB, the same bytes", SameBytes(File.ReadAllBytes(Root + "/sub/big copy.bin"), big));
            Check("24: File.Copy onto a file: IOException; with overwrite: done",
                  Throws(e => e is IOException, () => File.Copy(Root + "/a.txt", Root + "/b.log"))
                  && Run(() => File.Copy(Root + "/a.txt", Root + "/b.log", true)) && File.ReadAllText(Root + "/b.log") == "alpha");

            var dir = new DirectoryInfo(Root + "/sub");
            Check("24: DirectoryInfo: files, parent, name",
                  dir.GetFiles().Length == 2 && dir.Parent.FullName == Process.ResolvePath(Root) && dir.Name == "sub");

            var drive = new DriveInfo("/");
            long free = drive.TotalFreeSpace;
            File.WriteAllBytes(Root + "/mega.bin", new byte[1024 * 1024]);
            long after = drive.TotalFreeSpace;
            File.Delete(Root + "/mega.bin");
            long back = drive.TotalFreeSpace;
            Check("24: DriveInfo: a MiB written takes a MiB, deleted gives it back (" + free.ToString() + " / " + after.ToString() + " / " + back.ToString() + ")",
                  drive.TotalSize > 0 && free - after == 1024 * 1024 && back == free);

            List<string> ls = Lines("ls -l t24");
            bool sizeShown = false, dirShown = false;
            foreach (string l in ls)
            {
                if (l.IndexOf("<DIR>") >= 0 && l.EndsWith("sub/", StringComparison.OrdinalIgnoreCase)) dirShown = true;
                if (l.IndexOf(" 204817 ") >= 0 && l.EndsWith("big.bin", StringComparison.OrdinalIgnoreCase)) sizeShown = true;
            }
            Check("24: sh: ls -l shows sizes and directories", sizeShown && dirShown);
            Check("24: sh: cp -r copies the tree",
                  Sh("cp -r t24 t24c") == 0 && SameBytes(File.ReadAllBytes("t24c/sub/big copy.bin"), big)
                  && File.ReadAllText("t24c/a longer name.txt") == "gamma");
            List<string> df = Lines("df");
            Check("24: sh: df", df.Count == 2 && df[1].EndsWith("%"));

            Directory.Delete(Root, true);
            Directory.Delete("t24c", true);
            Check("24: all of it deleted", !Directory.Exists(Root) && !Directory.Exists("t24c"));
        }

        // The same paths, in any order.
        private static bool Same(string[] got, params string[] expected)
        {
            if (got.Length != expected.Length) return false;
            foreach (string e in expected)
            {
                bool found = false;
                foreach (string g in got) if (string.Equals(g, e, StringComparison.OrdinalIgnoreCase)) found = true;
                if (!found) return false;
            }
            return true;
        }

        private static bool Run(Action action)
        {
            try { action(); return true; }
            catch (Exception) { return false; }
        }
    }
}
