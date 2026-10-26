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
            string kind = null;
            // A byte stream (cat, READ) comes in chunks that cut lines and even
            // UTF-8 sequences anywhere. This BCL has no Encoding.GetDecoder, so
            // the bytes are gathered whole and decoded once at the end.
            byte[] gathered = null;
            int gatheredLength = 0;

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
                    int length = message.Length;
                    if (gathered == null) gathered = new byte[Math.Max(4096, length)];
                    if (gatheredLength + length > gathered.Length)
                        Array.Resize(ref gathered, Math.Max(gathered.Length * 2, gatheredLength + length));
                    for (int i = 0; i < length; i++) gathered[gatheredLength + i] = (byte)message[i];
                    gatheredLength += length;
                    continue;
                }
                string text = message.Kind == ViewKind.String ? (string)message : message.ToString();
                // The type's name is the region's text: keep a copy of our own.
                if (kind == null) kind = message.Kind == ViewKind.String ? "text" : new string((message.TypeName ?? "null").AsSpan());
                AddLines(lines, text ?? "null");
            }
            }
            catch (PipeException e) when (e.IsStandardEndBroken)
            {
                broken = true;
            }
            if (gatheredLength > 0)
                AddLines(lines, Encoding.UTF8.GetString(gathered, 0, gatheredLength));
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

        // One message may hold many lines (cat sends a file as one): split,
        // expand tabs, and show control characters instead of obeying them.
        private static void AddLines(List<string> lines, string text)
        {
            var b = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    lines.Add(b.ToString());
                    b.Clear();
                }
                else if (c == '\r')
                {
                    // CRLF: the \n ends the line.
                }
                else if (c == '\t')
                {
                    b.Append(' ', TabWidth - b.Length % TabWidth);
                }
                else if (c == '\u001b')
                {
                    b.Append("^[");
                }
                else if (c < ' ')
                {
                    b.Append('^').Append((char)(c + '@'));
                }
                else
                {
                    b.Append(c);
                }
            }
            // A message's last line, even without \n; but "a\n" is one line, not two.
            if (b.Length > 0 || text.Length == 0 || text[text.Length - 1] != '\n')
                lines.Add(b.ToString());
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
