using System;
using System.Text;

namespace SharpOS.Std.Pipes
{
    /// <summary>
    /// How a message looks on the screen when a program has no output
    /// (step197): a string as it is, on its line; a byte[] as text when it is
    /// UTF-8 without control characters (tab, CR and LF allowed), with no line
    /// added — the bytes of a file arrive in pieces; otherwise its size and its
    /// first bytes in hex; anything else as a view prints it.
    /// </summary>
    internal static unsafe class ScreenText
    {
        // The start of a character cut by the end of the previous byte[].
        private static byte[] s_carry;
        private static int s_carryLength;

        // A run of objects of one type beside an inspector (step237): the
        // first on its line, the rest counted on the summary line under it,
        // which is rewritten in place while it is the last thing on the screen.
        private static string s_runType;
        private static int s_runCount;
        private static int s_runShown;
        private static int s_runPainted;
        private static IScreenInspector s_runInspector;

        // How often the count on the summary line moves, in milliseconds.
        private const int SummaryInterval = 100;

        internal static void Print(View root, IScreenInspector inspector = null)
        {
            if (root.TryGetChars(out char* chars, out int count))
            {
                EndRun();
                Flush();
                PipeTransport.Print(Visible(new ReadOnlySpan<char>(chars, count), keepColours: true));
                return;
            }
            if (root.TryGetBytes(out byte* bytes, out int length))
            {
                EndRun();
                PrintBytes(bytes, length);
                return;
            }
            Flush();
            if (inspector != null && inspector.Summary)
            {
                string type = root.TypeName;
                if (s_runCount > 0 && type == s_runType)
                {
                    s_runCount++;
                    int now = Environment.TickCount;
                    if (s_runShown == 1 || now - s_runPainted >= SummaryInterval) PaintSummary(now);
                    return;
                }
                EndRun();
                s_runType = type;
                s_runCount = 1;
                s_runShown = 1;
                s_runInspector = inspector;
            }
            else EndRun();
            PipeTransport.Print(root.ToScreenString());
        }

        // `▸ 1000 × T  #3`: the count, the type, and the inspector's mark.
        private static void PaintSummary(int now)
        {
            var line = new StringBuilder();
            line.Append('\r').Append("\u001b[90m▸\u001b[0m ").Append(s_runCount.ToString())
                .Append(" \u001b[90m×\u001b[0m ");
            string type = s_runType ?? "";
            int dot = type.LastIndexOf('.');
            if (dot > 0) line.Append("\u001b[90m").Append(type, 0, dot + 1).Append("\u001b[0m");
            line.Append("\u001b[1;36m").Append(dot > 0 ? type.Substring(dot + 1) : type).Append("\u001b[0m");
            string mark = s_runInspector?.Mark;
            if (!string.IsNullOrEmpty(mark)) line.Append("\u001b[90m  ").Append(mark).Append("\u001b[0m");
            line.Append("\u001b[K");
            PipeTransport.PrintText(line.ToString());
            s_runShown = s_runCount;
            s_runPainted = now;
        }

        /// <summary>The run is over: its summary line shows the last count and ends.</summary>
        internal static void EndRun()
        {
            if (s_runCount > 1)
            {
                if (s_runShown != s_runCount) PaintSummary(Environment.TickCount);
                PipeTransport.PrintText("\n");
            }
            s_runCount = 0;
            s_runType = null;
            s_runInspector = null;
        }

        private static void PrintBytes(byte* bytes, int length)
        {
            // The carried start of a character comes first.
            byte[] joined = null;
            if (s_carryLength > 0)
            {
                joined = new byte[s_carryLength + length];
                Array.Copy(s_carry, joined, s_carryLength);
                new ReadOnlySpan<byte>(bytes, length).CopyTo(new Span<byte>(joined, s_carryLength, length));
                s_carryLength = 0;
            }
            ReadOnlySpan<byte> all = joined != null ? joined : new ReadOnlySpan<byte>(bytes, length);

            int complete = CompleteLength(all);
            if (IsText(all.Slice(0, complete)))
            {
                PipeTransport.PrintText(Encoding.UTF8.GetString(all.Slice(0, complete)));
                int rest = all.Length - complete;
                if (rest > 0)
                {
                    s_carry ??= new byte[4];
                    all.Slice(complete).CopyTo(s_carry);
                    s_carryLength = rest;
                }
                return;
            }

            PrintHex(all);
        }

