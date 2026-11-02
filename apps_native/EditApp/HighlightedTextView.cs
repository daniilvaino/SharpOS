// TextView with syntax highlighting: tui-cs/Editor's engine (vendor/TuiEditor,
// AvaloniaEdit's) over TextView's lines, the definition chosen by the file's
// extension.
//
// The engine colours one line at a time from the state its previous line left
// (inside a comment, a string, ...). That state is kept per line with the text
// it was scanned from: a redraw rescans only from the first line whose text
// changed, and colours only the rows on the screen.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Terminal.Gui;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Editor.Highlighting;
using Attribute = Terminal.Gui.Attribute;
using Rune = System.Rune;
using SpanStack = System.Collections.Immutable.ImmutableStack<Terminal.Gui.Editor.Highlighting.HighlightingSpan>;

namespace EditApp
{
    internal sealed class HighlightedTextView : TextView
    {
        private HighlightingEngine _engine;

        // _starts[i]: the state at the start of line i; _scanned[i]: the text of
        // line i that _starts[i + 1] was scanned from.
        private readonly List<SpanStack> _starts = new List<SpanStack>();
        private readonly List<string> _scanned = new List<string>();

        // This redraw's rows: the model's line and a colour per rune.
        private readonly List<List<Rune>> _rows = new List<List<Rune>>();
        private readonly List<Attribute[]> _colors = new List<Attribute[]>();
        private int _row;

        // The definition's name ("C#", "Json"), or null for plain text.
        public string Language { get; private set; }

        // Set when the engine failed on this text; highlighting is then off.
        public string Failure { get; private set; }

        public void SetLanguage(string path)
        {
            IHighlightingDefinition definition = null;
            Failure = null;
            try
            {
                string extension = path == null ? "" : Path.GetExtension(path);
                if (extension.Length > 0) definition = HighlightingManager.Instance.GetDefinitionByExtension(extension);
            }
            catch (Exception ex)
            {
                Failure = ex.Message;
            }
            _engine = definition == null ? null : new HighlightingEngine(definition.MainRuleSet);
            Language = definition?.Name;
            _starts.Clear();
            _scanned.Clear();
            SetNeedsDisplay();
        }

        public override void Redraw(Rect bounds)
        {
            _rows.Clear();
            _colors.Clear();
            _row = 0;
            if (_engine != null)
            {
                try
                {
                    Highlight(TopRow, Math.Min(Lines, TopRow + Frame.Height));
                }
                catch (Exception ex)
                {
                    Failure = ex.Message;
                    _engine = null;
                    Language = null;
                    _rows.Clear();
                    _colors.Clear();
                }
            }
            base.Redraw(bounds);
        }

        protected override void SetNormalColor(List<Rune> line, int idx)
        {
            // Rows are drawn in order; a line without text never asks.
            while (_row < _rows.Count && !ReferenceEquals(_rows[_row], line)) _row++;
            if (_row < _rows.Count && idx < _colors[_row].Length) Driver.SetAttribute(_colors[_row][idx]);
            else base.SetNormalColor(line, idx);
        }

        private void Highlight(int first, int end)
        {
            if (_starts.Count == 0) _starts.Add(SpanStack.Empty);

            // A line's start state holds while every line above is unchanged.
            int known = 0;
            while (known < _scanned.Count && known < end && Same(GetLine(known), _scanned[known])) known++;
            _scanned.RemoveRange(known, _scanned.Count - known);
            _starts.RemoveRange(known + 1, _starts.Count - known - 1);

            for (int i = known; i < end; i++)
            {
                List<Rune> line = GetLine(i);
                string text = AsString(line, out int[] at);
                var document = new LineDocument(text);
                _engine.CurrentSpanStack = _starts[i];
                if (i < first)
                {
                    _engine.ScanLine(document, document);
                }
                else
                {
                    HighlightedLine highlighted = _engine.HighlightLine(document, document);
                    _rows.Add(line);
                    _colors.Add(Colors(highlighted, line.Count, at));
                }
                _scanned.Add(text);
                _starts.Add(_engine.CurrentSpanStack);
            }

            // Visible rows above the first changed line: their states are known.
            for (int i = first; i < Math.Min(known, end); i++)
            {
                List<Rune> line = GetLine(i);
                string text = AsString(line, out int[] at);
                var document = new LineDocument(text);
                _engine.CurrentSpanStack = _starts[i];
                _rows.Insert(i - first, line);
                _colors.Insert(i - first, Colors(_engine.HighlightLine(document, document), line.Count, at));
            }
        }

        private static bool Same(List<Rune> line, string text)
        {
            int c = 0;
            for (int i = 0; i < line.Count; i++)
            {
                uint v = line[i].Value;
                if (v < 0x10000)
                {
                    if (c >= text.Length || text[c] != (char)v) return false;
                    c++;
                }
                else
                {
                    if (c + 1 >= text.Length || char.ConvertToUtf32(text[c], text[c + 1]) != (int)v) return false;
                    c += 2;
                }
            }
            return c == text.Length;
        }

