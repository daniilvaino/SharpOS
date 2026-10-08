using System;
using SharpOS.AppSdk;

namespace Internal.ReadLine.Abstractions
{
    /// <summary>
    /// The line editor's view of the screen (vendor/ReadLine's IConsole), in
    /// place of the library's own adapter to System.Console's cursor.
    /// </summary>
    /// <remarks>
    /// The terminal cannot be asked where its cursor is, so this counts it:
    /// column and row relative to where the line began, the row 0 being the
    /// prompt's. Every move the editor asks for goes out as a relative escape
    /// (ESC [n A/B/C/D) from the counted position, which needs no absolute row
    /// — the screen scrolls under us. The count models the Windows console the
    /// library was written against: writing the last column moves to the next
    /// row at once. An xterm instead holds the cursor in the last column until
    /// the next character, so a write that ends exactly there is followed by a
    /// space and a carriage return, which puts the real cursor where the count
    /// says. The cell that space lands in is past the end of the text.
    /// </remarks>
    internal sealed class Console2 : IConsole
    {
        /// <summary>The column the next line starts in: the prompt's visible length. Set before each line.</summary>
        public static int Origin;

        private int _left;
        private int _top;
        private readonly int _width;
        private readonly int _height;

        public Console2()
        {
            if (!AppConsole.TryGetSize(out _width, out _height))
            {
                _width = 80;
                _height = 25;
            }
            _left = Origin % _width;
        }

        public int CursorLeft => _left;
        public int CursorTop => _top;
        public int BufferWidth => _width;
        public int BufferHeight => _height;

        // The library's ReadPassword sets it; the shell never does.
        public bool PasswordMode { get; set; }

        public void SetBufferSize(int width, int height) { }

        public void SetCursorPosition(int left, int top)
        {
            if (PasswordMode) return;
            Move(top - _top, 'B', 'A');
            Move(left - _left, 'C', 'D');
            _left = left;
            _top = top;
        }

        public void Write(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (PasswordMode) value = new string('*', value.Length);
            AppHost.WriteString(value);
            for (int i = 0; i < value.Length; i++)
            {
                if (++_left < _width) continue;
                _left = 0;
                _top++;
            }
            if (_left == 0) AppHost.WriteString(" \r");
        }

        public void WriteLine(string value)
        {
            Write(value);
            AppHost.WriteString("\n");
            _left = 0;
            _top++;
        }

        // ESC [n X, X by the sign of n; nothing for 0.
        private static void Move(int by, char forward, char back)
        {
            if (by == 0) return;
            AppHost.WriteString("\u001b[");
            AppHost.WriteString((by > 0 ? by : -by).ToString());
            AppHost.WriteChar(by > 0 ? forward : back);
        }
    }
}
