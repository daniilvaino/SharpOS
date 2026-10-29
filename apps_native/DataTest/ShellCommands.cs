using System;
using System.Collections.Generic;
using System.IO;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace DataApps
{
    // Test 19 (step208): the shell's environment commands and $NAME — each
    // line in a shell of its own (sh -c), so nothing set here outlives it.
    internal static unsafe partial class AppEntry
    {
        private static void ShellCommands()
        {
            string home = Environment.GetEnvironmentVariable("HOME") ?? "";

            Check("19: echo $HOME", Join(Lines("echo $HOME")) == home + "\n");
            Check("19: echo ~/x", Join(Lines("echo ~/x")) == home + "/x\n");

            List<string> quoting = Lines("export T19=один; echo $T19 \"$T19\" '$T19' ${T19}x \\$T19 \"[$T19_NONE]\"");
            Check("19: $T19 \"$T19\" '$T19' ${T19}x \\$T19, an unset one empty (" + Escaped(Join(quoting)) + ")",
                  Join(quoting) == "один один $T19 одинx $T19 []\n");

            List<string> spaces = Lines("export T19='a b'; " + Self("getenv T19"));
            Check("19: export reaches a program, a value with a space one word (" + Escaped(Join(spaces)) + ")",
                  Join(spaces) == "a b\n");

            List<string> once = Lines("env T19=два " + Self("getenv T19"));
            Check("19: env NAME=value program (" + Escaped(Join(once)) + ")", Join(once) == "два\n");

            List<string> staged = Lines("env T19=три " + Self("getenv T19") + " | " + Self("count"));
            Check("19: env as a pipeline's stage (" + Escaped(Join(staged)) + ")", Join(staged) == "1\n");

            List<string> listed = Lines("env T19=z");
            Check("19: env NAME=value lists it", listed.Contains("T19=z") && Contains(listed, "PATH="));

            List<string> unset = Lines("export T19=1; unset T19; " + Self("getenv T19"));
            Check("19: unset (" + Escaped(Join(unset)) + ")", Join(unset) == "<unset>\n");

            Check("19: nothing set by a shell is left here", Environment.GetEnvironmentVariable("T19") == null);

            string pipegen = Process.FindProgram("PIPEGEN");
            List<string> which = Lines("which PIPEGEN echo");
            Check("19: which (" + Escaped(Join(which)) + ")", Join(which) == pipegen + "\necho: shell builtin\n");
            Check("19: which of nothing: 1", Sh("which NO_SUCH_PROGRAM") == 1);

            // Paths as cd takes them (step208): the root, "..", "./", a file.
            Check("19: cd / and cd '\\': the root, /  (" + Escaped(Join(Lines("cd / && pwd"))) + ")",
                  Join(Lines("cd / && pwd")) == "/\n" && Join(Lines("cd '\\' && pwd")) == "/\n");
            Check("19: cd ../ from /bin, and .. above the root, stay at the root",
                  Join(Lines("cd /bin && cd ../ && pwd")) == "/\n" && Join(Lines("cd /bin && cd ../.. && pwd")) == "/\n");
            Check("19: cd ./bin/../tests/: /tests", Join(Lines("cd / && cd ./bin/../tests/ && pwd")) == "/tests\n");
            // step215: '/' everywhere; a '\' a program hands in is still a separator.
            Check("19: cd '\\tests' (a backslash in quotes): /tests", Join(Lines("cd '\\tests' && pwd")) == "/tests\n");
            Check("19: which names a /-path (" + pipegen + ")", pipegen.StartsWith("/") && pipegen.IndexOf('\\') < 0);
            Check("19: PATH split at ';', and at ':' too",
                  Join(Lines("export PATH='/nowhere;/bin:/tests'; which PIPEGEN")) == pipegen + "\n");
            Check("19: cd into a file: 1; which in small letters names the stored file",
                  Sh("cd /bin/PIPEGEN.EXE") == 1 && Join(Lines("which pipegen")) == pipegen + "\n" && pipegen.EndsWith("PIPEGEN.EXE"));

            // The shell's input to every command, read in turn (step216): the
            // first takes two, the next counts the rest. Before, the second
            // got none: 0.
            List<string> shared = Lines("PIPEGEN 5 | SHELL -c '" + Self("take 2") + "; PIPECNT'");
            Check("19: echo ... | shell: its commands read the input in turn (" + Escaped(Join(shared)) + ")",
                  Join(shared) == "3\n");

            // Hints (step216): FETCH's record goes to a pipe, its panel
            // (stdtext, "describes the output") does not follow it there; the
            // panel goes where 3 is sent.
            int records = 0;
            string recordType = null;
            foreach (View v in Pipe.From("FETCH")) { records++; recordType = v.TypeName; }
            Check("19: FETCH | ...: one SystemInfo (" + records.ToString() + " " + recordType + ")",
                  records == 1 && recordType != null && recordType.EndsWith("SystemInfo"));
            List<string> panel = Lines("FETCH 3>&1 >&-");
            Check("19: FETCH 3>&1 >&-: the panel (" + panel.Count.ToString() + " lines)",
                  panel.Count > 10 && Join(panel).Contains("processors"));

            // `1>&2` with 2 not redirected (step218): the output goes where
            // the error stream does, the screen — not down the pipe.
            List<string> toErrors = Lines("PIPEGEN 3 1>&2 | PIPECNT");
            Check("19: PIPEGEN 3 1>&2 | PIPECNT: nothing down the pipe (" + Escaped(Join(toErrors)) + ")",
                  Join(toErrors) == "0\n");

            File.WriteAllText(home + "/t19.txt", "old");
            Check("19: echo hello > $HOME/$T19D.txt", Sh("export T19D=t19; echo hello > $HOME/$T19D.txt") == 0);
            Check("19: the file", Text(home + "/t19.txt").StartsWith("hello"));

            // Refused whole, before anything runs: 2.
            string[] refused =
            {
                "echo $(pwd)", "for i in a b; do echo $i; done", "x=1", "X=1 PIPEGEN 1", "echo $1", "echo $?",
                "$T19", "echo ${T19:-d}", "(pwd)", "export T19=1; echo `pwd`",
            };
            foreach (string line in refused)
                Check("19: refused: " + line, Sh(line) == 2);
        }

        // Test 20 (step208): Tab, without the keyboard — SHELL --complete
        // LINE is what Tab at the end of LINE offers; KQL --complete TEXT
        // what the KQL prompt offers, with the input's columns when it has
        // an input. First line of KQL's answer: where the word starts.
        private static void Completions()
        {
            List<string> typed = Lines("PIPEGEN 0 | KQL --complete 'where Le'");
            Check("20: KQL over LogEntry, 'where Le': Level (" + Escaped(Join(typed)) + ")", Join(Trimmed(typed)) == "6\nLevel\n");

            List<string> by = Lines("PIPEGEN 0 | KQL --complete 'summarize n = count() by Te'");
            Check("20: after by: Text (" + Escaped(Join(by)) + ")", Join(Trimmed(by)) == "25\nText\n");

            List<string> operators = Lines("KQL --complete 'where Level > 3 | su'");
            Check("20: after |: summarize (" + Escaped(Join(operators)) + ")", Join(Trimmed(operators)) == "18\nsummarize\n");

            List<string> functions = Lines("KQL --complete 'summarize n = cou'");
            Check("20: functions: count() first (" + Escaped(Join(functions)) + ")",
                  functions.Count > 2 && functions[0] == "14" && functions[1] == "count()" && functions.Contains("countif("));

            List<string> untyped = Lines("KQL --complete 'where Le'");
            Check("20: no input, no columns (" + Escaped(Join(untyped)) + ")", Join(Trimmed(untyped)) == "6\n");

            List<string> inLine = Lines("SHELL --complete \"PIPEGEN | KQL 'where Level > 3 | su\"");
            Check("20: Tab inside KQL '…' in a shell line (" + Escaped(Join(inLine)) + ")", Join(Trimmed(inLine)) == "summarize\n");

            List<string> variable = Lines("SHELL --complete 'echo $HO'");
            Check("20: Tab after $HO (" + Escaped(Join(variable)) + ")", Join(variable) == "$HOME\n");

            List<string> braced = Lines("SHELL --complete 'echo ${HO'");
            Check("20: Tab after ${HO (" + Escaped(Join(braced)) + ")", Join(braced) == "${HOME}\n");

            List<string> program = Lines("SHELL --complete 'PIPEG'");
            Check("20: Tab on a command: PIPEGEN (" + Escaped(Join(program)) + ")", program.Contains("PIPEGEN"));

            List<string> unset = Lines("SHELL --complete 'unset HO'");
            Check("20: Tab after unset: HOME (" + Escaped(Join(unset)) + ")", unset.Contains("HOME"));
        }

        private static List<string> Trimmed(List<string> lines)
        {
            var trimmed = new List<string>();
            foreach (string l in lines) trimmed.Add(l.TrimEnd());
            return trimmed;
        }

        private static bool Contains(List<string> lines, string prefix)
        {
            foreach (string l in lines)
                if (l.StartsWith(prefix)) return true;
            return false;
        }
    }
}
