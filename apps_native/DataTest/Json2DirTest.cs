using System;
using System.IO;
using SharpOS.AppSdk;

namespace DataApps
{
    // Test 27 (step208): JSON2DIR (json2dir, vendor/json2dir) — a tree made
    // from a document on the standard input, made again over itself, the
    // refusals that write nothing; READ's explicit modes.
    internal static unsafe partial class AppEntry
    {
        private static void Json2Dir()
        {
            const string Root = "t27";
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            Directory.CreateDirectory(Root);
            string home = Environment.GetEnvironmentVariable("HOME") ?? "";

            File.WriteAllText("t27.json",
                "{ \"a.txt\": \"alpha\", \"dir\": { \"b.txt\": \"beta\", \"Отчёт.txt\": \"кириллица\", \"deep\": { \"c\": \"c\" } },"
                + " \"run.sh\": [\"script\", \"echo hi\"] }");
            Check("27: READ t27.json | JSON2DIR, in t27", Sh("cd t27 && READ $HOME/t27.json | JSON2DIR") == 0);
            Check("27: the files and directories, the contents",
                  File.ReadAllText(Root + "/a.txt") == "alpha" && File.ReadAllText(Root + "/dir/b.txt") == "beta"
                  && File.ReadAllText(Root + "/dir/Отчёт.txt") == "кириллица" && File.ReadAllText(Root + "/dir/deep/c") == "c"
                  && Directory.Exists(Root + "/dir/deep"));
            Check("27: a script is an ordinary file on FAT", File.ReadAllText(Root + "/run.sh") == "echo hi");

            Check("27: again over itself: the files replaced", Sh("cd t27 && READ $HOME/t27.json | JSON2DIR") == 0
                  && File.ReadAllText(Root + "/a.txt") == "alpha");
            File.WriteAllText("t27.json", "{ \"a.txt\": \"changed\", \"dir\": { \"new.txt\": \"n\" } }");
            Check("27: a changed document: changed and added, the rest left",
                  Sh("cd t27 && READ $HOME/t27.json | JSON2DIR") == 0 && File.ReadAllText(Root + "/a.txt") == "changed"
                  && File.ReadAllText(Root + "/dir/new.txt") == "n" && File.ReadAllText(Root + "/dir/b.txt") == "beta");

            File.WriteAllText("t27.json", "{ \"first.txt\": \"x\", \"l\": [\"link\", \"a.txt\"] }");
            Check("27: a link: the whole document refused (1), nothing written",
                  Sh("cd t27 && READ $HOME/t27.json | JSON2DIR") == 1 && !File.Exists(Root + "/first.txt"));
            File.WriteAllText("t27.json", "{ \"x\": 1 }");
            Check("27: a number: refused (1)", Sh("cd t27 && READ $HOME/t27.json | JSON2DIR") == 1 && !File.Exists(Root + "/x"));
            File.WriteAllText("t27.json", "[\"not\", \"an object\"]");
            Check("27: a root that is not an object: refused (1)", Sh("cd t27 && READ $HOME/t27.json | JSON2DIR") == 1);
            File.WriteAllText("t27.json", "{ \"a/b\": \"x\" }");
            Check("27: a name with /: refused (1)", Sh("cd t27 && READ $HOME/t27.json | JSON2DIR") == 1);
            File.WriteAllText("t27.json", "{ \"a.txt\": \"x\", ");
            Check("27: JSON cut short: refused (1)", Sh("cd t27 && READ $HOME/t27.json | JSON2DIR") == 1);
            File.WriteAllText("t27.json", "{ \"dir\": \"a file where a directory is\" }");
            Check("27: a directory in the way of a file: 1, the directory kept",
                  Sh("cd t27 && READ $HOME/t27.json | JSON2DIR") == 1 && Directory.Exists(Root + "/dir"));
            Check("27: an argument: usage (2)", Sh("JSON2DIR extra") == 2);

            Check("27: READ --externalpipe of a file that is not one: 1",
                  Sh("READ t27.json --externalpipe") == 1 && Sh("READ t27.json --bytes --externalpipe") == 2);

            Directory.Delete(Root, true);
            File.Delete("t27.json");
            Check("27: all of it deleted", !Directory.Exists(Root) && !File.Exists("t27.json"));
        }
    }
}
