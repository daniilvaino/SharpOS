// A console driver for SharpOS.
//
// The stock drivers all speak to a host we do not have: WindowsDriver calls
// kernel32, CursesDriver links ncurses, NetDriver drives System.Console with a
// process behind it. What we have instead is better suited than any of them —
// the kernel already runs a full terminal emulator (vendored XtermSharp) and
// paints its cell grid to the framebuffer. So this driver does what NetDriver
// does on a real terminal: writes escape sequences and lets the emulator on the
// other side lay out the screen.
//
// That decision is worth stating, because the alternative looks tempting: we
// could have added "put this rune at this cell" to the service table and drawn
// directly. It would mean a second renderer beside the one the kernel already
// has, with its own idea of scrolling, colour and cursor state — two truths
// about what is on screen, which is exactly how a display goes wrong.

using System;
using System.Collections.Generic;
using System.Text;
using SharpOS.AppSdk;

namespace Terminal.Gui
{
    /// <summary>
    /// Draws through the kernel's terminal emulator using ANSI sequences.
    /// </summary>
    public class SharpOSDriver : ConsoleDriver
    {
        private int _cols;
        private int _rows;
        private int[,,] _contents = null!;
        private bool[] _dirtyLine = null!;

        // Output is built up and written once per refresh. Every escape sequence
        // is its own service call otherwise, and a full repaint is thousands of
        // them — the difference between a screen that appears and one that
        // visibly fills in.
        private StringBuilder _out = null!;

        // The cursor the library moves with Move(); each driver keeps its own,
        // as NetDriver and WindowsDriver do.
        private int ccol, crow;

        private int _lastCol = -1;
        private int _lastRow = -1;
        private int _currentAttribute = -1;
        private CursorVisibility _cursor = CursorVisibility.Default;

        private Action? _terminalResized;

        // Every sequence below starts with this. Writing "[2J" without it does
        // not clear the screen, it prints "[2J" — the kind of mistake that
        // looks like a driver doing nothing at all.
        private const string Esc = "\u001b";

        // Const, so the concatenation happens at compile time. Written inline
        // it would build a new string on every cursor move.
        private const string CsiStart = Esc + "[";

        public override int Cols => _cols;
        public override int Rows => _rows;
        public override int Left => 0;
        public override int Top => 0;

        public override int[,,] Contents => _contents;

        public override IClipboard Clipboard { get; } = new MemoryClipboard();

        // Neither applies here: the kernel's emulator has a fixed grid and no
        // scroll region we grow into. Accepting the flags and ignoring them is
        // right — refusing would stop callers that set them out of habit.
        public override bool EnableConsoleScrolling { get; set; }
        public override bool HeightAsBuffer { get; set; }

        public override void Init(Action terminalResized)
        {
            _terminalResized = terminalResized;

            if (!AppConsole.TryGetSize(out _cols, out _rows))
            {
                // No terminal front-end on the other side. Say so rather than
                // drawing into nothing: every later call would "work" and
                // nothing would appear.
                throw new InvalidOperationException(
                    "SharpOSDriver: the kernel published no console size — is the terminal front-end up?");
            }

            _out = new StringBuilder(8192);
            ResizeScreen();
            UpdateOffScreen();

            Write(Esc + "[?1049h");   // alternate screen: keep the boot log intact
            Write(Esc + "[2J");
            Flush();

            CurrentAttribute = MakeColor(Color.White, Color.Black);

            // The colour themes, which every stock driver sets up in its own
            // Init and this one did not. Without it every scheme keeps its
            // default attributes, so Normal and Focus are the same colour —
            // the interface draws correctly and gives no sign of which control
            // is selected, which reads as "Tab does nothing".
            InitalizeColorSchemes();
        }

        public override void End()
        {
            Write(Esc + "[0m");
            Write(Esc + "[?25h");
            Write(Esc + "[?1049l");   // hand the boot log back
            Flush();
        }

        public override void ResizeScreen()
        {
            _contents = new int[_rows, _cols, 3];
            _dirtyLine = new bool[_rows];

            Clip = new Rect(0, 0, _cols, _rows);
        }

        public override void UpdateOffScreen()
        {
            int attribute = MakeColor(Color.White, Color.Black);

            for (int row = 0; row < _rows; row++)
            {
                for (int col = 0; col < _cols; col++)
                {
                    _contents[row, col, 0] = ' ';
                    _contents[row, col, 1] = attribute;

                    // Marked dirty, not clean: this is the first frame, and
                    // nothing on the screen matches it yet.
                    _contents[row, col, 2] = 1;
                }
                _dirtyLine[row] = true;
            }
        }

