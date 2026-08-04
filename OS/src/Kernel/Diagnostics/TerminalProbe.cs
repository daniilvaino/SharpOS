using OS.Hal;
using XtermSharp;

namespace OS.Kernel.Diagnostics
{
    // Self-test for the vendored terminal engine running inside the kernel
    // (vendor/XtermSharp, front-end in OS/src/Hal/TerminalConsole.cs).
    //
    // Deliberately checks the *engine*, not the pixels: it feeds escape sequences
    // and reads the resulting cell grid back, so a failure says which control
    // function misbehaved rather than "the screen looks wrong". Pixel-level
    // verification is FbConsole.Checksum's job and belongs in a later probe, once
    // there is a golden to compare against.
    //
    // Runs on a private Terminal instance, never on the live console one — a probe
    // must not scribble over the boot log it is being reported on.
    internal static class TerminalProbe
    {
        public static void Run()
        {
            Log.Write(LogLevel.Info, "[term] engine self-test");

            int passed = 0;
            int total = 0;

            var terminal = new Terminal(null, new TerminalOptions { Cols = 40, Rows = 8, ConvertEol = false });

            // Plain text lands where it should.
            Feed(terminal, "SharpOS");
            Check(ref passed, ref total, "print", RowText(terminal, 0) == "SharpOS");

            // CR/LF move the cursor rather than printing.
            Feed(terminal, "\r\nsecond");
            Check(ref passed, ref total, "crlf", RowText(terminal, 1) == "second");

            // CUP: row 4, column 3 (1-based) -> zero-based 3,2.
            Feed(terminal, "\u001b[4;3HX");
            Check(ref passed, ref total, "cup", terminal.Buffer.Y == 3 && RowText(terminal, 3) == "  X");

            // SGR sets the foreground in the cell attribute (fg is bits 9..17).
            Feed(terminal, "\u001b[H\u001b[31mR");
            Check(ref passed, ref total, "sgr-fg", ((terminal.Buffer.Lines[terminal.Buffer.YDisp][0].Attribute >> 9) & 0x1ff) == 1);

            // ED 2 clears the screen without moving the cursor.
            Feed(terminal, "\u001b[0m\u001b[2J");
            Check(ref passed, ref total, "ed2", RowText(terminal, 0).Length == 0 && RowText(terminal, 1).Length == 0);

            // Scroll region: with 2..4 set, printing past the bottom scrolls only inside it.
            Feed(terminal, "\u001b[2;4r\u001b[2;1Ha\r\nb\r\nc\r\nd");
            Check(ref passed, ref total, "decstbm", RowText(terminal, 1) == "b" && RowText(terminal, 3) == "d");

            // Wide glyph occupies two cells: the second carries width 0.
            Feed(terminal, "\u001bc\u001b[H日");
            var first = terminal.Buffer.Lines[terminal.Buffer.YDisp][0];
            var second = terminal.Buffer.Lines[terminal.Buffer.YDisp][1];
            Check(ref passed, ref total, "wide", first.Width == 2 && second.Width == 0);

            // Autowrap: 40 columns, so the 41st character starts the next row.
            Feed(terminal, "\u001bc\u001b[H");
            for (int i = 0; i < 41; i++)
                Feed(terminal, "z");
            Check(ref passed, ref total, "wrap", RowText(terminal, 0).Length == 40 && RowText(terminal, 1) == "z");

            Log.Write(LogLevel.Info, passed == total ? "[term] self-test PASS" : "[term] self-test FAIL");
            Log.Write(LogLevel.Info, Format(passed, total));
        }

        // Number formatting goes through std (post-Phase 2 this is safe).
        private static string Format(int passed, int total)
        {
            return "[term] " + passed.ToString() + "/" + total.ToString() + " cases";
        }

        private static void Feed(Terminal terminal, string text)
        {
            // The probe's inputs are ASCII plus one CJK glyph; encode inline rather than
            // pulling in Encoding, which would allocate per call.
            var bytes = new byte[text.Length * 3];
            int length = 0;
            for (int i = 0; i < text.Length; i++)
            {
                uint value = text[i];
                if (value < 0x80)
                {
                    bytes[length++] = (byte)value;
                }
                else if (value < 0x800)
                {
                    bytes[length++] = (byte)(0xC0 | (value >> 6));
                    bytes[length++] = (byte)(0x80 | (value & 0x3F));
                }
                else
                {
                    bytes[length++] = (byte)(0xE0 | (value >> 12));
                    bytes[length++] = (byte)(0x80 | ((value >> 6) & 0x3F));
                    bytes[length++] = (byte)(0x80 | (value & 0x3F));
                }
            }
            terminal.Feed(bytes, length);
        }

        private static string RowText(Terminal terminal, int row)
        {
            var buffer = terminal.Buffer;
            int index = buffer.YDisp + row;
            if (index < 0 || index >= buffer.Lines.Length)
                return string.Empty;

            var line = buffer.Lines[index];
            var text = new System.Text.StringBuilder(terminal.Cols);
            for (int x = 0; x < terminal.Cols && x < line.Length; x++)
            {
                var cell = line[x];
                if (cell.Width == 0) continue;
                text.Append(cell.Code == 0 ? ' ' : (char)cell.Code);
            }
            return text.ToString().TrimEnd();
        }

        private static void Check(ref int passed, ref int total, string name, bool ok)
        {
            total++;
            if (ok) passed++;
            Log.Write(ok ? LogLevel.Info : LogLevel.Warn, (ok ? "[term] ok   " : "[term] FAIL ") + name);
        }
    }
}
