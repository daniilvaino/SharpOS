using System;
using System.Collections.Generic;
using System.IO;
using SharpOS.AppSdk;

namespace DataApps
{
    // Test 25 (step208): names as they were written — an 8.3 name in small
    // letters stays small (the case flags), a mixed one takes a long name —
    // and names past ASCII: Cyrillic files and directories, matched without
    // regard to case, renamed, typed in the shell, a working directory; a
    // character past the BMP (a surrogate pair).
    internal static unsafe partial class AppEntry
    {
        private static void Names()
        {
            const string Root = "t25";
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            Directory.CreateDirectory(Root);

            File.WriteAllText(Root + "/a.txt", "1");
            File.WriteAllText(Root + "/B.TXT", "2");
            File.WriteAllText(Root + "/readme.TXT", "3");
            File.WriteAllText(Root + "/ReadMe2.txt", "4");
            Directory.CreateDirectory(Root + "/lower");
            Check("25: names listed as written (" + Listing(Root) + ")",
                  Same(Split(Listing(Root)), "a.txt", "B.TXT", "readme.TXT", "ReadMe2.txt", "lower/"));
            Check("25: and found in any case", File.ReadAllText(Root + "/A.TXT") == "1" && File.ReadAllText(Root + "/b.txt") == "2");

            const string Report = Root + "/Отчёт за май.txt";
            File.WriteAllText(Report, "кириллица");
            Check("25: a Cyrillic name: written, read, listed",
                  File.Exists(Report) && File.ReadAllText(Report) == "кириллица" && Split(Listing(Root)).Contains("Отчёт за май.txt"));
            Check("25: matched without regard to case", File.ReadAllText(Root + "/ОТЧЁТ ЗА МАЙ.TXT") == "кириллица");

            Directory.CreateDirectory(Root + "/Папка");
            File.WriteAllText(Root + "/Папка/файл.txt", "ф");
            string[] inside = Directory.GetFiles(Root + "/Папка");
            Check("25: a Cyrillic directory and a file in it (" + (inside.Length > 0 ? inside[0] : "none") + ")",
                  inside.Length == 1 && inside[0].EndsWith("Папка\\файл.txt") && new FileInfo(Root + "/Папка/файл.txt").Length == 2);

            File.Move(Report, Root + "/report.txt");
            File.Move(Root + "/report.txt", Root + "/Итог.txt");
            Check("25: renamed to ASCII and back", !File.Exists(Report) && File.ReadAllText(Root + "/Итог.txt") == "кириллица");

            const string Emoji = Root + "/\U0001F600 smile.txt";
            File.WriteAllText(Emoji, ":)");
            Check("25: a character past the BMP", File.ReadAllText(Emoji) == ":)" && Split(Listing(Root)).Contains("\U0001F600 smile.txt"));

            Check("25: sh: mkdir and > with Cyrillic names",
                  Sh("mkdir t25/каталог && echo привет > t25/каталог/файл.txt") == 0
                  && File.ReadAllText(Root + "/каталог/файл.txt").StartsWith("привет"));
            List<string> pwd = Lines("cd t25/каталог && pwd");
            Check("25: sh: a Cyrillic working directory (" + Join(pwd).Trim() + ")",
                  pwd.Count == 1 && pwd[0] == Process.ResolvePath(Root + "/каталог"));
            Check("25: sh: ls lists it", Lines("ls t25").Contains("Итог.txt"));

            Directory.Delete(Root, true);
            Check("25: all of it deleted", !Directory.Exists(Root));
        }

        private static List<string> Split(string listing)
            => listing.Length == 0 ? new List<string>() : new List<string>(listing.Split(','));

        private static bool Same(List<string> got, params string[] expected)
        {
            if (got.Count != expected.Length) return false;
            foreach (string e in expected) if (!got.Contains(e)) return false;
            return true;
        }
    }
}