        // In the objects' colours (View.ToScreenString): the type cyan, the bytes dim.
        private static void PrintHex(ReadOnlySpan<byte> all)
        {
            var line = new StringBuilder();
            line.Append("\u001b[90mSystem.\u001b[1;36mByte\u001b[0m\u001b[90m[").Append(all.Length.ToString()).Append("]");
            for (int i = 0; i < all.Length && i < 16; i++)
                line.Append(' ').Append(all[i].ToString("X2"));
            if (all.Length > 16) line.Append(" …");
            line.Append("\u001b[0m");
            PipeTransport.Print(line.ToString());
        }

        // A character cut short at the end stays for the next piece. Only a
        // real lead byte starts one: C2..F4 (step206 — FF, the end of a CBOR
        // array, was taken for the start of four bytes and never shown).
        private static int CompleteLength(ReadOnlySpan<byte> s)
        {
            int n = s.Length;
            for (int back = 1; back <= 3 && back <= n; back++)
            {
                byte b = s[n - back];
                if ((b & 0xC0) == 0x80) continue;            // a continuation byte
                int need = b >= 0xC2 && b < 0xE0 ? 2 : b >= 0xE0 && b < 0xF0 ? 3 : b >= 0xF0 && b < 0xF5 ? 4 : 1;
                return need > back ? n - back : n;
            }
            return n;
        }

        /// <summary>
        /// A string of data as the screen may show it (step238): a control
        /// character as `less` shows one, ^[ for ESC, ^H for backspace; with
        /// <paramref name="keepColours"/> a colour sequence (ESC [ … m) stays.
        /// A string in a pipe is data, not terminal text — that is the
        /// TerminalText attribute's — and a log line read back with its escapes
        /// switched the terminal to the alternate screen and left it there,
        /// the side panel and F12 with it.
        /// </summary>
        internal static string Visible(ReadOnlySpan<char> s, bool keepColours)
        {
            int i = 0;
            while (i < s.Length && !IsControl(s[i])) i++;
            if (i == s.Length) return new string(s);

            var text = new StringBuilder(s.Length + 8);
            text.Append(s.Slice(0, i));
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (!IsControl(c)) { text.Append(c); continue; }
                if (c == '\u001b' && keepColours)
                {
                    int end = ColourEnd(s, i);
                    if (end > i)
                    {
                        text.Append(s.Slice(i, end - i + 1));
                        i = end;
                        continue;
                    }
                }
                if (c < ' ') text.Append('^').Append((char)(c + '@'));
                else if (c == '\u007f') text.Append("^?");
                else text.Append('?');                       // C1: 0x80-0x9F
            }
            return text.ToString();
        }

        // Tab is the one control a line keeps.
        private static bool IsControl(char c)
            => (c < ' ' && c != '\t') || c == '\u007f' || (c >= '\u0080' && c <= '\u009f');

        // ESC [ digits and semicolons m: the index of the m, or -1.
        private static int ColourEnd(ReadOnlySpan<char> s, int at)
        {
            if (at + 1 >= s.Length || s[at + 1] != '[') return -1;
            for (int j = at + 2; j < s.Length; j++)
            {
                char c = s[j];
                if (c == 'm') return j;
                if ((c < '0' || c > '9') && c != ';') return -1;
            }
            return -1;
        }

        /// <summary>The end of what goes to the screen: a carried piece is shown as it is.</summary>
        internal static void End()
        {
            EndRun();
            Flush();
        }

        // Valid UTF-8 without control characters but tab, CR and LF.
        private static bool IsText(ReadOnlySpan<byte> s)
        {
            int i = 0;
            while (i < s.Length)
            {
                byte b = s[i];
                if (b < 0x80)
                {
                    if ((b < 0x20 && b != (byte)'\t' && b != (byte)'\n' && b != (byte)'\r') || b == 0x7F) return false;
                    i++;
                    continue;
                }
                int need = b >= 0xC2 && b < 0xE0 ? 1 : b >= 0xE0 && b < 0xF0 ? 2 : b >= 0xF0 && b < 0xF5 ? 3 : -1;
                if (need < 0 || i + need >= s.Length) return false;
                for (int k = 1; k <= need; k++)
                    if ((s[i + k] & 0xC0) != 0x80) return false;
                i += need + 1;
            }
            return true;
        }

        // A carried piece that will not be completed: shown as it is.
        private static void Flush()
        {
            if (s_carryLength == 0) return;
            int length = s_carryLength;
            s_carryLength = 0;
            PrintHex(new ReadOnlySpan<byte>(s_carry, 0, length));
        }
    }
}