        // The line as a string, and the string offset of each rune.
        private static string AsString(List<Rune> line, out int[] at)
        {
            var text = new StringBuilder(line.Count);
            at = new int[line.Count];
            for (int i = 0; i < line.Count; i++)
            {
                at[i] = text.Length;
                text.Append(line[i].ToString());
            }
            return text.ToString();
        }

        private Attribute[] Colors(HighlightedLine highlighted, int count, int[] at)
        {
            Attribute normal = GetNormalColor();
            var colors = new Attribute[count];
            for (int i = 0; i < count; i++) colors[i] = normal;

            // Outer sections come before the ones inside them: later wins.
            foreach (HighlightedSection section in highlighted.Sections)
            {
                Color? foreground = ToConsole(section.Color?.Foreground?.Color);
                if (foreground == null) continue;
                var attribute = new Attribute(foreground.Value, normal.Background);
                int end = section.Offset + section.Length;
                for (int i = 0; i < count; i++)
                    if (at[i] >= section.Offset && at[i] < end) colors[i] = attribute;
            }
            return colors;
        }

        // The nearest of the console's sixteen (CGA values, in the order of
        // Terminal.Gui's Color). The definitions are made for a white page and
        // this one is black: dark colours go bright, black and grey stay text.
        private static Color? ToConsole(Terminal.Gui.Drawing.Color? color)
        {
            if (color == null) return null;
            ReadOnlySpan<uint> palette = stackalloc uint[]
            {
                0x000000, 0x0000AA, 0x00AA00, 0x00AAAA, 0xAA0000, 0xAA00AA, 0xAA5500, 0xAAAAAA,
                0x555555, 0x5555FF, 0x55FF55, 0x55FFFF, 0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF,
            };
            int best = 0;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < palette.Length; i++)
            {
                int r = color.Value.R - (int)((palette[i] >> 16) & 0xFF);
                int g = color.Value.G - (int)((palette[i] >> 8) & 0xFF);
                int b = color.Value.B - (int)(palette[i] & 0xFF);
                int distance = r * r + g * g + b * b;
                if (distance < bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }
            }

            switch ((Color)best)
            {
                case Color.Black:
                case Color.DarkGray:
                case Color.Gray:
                    return null;
                case Color.Blue: return Color.BrightBlue;
                case Color.Green: return Color.BrightGreen;
                case Color.Cyan: return Color.BrightCyan;
                case Color.Red: return Color.BrightRed;
                case Color.Magenta: return Color.BrightMagenta;
                case Color.Brown: return Color.BrightYellow;
                default: return (Color)best;
            }
        }

        // The engine asks a document for a line's text and offset, nothing else:
        // one line is the document and its only line.
        private sealed class LineDocument : StringTextSource, IDocument, IDocumentLine
        {
            public LineDocument(string text) : base(text) { }

            string IDocument.Text { get => Text; set => throw new NotSupportedException(); }

            public event EventHandler<TextChangeEventArgs> TextChanging { add { } remove { } }

            public event EventHandler<TextChangeEventArgs> TextChanged { add { } remove { } }

            public event EventHandler ChangeCompleted { add { } remove { } }

            public event EventHandler FileNameChanged { add { } remove { } }

            public int LineCount => 1;

            public string FileName => null;

            public IDocumentLine GetLineByNumber(int lineNumber) => lineNumber == 1 ? this : throw new ArgumentOutOfRangeException(nameof(lineNumber));

            public IDocumentLine GetLineByOffset(int offset) => this;

            public int GetOffset(int line, int column) => column - 1;

            public int GetOffset(TextLocation location) => location.Column - 1;

            public TextLocation GetLocation(int offset) => new TextLocation(1, offset + 1);

            public void Insert(int offset, string text) => throw new NotSupportedException();

            public void Insert(int offset, ITextSource text) => throw new NotSupportedException();

            public void Insert(int offset, string text, AnchorMovementType defaultAnchorMovementType) => throw new NotSupportedException();

            public void Insert(int offset, ITextSource text, AnchorMovementType defaultAnchorMovementType) => throw new NotSupportedException();

            public void Remove(int offset, int length) => throw new NotSupportedException();

            public void Replace(int offset, int length, string newText) => throw new NotSupportedException();

            public void Replace(int offset, int length, ITextSource newText) => throw new NotSupportedException();

            public void StartUndoableAction() { }

            public void EndUndoableAction() { }

            public IDisposable OpenUndoGroup() => throw new NotSupportedException();

            public ITextAnchor CreateAnchor(int offset) => throw new NotSupportedException();

            public object GetService(Type serviceType) => null;

            public int Offset => 0;

            public int Length => TextLength;

            public int EndOffset => TextLength;

            public int TotalLength => TextLength;

            public int DelimiterLength => 0;

            public int LineNumber => 1;

            public IDocumentLine PreviousLine => null;

            public IDocumentLine NextLine => null;

            public bool IsDeleted => false;
        }
    }
}
