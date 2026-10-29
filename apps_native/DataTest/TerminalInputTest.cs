using System;
using System.Collections.Generic;
using System.Text;
using SharpOS.AppSdk;

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