        public override void Move(int col, int row)
        {
            ccol = col;
            crow = row;
        }

        public override void AddRune(Rune rune)
        {
            rune = MakePrintable(rune);

            if (Clip.Contains(ccol, crow))
            {
                _contents[crow, ccol, 0] = (int)(uint)rune;
                _contents[crow, ccol, 1] = CurrentAttribute;
                _contents[crow, ccol, 2] = 1;
                _dirtyLine[crow] = true;
            }

            ccol++;

            // A wide character owns the cell after it. Marking that cell empty
            // rather than leaving whatever was there stops the tail of the
            // previous frame showing through the right half of a glyph.
            int width = Rune.ColumnWidth(rune);
            if (width > 1 && ccol < _cols && Clip.Contains(ccol, crow))
            {
                _contents[crow, ccol, 0] = ' ';
                _contents[crow, ccol, 1] = CurrentAttribute;
                _contents[crow, ccol, 2] = 1;
                ccol++;
            }

            if (ccol >= _cols)
            {
                ccol = 0;
                crow++;
                if (crow >= _rows) crow = _rows - 1;
            }
        }

        public override void AddStr(string str)
        {
            if (str == null) return;

            foreach (Rune rune in str.ToRunes()) AddRune(rune);
        }

        public override void Refresh() => UpdateScreen();

        public override void UpdateScreen()
        {
            // Only the cells the library actually changed.
            //
            // The third plane of Contents is its per-cell dirty flag, set by
            // AddRune. Redrawing whole rows instead — which is what this did
            // first — turned every keystroke into a full-screen repaint: ninety
            // eight rows of escape sequences for a status line that moved one
            // digit, tens of kilobytes down a serial line per key.
            //
            // MoveTo already skips the cursor sequence when the next cell
            // follows the last one written, so a run of changed cells costs one
            // position and then the text.
            for (int row = 0; row < _rows; row++)
            {
                if (!_dirtyLine[row]) continue;
                _dirtyLine[row] = false;

                for (int col = 0; col < _cols; col++)
                {
                    if (_contents[row, col, 2] == 0) continue;
                    _contents[row, col, 2] = 0;

                    MoveTo(col, row);
                    SetOutputAttribute(_contents[row, col, 1]);
                    AppendRune(_contents[row, col, 0]);
                }
            }

            UpdateCursor();
            Flush();
        }

        public override void UpdateCursor()
        {
            MoveTo(ccol, crow);
            Flush();
        }

        public override bool GetCursorVisibility(out CursorVisibility visibility)
        {
            visibility = _cursor;
            return true;
        }

        public override bool SetCursorVisibility(CursorVisibility visibility)
        {
            _cursor = visibility;
            Write(visibility == CursorVisibility.Invisible ? Esc + "[?25l" : Esc + "[?25h");
            return true;
        }

        public override bool EnsureCursorVisibility() => true;

        public override void PrepareToRun(MainLoop mainLoop, Action<KeyEvent> keyHandler,
            Action<KeyEvent> keyDownHandler, Action<KeyEvent> keyUpHandler,
            Action<MouseEvent> mouseHandler)
        {
            // The mouse handler is accepted and never called: SharpOS has no
            // mouse yet. Taking it and ignoring it keeps the contract; pretending
            // to report moves would be worse.
            if (mainLoop.Driver is SharpOSMainLoop loop)
                loop.SetKeyHandlers(keyHandler, keyDownHandler, keyUpHandler);
        }

        public override void SendKeys(char keyChar, ConsoleKey key, bool shift, bool alt, bool control)
        {
            // Injecting synthetic keys is a testing facility upstream. Nothing
            // in this port drives it, and a silent no-op would make a test that
            // relied on it pass for the wrong reason.
            throw new NotSupportedException("SharpOSDriver: synthetic key injection is not implemented.");
        }

        public override void Suspend() { }

        public override void StartReportingMouseMoves() { }

        public override void StopReportingMouseMoves() { }

        public override void UncookMouse() { }

        public override void CookMouse() { }

        // --- colour -------------------------------------------------------

        public override Attribute MakeAttribute(Color fore, Color back) => MakeColor(fore, back);

