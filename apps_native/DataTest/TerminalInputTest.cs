using System;
using System.Collections.Generic;
using System.Text;
using SharpOS.AppSdk;
using SharpOS.Std;

namespace DataApps
{
    // Test 29 (step217): the terminal's input as bytes. The parser on byte
    // strings as a terminal sends them — no keyboard needed; then the real
    // terminal: Console.GetCursorPosition asks it (ESC [6n) and reads the
    // answer from the input.
    internal static unsafe partial class AppEntry
    {
        private static void TerminalInputBytes()
        {
            Check("29: letters, Enter, Backspace (DEL), Tab", Keys("aZ\r\u007f\t") == "a:A Z:Z+S Enter Backspace Tab");
            Check("29: Ctrl+C is 0x03, Ctrl+L 0x0C", Keys("\u0003\u000c") == "C+C L+C");
            Check("29: arrows, Home, End", Keys("\u001b[A\u001b[B\u001b[C\u001b[D\u001b[H\u001b[F")
                  == "UpArrow DownArrow RightArrow LeftArrow Home End");
            Check("29: modifiers 1;5 Ctrl, 1;2 Shift, 1;3 Alt", Keys("\u001b[1;5C\u001b[1;2A\u001b[1;3D")
                  == "RightArrow+C UpArrow+S LeftArrow+A");
            Check("29: Insert Delete PgUp PgDn, Ctrl+Delete", Keys("\u001b[2~\u001b[3~\u001b[5~\u001b[6~\u001b[3;5~")
                  == "Insert Delete PageUp PageDown Delete+C");
            Check("29: F1-F4 (SS3), F5 F12 (CSI ~), Shift+F1", Keys("\u001bOP\u001bOQ\u001bOR\u001bOS\u001b[15~\u001b[24~\u001b[1;2P")
                  == "F1 F2 F3 F4 F5 F12 F1+S");
            Check("29: back-tab ESC [Z, Alt+x ESC x", Keys("\u001b[Z\u001bx") == "Tab+S x:X+A");
            Check("29: a lone Esc, then a key", Keys("\u001b", "q") == "Escape q:Q");
            Check("29: UTF-8: Cyrillic and a box rune", Keys("ж─") == "ж:None ─:None");
            Check("29: answers swallowed: device attributes, OSC, an unknown report",
                  Keys("\u001b[?1;2cq\u001b]11;rgb:0/0/0\u0007w\u001b[8;24;80tz") == "q:Q w:W z:Z");

            RoundTrip();

            var parser = Parser(Encoding.UTF8.GetBytes("a\u001b[12;40Rb"));
            parser.CursorQueryPending = true;
            string got = Drain(parser);
            Check("29: a cursor report while asked: kept, keys around it delivered (" + got + ")",
                  got == "a:A b:B" && parser.ReportedTop == 11 && parser.ReportedLeft == 39);

            // The real terminal: write at a known column, ask.
            if (!TerminalKeys.Available) { Check("29: TerminalRead service", false); return; }
            Console.SetCursorPosition(7, Console.WindowHeight - 1);
            (int left, int top) = Console.GetCursorPosition();
            AppHost.Terminal.Write("\r\n");
            Check("29: Console.GetCursorPosition from the terminal's answer (" + left.ToString() + ", " + top.ToString() + ")",
                  left == 7 && top == Console.WindowHeight - 1);
        }

        // Test 30: who gets the terminal's input. No keyboard needed: the
        // terminal's answer to a question goes out the same way as keys.
        private static void TerminalOwnership()
        {
            if (!TerminalKeys.Available) { Check("30: TerminalRead service", false); return; }
            Check("30: the program the shell ran owns the terminal", Process.OwnsTerminal);

            Check("30: a job takes the terminal", Process.BeginForegroundJob() && !Process.OwnsTerminal);
            AppHost.Terminal.Write("\u001b[6n");
            Check("30: the answer is not given to the one that gave the terminal away", ReadTerminal() == 0);
            Process child = Process.Start("DATATEST.EXE", new[] { "--child", "terminal-answer" });
            Process.ForegroundJobStarted();
            child.WaitForExit();
            Check("30: it waited for the job's program (" + child.ExitCode.ToString() + ")", child.ExitCode == 0);
            child.Dispose();
            Check("30: the job gone, the terminal is back by itself", Process.OwnsTerminal);
            Check("30: closing the job after that", Process.EndForegroundJob() && Process.OwnsTerminal);

            Process.BeginForegroundJob();
            child = Process.Start("DATATEST.EXE", new[] { "--child", "terminal-lost" });
            Process.ForegroundJobStarted();
            Check("30: closing a job still running takes it back", Process.EndForegroundJob() && Process.OwnsTerminal);
            child.WaitForExit();
            Check("30: the program left in it no longer owns it (" + child.ExitCode.ToString() + ")", child.ExitCode == 0);
            child.Dispose();
        }

