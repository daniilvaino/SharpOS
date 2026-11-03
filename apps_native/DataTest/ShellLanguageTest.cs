using System;
using System.Collections.Generic;
using System.IO;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace DataApps
{
    // Test 32: the shell's language. Each line in a shell of its own (sh -c);
    // what it prints is the answer.
    internal static unsafe partial class AppEntry
    {
        private static void Says(string what, string line, string expected)
        {
            string got = string.Join("|", Lines(line).ToArray());
            Check("32: " + what + " (" + Escaped(got) + ")", got == expected);
        }

        private static void ShellLanguage()
        {
            Says("x=1", "x=1; echo $x", "1");
            Says("a variable is not in the environment", "x=1; " + Self("getenv x"), "<unset>");
            Says("export NAME moves it there", "x=1; export x; " + Self("getenv x"), "1");
            Says("X=1 program, for it alone", "X32=1 " + Self("getenv X32") + "; echo [$X32]", "1|[]");
            Says("unset", "x=1; unset x; echo [$x]", "[]");
            Says("$?", "false; echo $?; true; echo $?", "1|0");
            Says("${x:-d}, ${x:+d}, ${#x}, ${x:=d}", "y=abc; echo ${x:-d} ${y:-d} ${y:+e} ${#y}; echo ${z:=q} $z", "d abc e 3|q q");

            Says("a value splits, in quotes not", "x='a  b'; for i in $x; do echo $i; done; for i in \"$x\"; do echo \"$i\"; done", "a|b|a  b");
            Says("quotes join", "echo 'a'\"b\"c\\ d", "abc d");
            Says("an empty word stays, an empty value goes", "for i in '' $none x; do echo [$i]; done", "[]|[x]");
            Says("$(...)", "echo a$(echo b)c", "abc");
            Says("$(...) of a pipeline", "echo $(echo one two | wc -w)", "2");
            Says("$(...) of a program", "x=$(SAMPLE_PIPEGEN 3 | wc -l); echo $x", "3");
            Says("$(...) objects as text", "x=$(SAMPLE_PIPEGEN 1 | head -1); echo \"$x\"", Lines("SAMPLE_PIPEGEN 1 | CONVERT --to lines")[0]);
            Says("$(...) drops the last line feeds, keeps the rest", "x=$(echo a; echo b); echo \"$x\"", "a|b");
            Says("$(...) nested", "echo $(echo $(echo deep))", "deep");
            Says("$(...): its code is $?", "x=$(false); echo $?", "1");
            Says("$(...): what changes inside stays inside", "x=1; y=$(x=2; cd /; echo $x); echo $x $y", "1 2");
            Says("backquotes", "echo `echo b`", "b");
            Says("$((...))", "x=4; echo $((x + 1)) $(($x * 2)) $((7 / 2)) $((2 ** 10)) $((x > 3 ? 8 : 9))", "5 8 3 1024 8");
            Says("$((x = ...)) assigns", "echo $((x = 5)) $x; echo $((x += 2)) $x", "5 5|7 7");
            Check("32: a division by zero is 1", Sh("echo $((1 / 0))") == 1);

            Says("if", "if true; then echo y; fi; if false; then echo n; fi", "y");
            Says("else, elif", "if false; then echo a; elif true; then echo b; else echo c; fi; if false; then :; else echo d; fi", "b|d");
            Says("if over lines", "if true\nthen\n  echo y\nfi", "y");
            Says("the condition is a list", "if false; true; then echo y; fi; if true && false; then echo n; fi", "y");
            Says("while", "i=0; while [ $i -lt 3 ]; do echo $i; i=$((i + 1)); done", "0|1|2");
            Says("until", "i=0; until [ $i -eq 2 ]; do i=$((i + 1)); done; echo $i", "2");
            Says("for", "for i in a 'b c' d; do echo $i; done", "a|b c|d");
            Says("for over $(...)", "for i in $(echo x y); do echo $i; done", "x|y");
            Says("break", "for i in 1 2 3; do if [ $i = 2 ]; then break; fi; echo $i; done; echo after", "1|after");
            Says("continue", "for i in 1 2 3; do if [ $i = 2 ]; then continue; fi; echo $i; done", "1|3");
            Says("break 2", "for i in 1 2; do for j in a b; do echo $i$j; break 2; done; done; echo after", "1a|after");
            Says("case", "for x in abc b.txt other; do case $x in a*) echo A;; *.txt|q) echo T;; *) echo O;; esac; done", "A|T|O");
            Check("32: exit in a loop, its code", Sh("for i in 1 2; do exit 7; done; exit 1") == 7);
            Says("{ }", "{ echo a; echo b; }", "a|b");
            Says("( ): what changes inside stays inside", "x=1; (x=2; cd /; echo $x); echo $x", "2|1");
            Says("!", "! false && echo y; ! true || echo z", "y|z");

            Says("a loop in a pipeline", "for i in 3 1 2; do echo $i; done | sort", "1|2|3");
            Says("a pipeline's stage sees the variables", "x=7; { echo $x; } | wc -l", "       1");
            Says("if in a pipeline", "if true; then echo a; fi | wc -l", "       1");

            Says("test, strings and numbers", "test a = a && test a != b && test 2 -lt 10 && test -n x && test -z '' && echo y", "y");
            Says("[ -f ] and [ -d ]", "[ -d /bin ] && [ -f /config/system.env ] && [ ! -e /nothing ] && echo y", "y");
            Says("-a, -o, ( )", "[ a = a -a b = b ] && [ a = b -o c = c ] && [ '(' a = b ')' -o 1 -eq 1 ] && echo y", "y");
            Check("32: [ without ] is 2, a word for a number 2", Sh("[ a = a") == 2 && Sh("[ a -eq 1 ]") == 2);
            Check("32: false is 1", Sh("[ a = b ]") == 1);

            File.WriteAllText("t32a.txt", "b\na\n");
            File.WriteAllText("t32b.txt", "c\n");
            Says("< file", "sort < t32a.txt", "a|b");
            Says("< file in a pipeline", "sort < t32a.txt | head -1", "a");
            Check("32: < a file that is not there is 1, nothing starts", Sh("sort < t32nothing") == 1);
            Says("< file keeps objects", "SAMPLE_PIPEGEN 3 > t32c.pipe; wc -l < t32c.pipe", "       3");
            Says("a loop with > file", "for i in 1 2; do echo $i; done > t32d.txt; wc -l t32d.txt", "       2 t32d.txt");
            Says("a redirect's target expands", "x=t32e.txt; echo hi > $x; head t32e.txt", "hi");
            Says("> file alone makes it", "> t32f.txt; [ -f t32f.txt ] && echo y", "y");

            Says("* matches", "for f in t32[ab].txt; do echo $f; done", "t32a.txt|t32b.txt");
            Says("? and no case", "echo T32?.TXT", "t32a.txt t32b.txt t32d.txt t32e.txt t32f.txt");
            Says("in quotes it is itself", "echo 't32*' \"t32*\" t32\\*", "t32* t32* t32*");
            Says("nothing matches: as written", "echo t32nothing*", "t32nothing*");
            Says("from a value, too", "x='t32a.*'; echo $x; echo \"$x\"", "t32a.txt|t32a.*");
            Says("across directories", "echo /conf*/system.*", "/config/system.env");

            foreach (string name in new[] { "t32a.txt", "t32b.txt", "t32c.pipe", "t32d.txt", "t32e.txt", "t32f.txt" }) File.Delete(name);

            // A script: lines joined until a construct closes; $1.
            File.WriteAllText("t32s.sh", "for i in 1 2\ndo\n  echo line $i $1\ndone\nif true\nthen exit 5\nfi\n");
            Process script = Process.Start("SHELL.EXE", new[] { "t32s.sh", "arg" });
            script.WaitForExit();
            Check("32: a script over lines, $1, exit (" + script.ExitCode.ToString() + ")", script.ExitCode == 5);
            script.Dispose();
            File.Delete("t32s.sh");
        }
    }
}
