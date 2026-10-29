using System;
using System.Collections.Generic;
using System.IO;
using SharpOS.AppSdk;

namespace DataApps
{
    // Test 22 (step208): the FAT writer under a long run of mixed changes —
    // files made, grown and deleted, directories made and removed, in an
    // order a fixed seed decides, every file's bytes kept in a model and
    // compared. What this cannot see — clusters lost or shared, a chain cut
    // short, a FAT copy out of step — fsck.fat sees after the run
    // (run_build.ps1, last_fsck.log).
    internal static unsafe partial class AppEntry
    {
        // A file in the model: its bytes are Pattern(Seed, offset) for every
        // offset below Length, so the model holds two numbers, not the data.
        private sealed class ModelFile
        {
            public string Path;
            public ulong Seed;
            public long Length;
        }

        private static ulong s_rng;

        private static ulong Next()
        {
            s_rng ^= s_rng << 13;
            s_rng ^= s_rng >> 7;
            s_rng ^= s_rng << 17;
            return s_rng;
        }

        private static int Below(int n) => (int)(Next() % (ulong)n);

        private static byte Pattern(ulong seed, long offset) => (byte)(seed * 31 + (ulong)offset * 7 + (ulong)(offset >> 9));

        // Sizes around the places a writer gets wrong: nothing, one byte, a
        // cluster's edges (4096), past the staging run (64 KiB).
        private static readonly int[] s_sizes = { 0, 1, 100, 4095, 4096, 4097, 8192, 20000, 65535, 65536, 65537, 140000 };

        private static void Grinder()
        {
            const string Root = "t22";
            if (Directory.Exists(Root)) Directory.Delete(Root, true);

            // Files the test does not touch: their bytes must not change.
            string[] witnesses = { "/bin/SHELL.EXE", "/tests/DATATEST.EXE" };
            var before = new List<ulong>();
            foreach (string w in witnesses) before.Add(Fnv(File.ReadAllBytes(w)));

            s_rng = 0x9E3779B97F4A7C15UL;
            int operations = s_quick ? 150 : 600;
            const int Directories = 4, PerDirectory = 50;   // 50 long names: four clusters of entries

            Directory.CreateDirectory(Root);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var dirs = new List<string>();
            var files = new List<ModelFile>();
            var made = new int[6];          // create, append, delete, mkdir, rmdir, rename
            int verified = 0, mismatches = 0, failures = 0, serial = 0;
            string firstProblem = null;

            for (int op = 0; op < operations; op++)
            {
                int kind = Below(100);
                try
                {
                    if (kind < 8 && dirs.Count < Directories)
                    {
                        string d = Root + "/" + (Below(2) == 0 ? "D" + serial.ToString() : "a directory " + serial.ToString());
                        serial++;
                        Directory.CreateDirectory(d);
                        dirs.Add(d);
                        made[3]++;
                    }
                    else if (kind < 12 && dirs.Count > 0)
                    {
                        // An empty directory only: its files go first.
                        string d = dirs[Below(dirs.Count)];
                        for (int i = files.Count - 1; i >= 0; i--)
                            if (files[i].Path.StartsWith(d + "/")) { File.Delete(files[i].Path); files.RemoveAt(i); made[2]++; }
                        Directory.Delete(d);
                        dirs.Remove(d);
                        made[4]++;
                    }
                    else if (kind < 50)
                    {
                        string dir = dirs.Count == 0 || Below(3) == 0 ? Root : dirs[Below(dirs.Count)];
                        if (CountIn(files, dir) >= PerDirectory) continue;
                        string name = Below(2) == 0 ? "F" + serial.ToString() + ".BIN" : "file number " + serial.ToString() + " of the grinder.dat";
                        serial++;
                        var f = new ModelFile { Path = dir + "/" + name, Seed = Next() };
                        Append(f, s_sizes[Below(s_sizes.Length)], create: true);
                        files.Add(f);
                        made[0]++;
                    }
                    else if (kind < 68 && files.Count > 0)
                    {
                        ModelFile f = files[Below(files.Count)];
                        if (f.Length < 400000) { Append(f, s_sizes[Below(s_sizes.Length)], create: false); made[1]++; }
                    }
                    else if (kind < 80 && files.Count > 0)
                    {
                        int i = Below(files.Count);
                        File.Delete(files[i].Path);
                        files.RemoveAt(i);
                        made[2]++;
                    }
                    else if (kind < 88 && files.Count > 0)
                    {
                        // A rename, into this directory or another (step208).
                        ModelFile f = files[Below(files.Count)];
                        string dir = dirs.Count == 0 || Below(3) == 0 ? Root : dirs[Below(dirs.Count)];
                        if (CountIn(files, dir) >= PerDirectory) continue;
                        string name = Below(2) == 0 ? "R" + serial.ToString() + ".BIN" : "renamed file " + serial.ToString() + ".dat";
                        serial++;
                        File.Move(f.Path, dir + "/" + name);
                        f.Path = dir + "/" + name;
                        made[5]++;
                    }
                    else if (files.Count > 0)
                    {
                        ModelFile f = files[Below(files.Count)];
                        verified++;
                        string why = Differs(f);
                        if (why != null) { mismatches++; firstProblem ??= "op " + op.ToString() + ": " + why; }
                    }
                }
                catch (Exception e)
                {
                    failures++;
                    firstProblem ??= "op " + op.ToString() + ": " + e.GetType().Name + ": " + e.Message;
                }
            }

            // Everything once more, and each directory's listing against the model.
            foreach (ModelFile f in files)
            {
                verified++;
                string why = Differs(f);
                if (why != null) { mismatches++; firstProblem ??= "final: " + why; }
            }
            string listingProblem = null;
            var all = new List<string>(dirs);
            all.Add(Root);
            foreach (string d in all)
            {
                string listed = Listing(d).ToUpperInvariant();
                string want = ExpectedListing(files, dirs, d);
                string got = listed.Length == 0 ? "" : Sorted(new List<string>(listed.Split(',')));
                if (want != got) { listingProblem ??= d + ": " + got + " (expected " + want + ")"; }
            }

            Console.WriteLine("[datatest] grinder: " + operations.ToString() + " operations — " + made[0].ToString() + " made, "
                              + made[1].ToString() + " grown, " + made[2].ToString() + " deleted, " + made[3].ToString() + " directories made, "
                              + made[4].ToString() + " removed, " + made[5].ToString() + " renamed; " + verified.ToString() + " reads compared; " + files.Count.ToString()
                              + " files left in " + (dirs.Count + 1).ToString() + " directories; "
                              + clock.ElapsedMilliseconds.ToString() + " ms");
            Check("22: no operation failed" + (failures > 0 ? " (" + failures.ToString() + "; " + firstProblem + ")" : ""), failures == 0);
            Check("22: every read matched the model" + (mismatches > 0 ? " (" + mismatches.ToString() + "; " + firstProblem + ")" : ""), mismatches == 0);
            Check("22: every directory lists what the model holds" + (listingProblem != null ? " (" + listingProblem + ")" : ""), listingProblem == null);

            Directory.Delete(Root, true);
            Check("22: the tree is gone", !Directory.Exists(Root));

            bool untouched = true;
            for (int i = 0; i < witnesses.Length; i++)
                if (Fnv(File.ReadAllBytes(witnesses[i])) != before[i]) untouched = false;
            Check("22: files outside the tree are as they were", untouched);
        }