        private static int ReadTerminal()
        {
            byte* buffer = stackalloc byte[64];
            return ((delegate* unmanaged<byte*, int, int>)AppRuntime.Services->TerminalReadAddress)(buffer, 64);
        }

        // Its parent asked the terminal where the cursor is and could not read
        // the answer; it is here, whole, up to the R.
        private static int TerminalAnswerChild()
        {
            if (!Process.OwnsTerminal) return 2;
            byte* buffer = stackalloc byte[64];
            var read = (delegate* unmanaged<byte*, int, int>)AppRuntime.Services->TerminalReadAddress;
            for (int tries = 0; tries < 500; tries++)
            {
                int n = read(buffer, 64);
                for (int i = 0; i < n; i++)
                    if (buffer[i] == (byte)'R') return 0;
                System.Threading.Thread.Sleep(10);
            }
            return 3;
        }

        // Owns the terminal at the start, or will; ends when it has lost it.
        private static int TerminalLostChild()
        {
            bool owned = false;
            for (int tries = 0; tries < 500; tries++)
            {
                if (Process.OwnsTerminal) owned = true;
                else if (owned || tries > 50) return 0;
                System.Threading.Thread.Sleep(10);
            }
            return 3;
        }

        // The keys a terminal's bytes make, "char:Key+mods" or "Key+mods" for
        // keys with no character; pieces fed with a pause between them.
        private static string Keys(params string[] pieces)
        {
            var parts = new List<byte[]>();
            foreach (string p in pieces) parts.Add(Encoding.UTF8.GetBytes(p));
            int part = 0, at = 0;
            var parser = new TerminalKeyParser(timeout =>
            {
                while (part < parts.Count && at == parts[part].Length)
                {
                    // The end of a piece is a pause: a wait shorter than forever times out.
                    if (timeout >= 0) { part++; at = 0; return -1; }
                    part++; at = 0;
                }
                if (part >= parts.Count) return -1;
                return parts[part][at++];
            });
            return Drain(parser);
        }

        // Step243: one key, two paths. A key as the raw key service gives it
        // (ConsoleKeys.FromKeyboard), turned into the terminal's bytes the way
        // the kernel does (ConsoleKeys.ToTerminal) and parsed back, is the same
        // key — for every key a terminal can say. What it cannot is checked to
        // come back as the key without what was lost.
        private static void RoundTrip()
        {
            var modifierSets = new ConsoleModifiers[8];
            for (int i = 0; i < 8; i++) modifierSets[i] = (ConsoleModifiers)i;
            const ConsoleModifiers None = 0, Alt = ConsoleModifiers.Alt, Ctrl = ConsoleModifiers.Control, Shift = ConsoleModifiers.Shift;

            int total = 0, bad = 0;
            string first = "";
            void One(char unicode, ushort scan, ConsoleModifiers modifiers, ConsoleKeyInfo expected)
            {
                total++;
                ConsoleKeyInfo key = ConsoleKeys.FromKeyboard(unicode, scan, modifiers);
                byte[] bytes = new byte[ConsoleKeys.MaxTerminalBytes];
                int n = ConsoleKeys.ToTerminal(key, bytes);
                Array.Resize(ref bytes, n);
                string got = Drain(Parser(bytes));
                string want = Describe(expected.Key == ConsoleKey.None && expected.KeyChar == '\0' ? key : expected);
                if (got == want) return;
                if (bad++ == 0) first = " first: " + ((int)unicode).ToString() + "/" + scan.ToString() + "/" + ((int)modifiers).ToString()
                                        + " want " + want + " got " + got;
            }

            // Printable ASCII, plain and with Alt (ESC before it).
            for (char c = ' '; c <= '~'; c++) { One(c, 0, None, default); One(c, 0, Alt, default); }
            // Ctrl+letter (the kernel gives the letter), with and without Alt.
            // H, I, J, M are BS, Tab, LF, CR in bytes: a terminal says the key.
            for (char c = 'a'; c <= 'z'; c++)
            {
                char code = (char)(c - 'a' + 1);
                ConsoleKeyInfo said = c == 'h' || c == 'i' || c == 'j' || c == 'm' ? ConsoleKeys.FromChar(code, None) : default;
                One(c, 0, Ctrl, said);
                One(c, 0, Ctrl | Alt, said.Key == ConsoleKey.None ? default : ConsoleKeys.FromChar(code, Alt));
            }
            // Keys with no character, under every set of modifiers.
            for (ushort scan = 0x01; scan <= 0x16; scan++)
                foreach (ConsoleModifiers m in modifierSets) One('\0', scan, m, default);
            One('\0', 0x17, None, default);
            One('\0', 0x17, Alt, default);
            foreach (char c in "\r\b\t") { One(c, 0, None, default); One(c, 0, Alt, default); }
            One('\t', 0, Shift, default);
            One(' ', 0, Ctrl, default);
            One('ж', 0, None, default);

            // What a terminal cannot say: Shift on a control character, modifiers on Enter.
            One('s', 0, Ctrl | Shift, ConsoleKeys.FromKeyboard('s', 0, Ctrl));
            One('\r', 0, Ctrl, ConsoleKeys.FromKeyboard('\r', 0, None));

            Check("29: one key, two paths: " + total.ToString() + " keys, raw = parsed terminal bytes (" + bad.ToString() + " differ" + first + ")",
                  bad == 0);

            // The raw path keeps what the bytes lose (Terminal.Gui reads it).
            ConsoleKeyInfo ctrlShiftS = ConsoleKeys.FromKeyboard('s', 0, Ctrl | Shift);
            Check("29: raw Ctrl+Shift+S keeps Shift", ctrlShiftS.Key == ConsoleKey.S && ctrlShiftS.KeyChar == '\u0013'
                  && ctrlShiftS.Modifiers == (ConsoleModifiers.Control | ConsoleModifiers.Shift));
            ConsoleKeyInfo ctrlH = ConsoleKeys.FromKeyboard('h', 0, Ctrl), ctrlBackspace = ConsoleKeys.FromKeyboard('\b', 0, Ctrl);
            Check("29: raw Ctrl+H is H, Ctrl+Backspace is Backspace",
                  ctrlH.Key == ConsoleKey.H && ctrlBackspace.Key == ConsoleKey.Backspace);
            Check("29: '?' is Oem2 with Shift, '/' Oem2 without",
                  Describe(ConsoleKeys.FromChar('?', None)) == "?:191+S" && Describe(ConsoleKeys.FromChar('/', None)) == "/:191");
        }

