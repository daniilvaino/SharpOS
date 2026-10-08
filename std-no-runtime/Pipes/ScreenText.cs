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

        internal static void Print(View root)
        {
            if (root.TryGetChars(out char* chars, out int count))
            {
                Flush();
                PipeTransport.Print(new string(new ReadOnlySpan<char>(chars, count)));
                return;
            }
            if (root.TryGetBytes(out byte* bytes, out int length))
            {
                PrintBytes(bytes, length);
                return;
            }
            Flush();
            PipeTransport.Print(root.ToScreenString());
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

            // In the objects' colours (View.ToScreenString): the type cyan, the bytes dim.
            var line = new StringBuilder();
            line.Append("\u001b[90mSystem.\u001b[1;36mByte\u001b[0m\u001b[90m[").Append(all.Length.ToString()).Append("]");
            for (int i = 0; i < all.Length && i < 16; i++)
                line.Append(' ').Append(all[i].ToString("X2"));
            if (all.Length > 16) line.Append(" …");
            line.Append("\u001b[0m");
            PipeTransport.Print(line.ToString());
        }

        // A character cut short at the end stays for the next piece.
        private static int CompleteLength(ReadOnlySpan<byte> s)
        {
            int n = s.Length;
            for (int back = 1; back <= 3 && back <= n; back++)
            {
                byte b = s[n - back];
                if ((b & 0xC0) == 0x80) continue;            // a continuation byte
                int need = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
                return need > back ? n - back : n;
            }
            return n;
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
            s_carryLength = 0;
        }
    }
}