        // Grows the file by `count` bytes of its pattern, in uneven pieces.
        private static void Append(ModelFile f, int count, bool create)
        {
            using AppFile file = AppFile.Open(f.Path, create ? AppFile.ModeWrite : AppFile.ModeAppend);
            var piece = new byte[9000];
            int left = count;
            while (left > 0)
            {
                int n = Math.Min(left, 1 + Below(piece.Length));
                for (int i = 0; i < n; i++) piece[i] = Pattern(f.Seed, f.Length + i);
                file.Write(new ReadOnlySpan<byte>(piece, 0, n));
                f.Length += n;
                left -= n;
            }
        }

        // Null when the file on the disk is the model's; else what differs.
        private static string Differs(ModelFile f)
        {
            byte[] data = File.ReadAllBytes(f.Path);
            if (data.Length != f.Length) return f.Path + ": " + data.Length.ToString() + " bytes, expected " + f.Length.ToString();
            for (int i = 0; i < data.Length; i++)
                if (data[i] != Pattern(f.Seed, i)) return f.Path + ": byte " + i.ToString() + " differs";
            return null;
        }

        private static int CountIn(List<ModelFile> files, string dir)
        {
            int n = 0;
            foreach (ModelFile f in files) if (f.Path.LastIndexOf('/') == dir.Length && f.Path.StartsWith(dir)) n++;
            return n;
        }

        // What Listing should give for `dir`: its files and directories, in
        // no particular order on the disk — so both sides are sorted.
        private static string ExpectedListing(List<ModelFile> files, List<string> dirs, string dir)
        {
            var names = new List<string>();
            foreach (ModelFile f in files)
                if (f.Path.LastIndexOf('/') == dir.Length && f.Path.StartsWith(dir)) names.Add(f.Path.Substring(dir.Length + 1).ToUpperInvariant());
            foreach (string d in dirs)
                if (d.LastIndexOf('/') == dir.Length && d.StartsWith(dir)) names.Add(d.Substring(dir.Length + 1).ToUpperInvariant() + "/");
            return Sorted(names);
        }

        private static string Sorted(List<string> names)
        {
            string[] a = names.ToArray();
            Array.Sort(a, string.CompareOrdinal);
            return string.Join(",", a);
        }

        private static ulong Fnv(byte[] data)
        {
            ulong s = 1469598103934665603UL;
            foreach (byte b in data) { s ^= b; s *= 1099511628211UL; }
            return s;
        }
    }
}