        private static TerminalKeyParser Parser(byte[] bytes)
        {
            int at = 0;
            return new TerminalKeyParser(timeout => at < bytes.Length ? bytes[at++] : -1);
        }

        private static string Drain(TerminalKeyParser parser)
        {
            var sb = new StringBuilder();
            while (parser.TryRead(0, out ConsoleKeyInfo key))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Describe(key));
            }
            return sb.ToString();
        }

        private static string Describe(ConsoleKeyInfo k)
        {
            string s;
            if (k.Key == ConsoleKey.Enter || k.Key == ConsoleKey.Backspace || k.Key == ConsoleKey.Tab || k.Key == ConsoleKey.Escape
                || k.KeyChar == '\0' || k.KeyChar < ' ')
                s = Name(k.Key);
            else
                s = k.KeyChar.ToString() + ":" + Name(k.Key);
            string mods = "";
            if ((k.Modifiers & ConsoleModifiers.Shift) != 0) mods += "S";
            if ((k.Modifiers & ConsoleModifiers.Alt) != 0) mods += "A";
            if ((k.Modifiers & ConsoleModifiers.Control) != 0) mods += "C";
            return mods.Length > 0 ? s + "+" + mods : s;
        }

        // Enum names by hand: ToString of an enum has no metadata here.
        private static string Name(ConsoleKey key)
        {
            if (key >= ConsoleKey.A && key <= ConsoleKey.Z) return ((char)('A' + (key - ConsoleKey.A))).ToString();
            if (key >= ConsoleKey.F1 && key <= ConsoleKey.F12) return "F" + (key - ConsoleKey.F1 + 1).ToString();
            return key switch
            {
                ConsoleKey.Enter => "Enter",
                ConsoleKey.Backspace => "Backspace",
                ConsoleKey.Tab => "Tab",
                ConsoleKey.Escape => "Escape",
                ConsoleKey.UpArrow => "UpArrow",
                ConsoleKey.DownArrow => "DownArrow",
                ConsoleKey.LeftArrow => "LeftArrow",
                ConsoleKey.RightArrow => "RightArrow",
                ConsoleKey.Home => "Home",
                ConsoleKey.End => "End",
                ConsoleKey.Insert => "Insert",
                ConsoleKey.Delete => "Delete",
                ConsoleKey.PageUp => "PageUp",
                ConsoleKey.PageDown => "PageDown",
                ConsoleKey.None => "None",
                _ => ((int)key).ToString(),
            };
        }
    }
}
