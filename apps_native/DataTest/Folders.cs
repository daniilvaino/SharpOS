using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SharpOS.AppSdk;

namespace DataApps
{
    // Test 21 (step208): directories made and removed, files deleted — the
    // FAT writer's new half, through System.IO and through the shell.
    internal static unsafe partial class AppEntry
    {
        private static void Folders()
        {
            // A run that failed half-way leaves its tree; this one starts clean.
            if (Directory.Exists("t21")) Directory.Delete("t21", true);

            Directory.CreateDirectory("t21");
            Check("21: CreateDirectory: a directory, not a file", Directory.Exists("t21") && !File.Exists("t21"));
            bool again = true;
            try { Directory.CreateDirectory("t21"); } catch (Exception) { again = false; }
            Check("21: CreateDirectory of one that exists: no error", again);

            DirectoryInfo nested = Directory.CreateDirectory("t21/a/b");
            Check("21: CreateDirectory makes the missing parents (" + nested.Name + ")",
                  Directory.Exists("t21/a") && Directory.Exists("t21/a/b") && nested.Name == "b");

            File.WriteAllText("t21/a/b/x.txt", "hi");
            Check("21: a file in a new directory, read back", File.Exists("t21/a/b/x.txt") && Text("t21/a/b/x.txt") == "hi"
                  && !Directory.Exists("t21/a/b/x.txt"));

            // A long name has entries of its own before the short one: all go,
            // or their pieces would name the entry after them.
            const string longName = "t21/a/a long file name.json";
            File.WriteAllText(longName, "{}");
            File.WriteAllText("t21/a/short.txt", "s");
            File.Delete(longName);
            Check("21: a long name deleted; its neighbour stays", !File.Exists(longName) && File.Exists("t21/a/short.txt")
                  && Text("t21/a/short.txt") == "s");
            // Ignoring case: a name that fits 8.3 is stored upper-case (the
            // writer sets no lower-case flags) — b and short.txt list as B and
            // SHORT.TXT. What matters here is that no piece of the long name
            // is left to show up as a name.
            string listed = Listing("t21/a");
            Check("21: the directory lists what is left (" + listed + ")", listed.ToUpperInvariant() == "B/,SHORT.TXT");
            File.WriteAllText(longName, "again");
            Check("21: the long name made again", Text(longName) == "again");

            bool quiet = true;
            try { File.Delete("t21/no-such-file.txt"); } catch (Exception) { quiet = false; }
            Check("21: File.Delete of a missing file: no error", quiet);
            Check("21: File.Delete under a missing directory: DirectoryNotFoundException",
                  Throws(e => e is DirectoryNotFoundException, () => File.Delete("t21/nowhere/x.txt")));
            Check("21: File.Delete of a directory: UnauthorizedAccessException",
                  Throws(e => e is UnauthorizedAccessException, () => File.Delete("t21/a")));
            Check("21: Directory.Delete of one with files: IOException",
                  Throws(e => e is IOException, () => Directory.Delete("t21/a")));
            Check("21: Directory.Delete of a missing one: DirectoryNotFoundException",
                  Throws(e => e is DirectoryNotFoundException, () => Directory.Delete("t21/none")));

            using (AppFile open = AppFile.Open("t21/a/short.txt", AppFile.ModeAppend))
                Check("21: a file open for writing cannot be deleted", Throws(e => e is IOException, () => File.Delete("t21/a/short.txt")));
            File.Delete("t21/a/short.txt");
            Check("21: deleted once closed", !File.Exists("t21/a/short.txt"));

            Directory.Delete("t21", true);
            Check("21: Directory.Delete(recursive): the tree is gone", !Directory.Exists("t21") && !File.Exists(longName));

            // The shell's builtins.
            Check("21: sh: mkdir, > into it, rm, rmdir",
                  Sh("mkdir t21s && echo x > t21s/f.txt && rm t21s/f.txt && rmdir t21s") == 0 && !Directory.Exists("t21s"));
            Check("21: sh: rm of nothing is 1, rm -f of nothing 0", Sh("rm t21-none") == 1 && Sh("rm -f t21-none") == 0);
            Check("21: sh: mkdir -p, then rm -r",
                  Sh("mkdir -p t21p/x/y && echo y > t21p/x/y/f.txt && rm -r t21p") == 0 && !Directory.Exists("t21p"));
            Check("21: sh: mkdir of one that exists is 1, rmdir of a full one is 1",
                  Sh("mkdir t21q") == 0 && Sh("mkdir t21q") == 1 && Sh("echo q > t21q/f.txt") == 0
                  && Sh("rmdir t21q") == 1 && Sh("rm -r t21q") == 0);
        }

        // Names in a directory, a directory with a / after it, in listing order.
        private static string Listing(string path)
        {
            var names = new StringBuilder();
            byte* name = stackalloc byte[256];
            string full = Process.ResolvePath(path);
            for (uint index = 0; ; index++)
            {
                if (AppHost.TryReadDirEntry(full, index, name, 256, out FileEntry entry) != AppServiceStatus.Ok) break;
                string text = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(name, (int)(entry.NameLength < 256 ? entry.NameLength : 256)));
                if (text == "." || text == "..") continue;
                if (names.Length > 0) names.Append(',');
                names.Append(text).Append(entry.IsDirectory != 0 ? "/" : "");
            }
            return names.ToString();
        }

        // Whether the action throws an exception the test takes. A predicate,
        // not catch (T): a generic type test does not link on this tier.
        private static bool Throws(Func<Exception, bool> expected, Action action)
        {
            try { action(); }
            catch (Exception e) { return expected(e); }
            return false;
        }
    }
}
