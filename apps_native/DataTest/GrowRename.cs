using System;
using System.IO;
using SharpOS.AppSdk;

namespace DataApps
{
    // Test 23 (step208): a directory past its first cluster, and renames —
    // a file in place, short to long and back, across directories, a
    // directory renamed and moved (its ".." follows), and the refusals.
    internal static unsafe partial class AppEntry
    {
        private static void GrowRename()
        {
            const string Root = "t23";
            foreach (string d in new[] { Root, "t23t", "t23u" })
                if (Directory.Exists(d)) Directory.Delete(d, true);
            Directory.CreateDirectory(Root);

            // Four entries a name (three long-name pieces and the short one):
            // a 4 KiB cluster holds 128, so this directory grows to ten.
            int count = s_quick ? 150 : 300;
            Directory.CreateDirectory(Root + "/many");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
                File.WriteAllText(Root + "/many/a long file name number " + i.ToString() + ".txt", i.ToString());
            bool all = true;
            for (int i = 0; i < count && all; i++)
            {
                string p = Root + "/many/a long file name number " + i.ToString() + ".txt";
                all = File.Exists(p) && Text(p) == i.ToString();
            }
            string listed = Listing(Root + "/many");
            int entries = listed.Length == 0 ? 0 : listed.Split(',').Length;
            Console.WriteLine("[datatest] 23: " + count.ToString() + " long names made, read back and listed in " + clock.ElapsedMilliseconds.ToString() + " ms");
            Check("23: " + count.ToString() + " long names in one directory, every one read back (" + entries.ToString() + " listed)",
                  all && entries == count);
            Directory.Delete(Root + "/many", true);
            Check("23: and the grown directory deleted with them", !Directory.Exists(Root + "/many"));

            File.WriteAllText(Root + "/a.txt", "A");
            File.Move(Root + "/a.txt", Root + "/b.txt");
            Check("23: File.Move in place", !File.Exists(Root + "/a.txt") && Text(Root + "/b.txt") == "A");
            File.Move(Root + "/b.txt", Root + "/a much longer name.txt");
            File.Move(Root + "/a much longer name.txt", Root + "/c.txt");
            Check("23: short to long and back", !File.Exists(Root + "/a much longer name.txt") && Text(Root + "/c.txt") == "A"
                  && Listing(Root).ToUpperInvariant() == "C.TXT");

            Directory.CreateDirectory(Root + "/d1");
            File.Move(Root + "/c.txt", Root + "/d1/c.txt");
            Check("23: into another directory", !File.Exists(Root + "/c.txt") && Text(Root + "/d1/c.txt") == "A");

            Directory.Move(Root + "/d1", Root + "/d2");
            Directory.CreateDirectory(Root + "/x");
            File.WriteAllText(Root + "/x/marker.txt", "M");
            Directory.Move(Root + "/d2", Root + "/x/d2");
            Check("23: a directory renamed, then moved, with its file",
                  !Directory.Exists(Root + "/d1") && !Directory.Exists(Root + "/d2") && Text(Root + "/x/d2/c.txt") == "A");
            // The SDK takes ".." itself (step208), so this is the path, not the
            // entry; the moved directory's ".." entry is what fsck.fat checks
            // after the run.
            Check("23: a path through the moved directory's .. reaches its new parent",
                  File.Exists(Root + "/x/d2/../marker.txt") && Text(Root + "/x/d2/../marker.txt") == "M");

            Check("23: a directory into itself: IOException",
                  Throws(e => e is IOException, () => Directory.Move(Root + "/x", Root + "/x/d2/inner")));
            File.WriteAllText(Root + "/e.txt", "E");
            Check("23: onto a file that exists: IOException",
                  Throws(e => e is IOException, () => File.Move(Root + "/x/d2/c.txt", Root + "/e.txt")));
            File.Move(Root + "/x/d2/c.txt", Root + "/e.txt", true);
            Check("23: File.Move with overwrite", Text(Root + "/e.txt") == "A" && !File.Exists(Root + "/x/d2/c.txt"));
            File.Move(Root + "/e.txt", Root + "/E-renamed.TXT");
            Check("23: a name in another case", Text(Root + "/e-renamed.txt") == "A");
            using (AppFile open = AppFile.Open(Root + "/E-renamed.TXT", AppFile.ModeAppend))
                Check("23: a file open for writing: IOException",
                      Throws(e => e is IOException, () => File.Move(Root + "/E-renamed.TXT", Root + "/f.txt")));
            Check("23: a missing file: FileNotFoundException",
                  Throws(e => e is FileNotFoundException, () => File.Move(Root + "/none.txt", Root + "/g.txt")));

            Check("23: sh: mv renames a file and a directory",
                  Sh("mkdir t23s && echo m > t23s/m.txt && mv t23s/m.txt t23s/n.txt && mv t23s t23t") == 0
                  && File.Exists("t23t/n.txt") && !Directory.Exists("t23s"));
            Check("23: sh: mv into a directory",
                  Sh("mkdir t23u && mv t23t/n.txt t23u") == 0 && File.Exists("t23u/n.txt") && !File.Exists("t23t/n.txt"));

            Directory.Delete(Root, true);
            Directory.Delete("t23t", true);
            Directory.Delete("t23u", true);
            Check("23: all of it deleted", !Directory.Exists(Root) && !Directory.Exists("t23t") && !Directory.Exists("t23u"));
        }
    }
}
