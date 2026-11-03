using System;
using System.Collections.Generic;
using System.IO;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace DataApps
{
    // Test 31: the small commands over lines and over objects. An object must
    // come out the same type: the next stage reads its fields.
    internal static unsafe partial class AppEntry
    {
        private static string Joined(string pipeline) => string.Join("|", Lines(pipeline).ToArray());

        private static List<long> Levels(string pipeline)
        {
            var levels = new List<long>();
            foreach (View v in Pipe.From(pipeline)) levels.Add((long)v["Level"]);
            return levels;
        }

        private static void UtilsCommands()
        {
            const string f = "t31.txt";
            File.WriteAllText(f, "b 2\na 10\nc 1\na 10\n");

            Check("31: head -2 of a file", Joined("head -2 " + f) == "b 2|a 10");
            Check("31: head of a byte stream", Joined("READ " + f + " | head -n 1") == "b 2");
            Check("31: tail -2 of a file", Joined("tail -2 " + f) == "c 1|a 10");
            List<long> all = Levels("SAMPLE_PIPEGEN 20");
            Check("31: head keeps the type", Levels("SAMPLE_PIPEGEN 20 | head -3").Count == 3);
            List<long> last = Levels("SAMPLE_PIPEGEN 20 | tail -3");
            Check("31: tail keeps the type and the last ones", last.Count == 3 && last[2] == all[19] && last[0] == all[17]);
            Check("31: head stops what feeds it", Sh("SAMPLE_PIPEGEN 100000000 | head -1") == 0);

            Check("31: grep", Joined("grep a " + f) == "a 10|a 10");
            Check("31: grep -v", Joined("grep -v a " + f) == "b 2|c 1");
            Check("31: grep -c", Joined("grep -c a " + f) == "2");
            Check("31: grep -i, a regex", Joined("grep -i '^C [0-9]$' " + f) == "c 1");
            Check("31: grep -F takes it as written", Sh("grep -q -F 'a.1' " + f) == 1);
            Check("31: grep -n", Joined("grep -n c " + f) == "3:c 1");
            Check("31: grep finding nothing is 1, a bad pattern 2", Sh("grep -q zzz " + f) == 1 && Sh("grep -q '(' " + f) == 2);
            long threes = 0;
            foreach (long l in all) if (l == 3) threes++;
            List<long> grepped = Levels("SAMPLE_PIPEGEN 20 | grep -k Level '^3$'");
            bool onlyThrees = true;
            foreach (long l in grepped) if (l != 3) onlyThrees = false;
            Check("31: grep -k a field of an object, keeps the type (" + grepped.Count.ToString() + ")",
                  grepped.Count == threes && threes > 0 && onlyThrees);

            string counted = Joined("wc " + f);
            Check("31: wc of a file (" + counted + ")", counted == "       4       8      18 " + f);
            Check("31: wc -l of objects", Joined("SAMPLE_PIPEGEN 20 | wc -l").Trim() == "20");

            Check("31: sort", Joined("sort " + f) == "a 10|a 10|b 2|c 1");
            Check("31: sort -r", Joined("sort -r " + f) == "c 1|b 2|a 10|a 10");
            Check("31: sort -u", Joined("sort -u " + f) == "a 10|b 2|c 1");
            Check("31: sort -n -k by a column", Joined("sort -n -k 2 " + f) == "c 1|b 2|a 10|a 10");
            List<long> sorted = Levels("SAMPLE_PIPEGEN 20 | sort -r -k Level");
            bool inOrder = sorted.Count == 20;
            for (int i = 1; i < sorted.Count; i++) if (sorted[i - 1] < sorted[i]) inOrder = false;
            Check("31: sort -k a field of an object, keeps the type", inOrder);

            Check("31: uniq", Joined("sort " + f + " | uniq") == "a 10|b 2|c 1");
            Check("31: uniq -c", Joined("sort " + f + " | uniq -c") == "      2 a 10|      1 b 2|      1 c 1");
            Check("31: uniq -d, -u", Joined("sort " + f + " | uniq -d") == "a 10" && Joined("sort " + f + " | uniq -u") == "b 2|c 1");

            Check("31: tee keeps text and sends it on", Joined("READ " + f + " --lines | tee t31b.txt | wc -l").Trim() == "4"
                  && File.ReadAllText("t31b.txt") == File.ReadAllText(f));
            Check("31: tee keeps objects as a file of messages", Levels("SAMPLE_PIPEGEN 5 | tee t31c.pipe").Count == 5
                  && Levels("READ t31c.pipe").Count == 5);
            Check("31: tee into a file it cannot write goes on, code 1", Sh("SAMPLE_PIPEGEN 5 | tee /nosuch/dir/x | wc -l") == 1);

            Directory.CreateDirectory("t31d/sub");
            File.WriteAllText("t31d/one.txt", "1");
            File.WriteAllText("t31d/sub/two.log", "22");
            Check("31: find", Joined("find t31d") == "t31d|t31d/one.txt|t31d/sub|t31d/sub/two.log");
            Check("31: find -name, without case", Joined("find t31d -name '*.TXT'") == "t31d/one.txt");
            Check("31: find -type d", Joined("find t31d -type d") == "t31d|t31d/sub");
            Check("31: find -maxdepth", Joined("find t31d -maxdepth 1 -type f") == "t31d/one.txt");
            long bytes = -1;
            foreach (View v in Pipe.From("find t31d -l -name two.log")) bytes = (long)v["Size"];
            Check("31: find -l sends objects", bytes == 2);
            Check("31: find of nothing is 1", Sh("find t31nothing") == 1);

            File.WriteAllText("t31e.bin", "Hi\n");
            Check("31: xxd (" + Joined("xxd t31e.bin") + ")", Joined("xxd t31e.bin") == "00000000: 4869 0a                                  Hi.");
            Check("31: xxd -p", Joined("xxd -p t31e.bin") == "48690a");
            Check("31: xxd over a pipe, -r back", Sh("xxd " + f + " | xxd -r > t31f.txt") == 0 && File.ReadAllText("t31f.txt") == File.ReadAllText(f));
            Check("31: xxd -l -s", Joined("xxd -p -s 1 -l 1 t31e.bin") == "69");

            bool listed = false;
            foreach (View v in Pipe.From("ps"))
                if ((uint)v["Id"] == Process.CurrentId && ((string)v["Name"]).ToUpperInvariant().StartsWith("DATATEST")) listed = true;
            Check("31: ps sends the processes, this one among them", listed);
            Check("31: ps goes on through grep -k", Levels2("ps | grep -k Name -i '^datatest'") >= 1);

            Process sleeper = Process.Start("UTILS.EXE", new[] { "sleep", "60" });
            Check("31: kill", Sh("kill " + sleeper.Id.ToString()) == 0);
            sleeper.WaitForExit();
            Check("31: what was killed ended with 137 (" + sleeper.ExitCode.ToString() + ")", sleeper.ExitCode == 137);
            sleeper.Dispose();
            Check("31: kill of nothing is 1", Sh("kill 4000000") == 1);

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Check("31: sleep", Sh("sleep 0.3") == 0);
            long slept = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000 / System.Diagnostics.Stopwatch.Frequency;
            Check("31: sleep waits (" + slept.ToString() + " ms)", slept >= 300);
            Check("31: sleep of a word is 2", Sh("sleep soon") == 2);
            string year = Joined("date +%Y");
            Check("31: date +%Y (" + year + ")", int.TryParse(year, out int y) && y >= 2020);
            string day = Joined("date +%F");
            Check("31: date +%F (" + day + ")", day.Length == 10 && day[4] == '-' && day[7] == '-');

            foreach (string file in new[] { f, "t31b.txt", "t31c.pipe", "t31e.bin", "t31f.txt" }) File.Delete(file);
            Directory.Delete("t31d", true);
        }

        private static int Levels2(string pipeline)
        {
            int n = 0;
            foreach (View v in Pipe.From(pipeline)) n++;
            return n;
        }
    }
}
