using System;
using System.IO;
using SharpOS.AppSdk;

namespace DataApps
{
    // Test 28 (step208): the kernel's program cache keeps a program across
    // writes to other files, and drops it when the program itself is
    // rewritten. Before, any write to the volume dropped all of it — and the
    // shell writes its history after every line.
    internal static unsafe partial class AppEntry
    {
        private static void ProgramCacheKeeps()
        {
            if (Directory.Exists("t28")) Directory.Delete("t28", true);
            Directory.CreateDirectory("t28");
            const string program = "t28/GEN.EXE";
            File.Copy(Process.FindProgram("PIPEGEN"), program);
            Check("28: the copy is big enough to be kept (" + new FileInfo(program).Length.ToString() + " bytes)",
                  new FileInfo(program).Length >= 64 * 1024);

            StartAndWait(program);
            ulong[] first = Process.ProgramCacheCounts();
            StartAndWait(program);
            ulong[] second = Process.ProgramCacheCounts();
            Check("28: the second start comes from the cache (" + Counts(first, second) + ")",
                  second[0] == first[0] + 1 && second[1] == first[1]);

            File.WriteAllText("t28/other.txt", "a write to another file");
            File.AppendAllText("t28/other.txt", ", and another");
            StartAndWait(program);
            ulong[] third = Process.ProgramCacheCounts();
            Check("28: writes to another file keep it (" + Counts(second, third) + ")",
                  third[0] == second[0] + 1 && third[1] == second[1]);

            File.WriteAllBytes(program, File.ReadAllBytes(program));
            StartAndWait(program);
            ulong[] fourth = Process.ProgramCacheCounts();
            Check("28: the program rewritten: read again (" + Counts(third, fourth) + ")",
                  fourth[0] == third[0] && fourth[1] == third[1] + 1);

            StartAndWait(program);
            ulong[] fifth = Process.ProgramCacheCounts();
            Check("28: and kept again (" + Counts(fourth, fifth) + ")",
                  fifth[0] == fourth[0] + 1 && fifth[1] == fourth[1]);

            Directory.Delete("t28", true);
        }

        private static void StartAndWait(string program)
        {
            var p = Process.Start(program, new[] { "0" });
            p.WaitForExit();
            p.Dispose();
        }

        private static string Counts(ulong[] before, ulong[] after)
            => "hits +" + (after[0] - before[0]).ToString() + ", reads +" + (after[1] - before[1]).ToString();
    }
}