        public override Attribute MakeColor(Color foreground, Color background)
            => new Attribute(((int)foreground & 0xF) | (((int)background & 0xF) << 4),
                             foreground, background);

        public override bool GetColors(int value, out Color foreground, out Color background)
        {
            foreground = (Color)(value & 0xF);
            background = (Color)((value >> 4) & 0xF);
            return true;
        }

        public override void SetColors(ConsoleColor foreground, ConsoleColor background)
            => CurrentAttribute = MakeColor((Color)(int)foreground, (Color)(int)background);

        public override void SetColors(short foregroundColorId, short backgroundColorId)
            => CurrentAttribute = MakeColor((Color)foregroundColorId, (Color)backgroundColorId);

        // --- output -------------------------------------------------------

        private void MoveTo(int col, int row)
        {
            if (col == _lastCol && row == _lastRow) return;

            // Rows and columns are 1-based in the escape sequence and 0-based
            // everywhere in the library. Getting this wrong shifts the whole
            // screen by one and looks like a layout bug.
            _out.Append(CsiStart);
            AppendNumber(row + 1);
            _out.Append(';');
            AppendNumber(col + 1);
            _out.Append('H');

            _lastCol = col;
            _lastRow = row;
        }

        private void SetOutputAttribute(int attribute)
        {
            if (attribute == _currentAttribute) return;
            _currentAttribute = attribute;

            GetColors(attribute, out Color fore, out Color back);

            _out.Append(CsiStart);
            AppendNumber(AnsiForeground(fore));
            _out.Append(';');
            AppendNumber(AnsiBackground(back));
            _out.Append('m');
        }

        // Terminal.Gui's Color is the CGA order (blue in bit 0); ANSI puts red
        // there. The bit swap below is that difference, not an arbitrary table.
        private static int AnsiForeground(Color c)
        {
            int index = ToAnsiIndex((int)c & 0x7);
            return ((int)c & 0x8) != 0 ? 90 + index : 30 + index;
        }

        private static int AnsiBackground(Color c)
        {
            int index = ToAnsiIndex((int)c & 0x7);
            return ((int)c & 0x8) != 0 ? 100 + index : 40 + index;
        }

        private static int ToAnsiIndex(int cga)
            => (cga & 0x2) | ((cga & 0x1) << 2) | ((cga & 0x4) >> 2);

        private void AppendRune(int rune)
        {
            if (rune < 32)
            {
                _out.Append(' ');
            }
            else if (rune <= 0xFFFF)
            {
                _out.Append((char)rune);
            }
            else
            {
                // Past the basic plane: the surrogate pair, written directly.
                uint value = (uint)rune - 0x10000u;
                _out.Append((char)(0xD800 + (value >> 10)));
                _out.Append((char)(0xDC00 + (value & 0x3FF)));
            }

            _lastCol++;
            if (_lastCol >= _cols) { _lastCol = -1; _lastRow = -1; }
        }

        /// <summary>
        /// Digits, without formatting a string to get them. Two numbers ride in
        /// every cursor move, and a repaint emits thousands of moves.
        /// </summary>
        private void AppendNumber(int value)
        {
            if (value >= 100) _out.Append((char)('0' + (value / 100) % 10));
            if (value >= 10) _out.Append((char)('0' + (value / 10) % 10));
            _out.Append((char)('0' + value % 10));
        }

        private void Write(string text) => _out.Append(text);

        private void Flush()
        {
            if (_out.Length == 0) return;

            Console.Write(_out.ToString());
            _out.Clear();
        }

        /// <summary>
        /// Called by the main loop when the kernel reports a different size.
        /// </summary>
        internal void OnResized(int cols, int rows)
        {
            if (cols == _cols && rows == _rows) return;

            _cols = cols;
            _rows = rows;
            ResizeScreen();
            UpdateOffScreen();
            _terminalResized?.Invoke();
        }
    }

    /// <summary>
    /// A clipboard that lives in memory.
    /// </summary>
    /// <remarks>
    /// Upstream reaches the host clipboard by running xclip or pbcopy. There is
    /// no process to run and no clipboard to reach, so copy and paste work
    /// within the application and stop at its edge — which is the whole truth
    /// about what this does.
    /// </remarks>
    internal class MemoryClipboard : ClipboardBase
    {
        private string _contents = "";

        public override bool IsSupported => true;

        protected override string GetClipboardDataImpl() => _contents;

        protected override void SetClipboardDataImpl(string text) => _contents = text ?? "";
    }
}
