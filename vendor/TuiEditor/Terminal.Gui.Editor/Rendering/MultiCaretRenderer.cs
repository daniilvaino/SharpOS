using System.Drawing;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Terminal.Gui.Editor.Rendering;

/// <summary>
///     Renders additional (non-primary) caret positions as blinking, reverse-video cells.
///     Installed automatically by <see cref="Editor" /> when multi-caret mode is active.
/// </summary>
public sealed class MultiCaretRenderer : IOverlayRenderer
{
    private readonly Editor _editor;

    /// <summary>Initializes a new <see cref="MultiCaretRenderer" /> bound to the given editor.</summary>
    public MultiCaretRenderer (Editor editor)
    {
        _editor = editor;
    }

    /// <inheritdoc />
    public void Draw (View host, CellVisualLine line, int row, Rectangle viewport)
    {
        if (!_editor.HasMultipleCarets || _editor.Document is null)
        {
            return;
        }

        // Use the visual line's element range to scope correctly in word-wrap mode,
        // where a CellVisualLine represents only one wrapped segment of a DocumentLine.
        var hasElements = line.Elements.Count > 0;
        var segStart = hasElements ? line.Elements[0].DocumentOffset : line.DocumentLine.Offset;
        var segEnd = hasElements ? line.Elements[^1].DocumentEndOffset : line.DocumentLine.EndOffset;

        Attribute normal = host.GetAttributeForRole (VisualRole.Normal);

        // Use reverse-video + blink to distinguish additional carets. Underline rendered poorly
        // and inconsistently across terminals; the reverse (foreground/background swap) is far
        // more legible and reliably supported.
        Attribute caretAttr = new (normal.Foreground, normal.Background, TextStyle.Blink | TextStyle.Reverse);

        foreach (var offset in _editor.AdditionalCaretOffsets)
        {
            if (!IsOffsetInSegment (offset, segStart, segEnd, line.DocumentLine.EndOffset))
            {
                continue;
            }

            var colInLine = offset - line.DocumentLine.Offset;
            var visualCol = line.GetVisualColumn (colInLine);
            var col = visualCol - viewport.X;

            if (col < 0 || col >= viewport.Width)
            {
                continue;
            }

            host.SetAttribute (caretAttr);
            host.Move (col, row);
            host.AddRune (offset < segEnd ? GetRuneAt (offset) : new Rune (' '));
        }

        // Restore the normal attribute so subsequent drawing doesn't inherit the caret style.
        host.SetAttribute (normal);
    }

    /// <summary>
    ///     Determines whether <paramref name="offset" /> falls within the given segment range.
    ///     Allows <c>offset == segEnd</c> only when the segment ends at the true line end
    ///     (not a word-wrap boundary), so carets at end-of-line are visible without duplicates.
    /// </summary>
    internal static bool IsOffsetInSegment (int offset, int segStart, int segEnd, int lineEndOffset)
    {
        if (offset < segStart || offset > segEnd)
        {
            return false;
        }

        // At the segment boundary: allow only if this is the true end-of-line, not a wrap break.
        if (offset == segEnd && segEnd != lineEndOffset)
        {
            return false;
        }

        return true;
    }

    private Rune GetRuneAt (int offset)
    {
        var ch = _editor.Document!.GetCharAt (offset);

        if (char.IsHighSurrogate (ch)
            && offset + 1 < _editor.Document.TextLength)
        {
            var lo = _editor.Document.GetCharAt (offset + 1);

            return char.IsLowSurrogate (lo)
                ? new Rune (char.ConvertToUtf32 (ch, lo))
                : new Rune (' ');
        }

        return char.IsSurrogate (ch) ? new Rune (' ') : new Rune (ch);
    }
}
