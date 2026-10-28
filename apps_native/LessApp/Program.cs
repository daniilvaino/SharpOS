using System;
using System.Collections.Generic;
using System.Runtime;
using System.Text;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace LessApp
{
    // LESS — a pager for the end of a pipe: text or records of any type,
    // read whole, then shown a screen at a time with scrolling and search.
    internal static unsafe class AppEntry
    {
        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return Run(PipeApps.StressArgs.Apply(AppHost.Arguments));
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run(AppHost.Arguments);

        private const int ExitOk = 0, ExitNoData = 1, ExitUsage = 2;
        private const int TabWidth = 8;

        private static int Run(string[] args)
        {
            foreach (string a in args)
            {
                if (a == "--help" || a == "-h" || a == "-?")
                {
                    PrintHelp();
                    return ExitOk;
                }
                Console.Error.WriteLine("less: unknown argument '" + a + "' (less reads its pipe; try: less --help)");
                return ExitUsage;
            }

            var lines = new List<string>();
            var builder = new LineBuilder(lines);
            string kind = null;
            // A byte stream (cat, READ) comes in chunks that cut lines and even
            // UTF-8 sequences anywhere. Decoded as it comes, a cut sequence
            // carried to the next chunk: gathered whole, a 16 MiB boot log was
            // a 32 MiB array and then a 32 MiB string — each past what one
            // object may take (step213).
            var decoder = new Utf8Pieces();

            // A writer that ends with a code other than 0 breaks the pipe
            // (step194 §3) — a test battery exits with its passed count. What
            // arrived before the break is still shown, marked as cut short.
            bool broken = false;
            try
            {
            foreach (View message in Pipe.Read())
            {
                // A view lives until the next message: turn it into data now.
                if (IsByteArray(message))
                {
                    if (kind == null) kind = "bytes";
                    builder.Add(decoder.Decode(message));
                    continue;
                }
                string text = message.Kind == ViewKind.String ? (string)message : message.ToString();
                // The type's name is the region's text: keep a copy of our own.
                if (kind == null) kind = message.Kind == ViewKind.String ? "text" : new string((message.TypeName ?? "null").AsSpan());
                builder.Add(text ?? "null");
                builder.EndMessage(empty: text != null && text.Length == 0);
            }
            }
            catch (PipeException e) when (e.IsStandardEndBroken)
            {
                broken = true;
            }
            builder.Add(decoder.Finish());
            builder.EndMessage();
            if (lines.Count == 0)
            {
                Console.Error.WriteLine("less: the input is empty");
                return ExitNoData;
            }

            var pager = new Pager(lines, broken ? (kind ?? "text") + ", cut short" : kind);
            pager.Loop();
            return ExitOk;
        }

        private static bool IsByteArray(View v)
            => v.Kind == ViewKind.Array && v.TypeName == "System.Byte[]";

        // A screen line longer than this goes on in the next one: a file
        // without line breaks was one string of all of it.
        private const int MaxLineChars = 4096;

        // A run of NULs this long or longer is one marker, not "^@" each: the
        // boot log is a fixed 16 MiB file, its unwritten sectors zeros.
        private const int NulRunMarked = 16;

        // One message may hold many lines (cat sends a file as one): split,
        // expand tabs, and show control characters instead of obeying them.
        // A byte stream is one text across its chunks; any other message ends
        // its last line (EndMessage).
        private sealed class LineBuilder
        {
            private readonly List<string> _lines;
            private readonly StringBuilder _b = new StringBuilder();
            private long _nuls;
            private bool _open;     // something since the last line break

            public LineBuilder(List<string> lines) => _lines = lines;

            public void Add(string text)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '\0') { _nuls++; _open = true; continue; }
                    PutNuls();
                    _open = true;
                    if (c == '\n')
                    {
                        Line();
                        _open = false;
                    }
                    else if (c == '\r')
                    {
                        // CRLF: the \n ends the line.
                    }
                    else if (c == '\t')
                    {
                        Append(new string(' ', TabWidth - _b.Length % TabWidth));
                    }
                    else if (c == '\u001b')
                    {
                        Append("^[");
                    }
                    else if (c < ' ')
                    {
                        Append("^" + (char)(c + '@'));
                    }
                    else
                    {
                        if (_b.Length >= MaxLineChars) Line();
                        _b.Append(c);
                    }
                }
            }

            // A message's last line, even without \n; but "a\n" is one line,
            // not two, and an empty message is an empty line.
            public void EndMessage(bool empty = false)
            {
                PutNuls();
                if (_open || empty) Line();
                _open = false;
            }

            private void PutNuls()
            {
                if (_nuls == 0) return;
                if (_nuls < NulRunMarked)
                    for (long i = 0; i < _nuls; i++) Append("^@");
                else
                    Append("^@ ×" + _nuls.ToString() + " ");
                _nuls = 0;
            }

            private void Append(string s)
            {
                if (_b.Length + s.Length > MaxLineChars) Line();
                _b.Append(s);
            }

            private void Line()
            {
                _lines.Add(_b.ToString());
                _b.Clear();
            }
        }

        // UTF-8 decoded a chunk at a time: a sequence cut at a chunk's end is
        // kept and finished by the next one. This BCL has no Encoding.GetDecoder.
        private sealed class Utf8Pieces
        {
            private byte[] _buffer = new byte[4096];
            private int _carried;

            public string Decode(View bytes)
            {
                int length = bytes.Length;
                if (_carried + length > _buffer.Length)
                {
                    var grown = new byte[_carried + length];
                    Array.Copy(_buffer, grown, _carried);
                    _buffer = grown;
                }
                for (int i = 0; i < length; i++) _buffer[_carried + i] = (byte)bytes[i];
                int total = _carried + length;
                int whole = CompleteLength(_buffer, total);
                string text = Encoding.UTF8.GetString(_buffer, 0, whole);
                _carried = total - whole;
                Array.Copy(_buffer, whole, _buffer, 0, _carried);
                return text;
            }

            public string Finish()
            {
                string text = _carried == 0 ? "" : Encoding.UTF8.GetString(_buffer, 0, _carried);
                _carried = 0;
                return text;
            }

            // Where the last whole sequence ends: a lead byte in the last three
            // whose sequence runs past the end starts the carried part.
            private static int CompleteLength(byte[] b, int length)
            {
                for (int back = 1; back <= 3 && back <= length; back++)
                {
                    byte lead = b[length - back];
                    if ((lead & 0xC0) == 0x80) continue;          // a continuation: look further
                    int need = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC0 ? 2 : 1;
                    return need > back ? length - back : length;
                }
                return length;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("less - a pager for the end of a pipe: proctest | less, synth 1000 | less");
            Console.WriteLine("  Up/Down j/k      a line          PgUp/PgDn b/Space  a screen");
            Console.WriteLine("  Home/End g/G     top / bottom    Left/Right         sideways");
            Console.WriteLine("  /text Enter      search (any case)   n / N          next / previous match");
            Console.WriteLine("  q                quit");
            Console.WriteLine("records are shown one per line as their text; the whole input is read first.");
            Console.WriteLine("exit: 0 shown, 1 empty input, 2 bad arguments");
        }

        private sealed class Pager
        {
            private readonly List<string> _lines;
            private readonly string _kind;
            private int _width, _height;   // the screen
            private int _top;              // first line shown
            private int _left;             // first column shown
            private string _search;        // the last search, or null
            private string _message;       // a one-off note for the status line

            public Pager(List<string> lines, string kind)
            {
                _lines = lines;
                _kind = kind;
            }

            private int Rows => Math.Max(1, _height - 1);
            private int MaxTop => Math.Max(0, _lines.Count - Rows);

            public void Loop()
            {
                MeasureScreen();
                Console.CursorVisible = false;
                try
                {
                    while (true)
                    {
                        Draw();
                        ConsoleKeyInfo key = Console.ReadKey(true);
                        _message = null;
                        if (!Handle(key)) break;
                    }
                }
                finally
                {
                    Console.ResetColor();
                    Console.Clear();
                    Console.CursorVisible = true;
                }
            }

            private void MeasureScreen()
            {
                _width = Console.WindowWidth;
                _height = Console.WindowHeight;
                if (_width < 20) _width = 80;
                if (_height < 3) _height = 25;
            }

            // false: quit.
            private bool Handle(ConsoleKeyInfo key)
            {
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow: Scroll(-1); return true;
                    case ConsoleKey.DownArrow: Scroll(1); return true;
                    case ConsoleKey.PageUp: Scroll(-Rows); return true;
                    case ConsoleKey.PageDown: Scroll(Rows); return true;
                    case ConsoleKey.Home: _top = 0; return true;
                    case ConsoleKey.End: _top = MaxTop; return true;
                    case ConsoleKey.LeftArrow: _left = Math.Max(0, _left - _width / 4); return true;
                    case ConsoleKey.RightArrow: _left += _width / 4; return true;
                    case ConsoleKey.Enter: Scroll(1); return true;
                }
                switch (key.KeyChar)
                {
                    case 'q':
                    case 'Q': return false;
                    case 'j': Scroll(1); return true;
                    case 'k': Scroll(-1); return true;
                    case ' ': Scroll(Rows); return true;
                    case 'b': Scroll(-Rows); return true;
                    case 'g': _top = 0; return true;
                    case 'G': _top = MaxTop; return true;
                    case '/': Search(); return true;
                    case 'n': Next(1); return true;
                    case 'N': Next(-1); return true;
                }
                return true;
            }

            private void Scroll(int by) => _top = Math.Max(0, Math.Min(MaxTop, _top + by));

            private void Search()
            {
                string text = Prompt("/");
                if (text == null) return;             // Esc
                if (text.Length == 0)
                {
                    if (_search == null) return;      // nothing to repeat
                }
                else
                {
                    _search = text;
                }
                Find(_top, 1, true);
            }

            private void Next(int direction)
            {
                if (_search == null)
                {
                    _message = "no search yet: / to search";
                    return;
                }
                Find(_top + direction, direction, false);
            }

            // From line `from` (included) in `direction`, wrapping once round the input.
            private void Find(int from, int direction, bool fresh)
            {
                int n = _lines.Count;
                for (int step = 0; step < n; step++)
                {
                    int i = ((from + direction * step) % n + n) % n;
                    int at = _lines[i].IndexOf(_search, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) continue;
                    _top = Math.Min(i, MaxTop);
                    // Bring the match into view sideways if it is off the screen.
                    if (at < _left || at + _search.Length > _left + _width - 1)
                        _left = Math.Max(0, at - _width / 4);
                    bool wrapped = direction > 0 ? i < from % n : i > ((from % n) + n) % n;
                    _message = wrapped && !fresh ? "search wrapped" : null;
                    return;
                }
                _message = "not found: " + _search;
            }

            // A line typed on the status row: Enter takes it, Esc gives null.
            private string Prompt(string lead)
            {
                var text = new StringBuilder();
                while (true)
                {
                    Console.SetCursorPosition(0, _height - 1);
                    string shown = lead + text.ToString();
                    if (shown.Length > _width - 1) shown = shown.Substring(shown.Length - (_width - 1));
                    Console.Write(shown.PadRight(_width - 1));
                    Console.SetCursorPosition(Math.Min(shown.Length, _width - 1), _height - 1);
                    Console.CursorVisible = true;
                    ConsoleKeyInfo k = Console.ReadKey(true);
                    Console.CursorVisible = false;
                    if (k.Key == ConsoleKey.Enter) return text.ToString();
                    if (k.Key == ConsoleKey.Escape) return null;
                    if (k.Key == ConsoleKey.Backspace)
                    {
                        if (text.Length > 0) text.Length--;
                        else return null;             // Backspace on an empty prompt leaves it
                        continue;
                    }
                    if (k.KeyChar >= ' ') text.Append(k.KeyChar);
                }
            }

            private void Draw()
            {
                int usable = _width - 1;              // never the last column: no wrap, no scroll
                for (int row = 0; row < Rows; row++)
                {
                    Console.SetCursorPosition(0, row);
                    int i = _top + row;
                    if (i >= _lines.Count)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write("~".PadRight(usable));
                        Console.ResetColor();
                        continue;
                    }
                    string line = _lines[i];
                    string part = _left >= line.Length ? "" : line.Substring(_left, Math.Min(usable, line.Length - _left));
                    WriteHighlighted(part);
                    if (part.Length < usable) Console.Write(new string(' ', usable - part.Length));
                }
                DrawStatus();
            }

            // The visible part of a line, with the search's matches in colour.
            private void WriteHighlighted(string part)
            {
                if (string.IsNullOrEmpty(_search) || part.Length == 0)
                {
                    Console.Write(part);
                    return;
                }
                int at = 0;
                while (at < part.Length)
                {
                    // IndexOf(string, int, StringComparison) is not in this BCL.
                    int hit = part.Substring(at).IndexOf(_search, StringComparison.OrdinalIgnoreCase);
                    if (hit >= 0) hit += at;
                    if (hit < 0)
                    {
                        Console.Write(part.Substring(at));
                        return;
                    }
                    if (hit > at) Console.Write(part.Substring(at, hit - at));
                    int len = Math.Min(_search.Length, part.Length - hit);
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write(part.Substring(hit, len));
                    Console.ResetColor();
                    at = hit + len;
                }
            }

            private void DrawStatus()
            {
                int last = Math.Min(_lines.Count, _top + Rows);
                int pct = _lines.Count == 0 ? 100 : (int)(100L * last / _lines.Count);
                var s = new StringBuilder();
                s.Append(' ').Append((_top + 1).ToString()).Append('-').Append(last.ToString())
                 .Append(" of ").Append(_lines.Count.ToString()).Append(" (").Append(pct.ToString()).Append("%)");
                if (_left > 0) s.Append("  col ").Append((_left + 1).ToString());
                if (_kind != null) s.Append("  ").Append(_kind);
                if (_message != null) s.Append("  | ").Append(_message);
                else s.Append("  | q quit  / search  n next  arrows/PgUp/PgDn");
                string status = s.ToString();
                if (status.Length > _width - 1) status = status.Substring(0, _width - 1);

                Console.SetCursorPosition(0, _height - 1);
                Console.ForegroundColor = ConsoleColor.Black;
                Console.BackgroundColor = ConsoleColor.Gray;
                Console.Write(status.PadRight(_width - 1));
                Console.ResetColor();
            }
        }
    }
}
