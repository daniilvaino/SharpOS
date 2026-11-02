using System.Drawing;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Terminal.Gui.Editor;

/// <summary>
///     Renders line numbers and handles line-selection mouse interactions.
/// </summary>
internal sealed class LineNumberGutter : View
{
    private readonly Editor _editor;
    private int? _selectionAnchorLineNumber;

    internal LineNumberGutter (Editor editor)
    {
        _editor = editor;
        CanFocus = false;

        MouseBindings.Add (MouseFlags.WheeledUp, Command.ScrollUp);
        MouseBindings.Add (MouseFlags.WheeledDown, Command.ScrollDown);
    }

    /// <inheritdoc />
    protected override bool OnDrawingContent (DrawContext? context)
    {
        TextDocument? document = _editor.Document;

        if (document is null)
        {
            return true;
        }

        Rectangle viewport = Viewport;

        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return true;
        }

        var visibleHeight = _editor.Viewport.Height;
        var blank = new string (' ', viewport.Width);
        var totalVisualRows = _editor.GetTotalVisualRows ();

        for (var row = 0; row < viewport.Height; row++)
        {
            var visibleIndex = _editor.Viewport.Y + row;

            if (row >= visibleHeight || visibleIndex < 0 || visibleIndex >= totalVisualRows)
            {
                Move (0, row);
                AddStr (blank);

                continue;
            }

            Move (0, row);

            // Use segment metadata to detect continuation rows — prevLineNumber comparison
            // fails when the viewport is scrolled so that row 0 is already a continuation.
            if (_editor.IsViewRowFirstWrapSegment (row))
            {
                var lineNumber = _editor.ViewRowToLineNumber (row);
                var lineText = lineNumber.ToString ().PadLeft (viewport.Width - 1) + " ";
                AddStr (lineText);
            }
            else
            {
                AddStr (blank);
            }
        }

        return true;
    }

    /// <inheritdoc />
    protected override bool OnMouseEvent (Mouse mouse)
    {
        if (mouse.Position is not { } pos)
        {
            return false;
        }

        if (mouse.Flags.HasFlag (MouseFlags.LeftButtonPressed) && mouse.Flags.HasFlag (MouseFlags.PositionReport))
        {
            if (_selectionAnchorLineNumber is not { } anchor)
            {
                anchor = _editor.ViewRowToLineNumber (pos.Y);
                _selectionAnchorLineNumber = anchor;
            }

            _editor.SelectLines (anchor, _editor.ViewRowToLineNumber (pos.Y));

            return true;
        }

        if (mouse.Flags.HasFlag (MouseFlags.LeftButtonPressed))
        {
            _selectionAnchorLineNumber = _editor.ViewRowToLineNumber (pos.Y);
            _editor.SelectLineAtViewRow (pos.Y);
            App?.Mouse.GrabMouse (this);

            return true;
        }

        if (!mouse.Flags.HasFlag (MouseFlags.LeftButtonReleased))
        {
            return false;
        }

        _selectionAnchorLineNumber = null;
        App?.Mouse.UngrabMouse ();

        return true;
    }
}
