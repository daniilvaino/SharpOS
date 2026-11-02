using Terminal.Gui.Editor.Document;
using Terminal.Gui.Editor.Rendering;

namespace Terminal.Gui.Editor;

public partial class Editor
{
    private bool InsertTab ()
    {
        if (_document is null)
        {
            return false;
        }

        if (ReadOnly)
        {
            return true;
        }

        if (HasMultipleCarets)
        {
            return MultiCaretInsertTab ();
        }

        if (HasSelection && SelectionSpansMultipleLines ())
        {
            IndentSelectedLines ();

            return true;
        }

        var text = GetTabInsertionText (HasSelection ? SelectionStart : CaretOffset);

        if (HasSelection)
        {
            ReplaceSelection (text);
        }
        else
        {
            _document.Insert (CaretOffset, text);
        }

        return true;
    }

    private bool Unindent ()
    {
        if (_document is null)
        {
            return false;
        }

        if (ReadOnly)
        {
            AnsiInputProcessorState.ClearPendingPrintableSuppression (App);

            return true;
        }

        if (HasMultipleCarets)
        {
            var handled = MultiCaretUnindent ();

            if (handled)
            {
                AnsiInputProcessorState.ClearPendingPrintableSuppression (App);
            }

            return handled;
        }

        List<DocumentLine> lines = HasSelection && SelectionSpansMultipleLines ()
            ? GetSelectedLines ()
            : [_document.GetLineByOffset (CaretOffset)];

        List<(int offset, int length)> removals = [];

        foreach (DocumentLine line in lines)
        {
            ISegment segment = TextUtilities.GetSingleIndentationSegment (_document, line.Offset, IndentationSize);

            if (segment.Length > 0)
            {
                removals.Add ((segment.Offset, segment.Length));
            }
        }

        if (removals.Count == 0)
        {
            AnsiInputProcessorState.ClearPendingPrintableSuppression (App);

            return true;
        }

        var hadSelection = HasSelection;
        var selectionWasForward = SelectionAnchorOffset <= CaretOffset;
        var selectionStart = SelectionStart;
        var selectionEnd = SelectionEnd;

        using (_document.RunUpdate ())
        {
            foreach ((int offset, int length) removal in removals.OrderByDescending (static r => r.offset))
            {
                _document.Remove (removal.offset, removal.length);
            }
        }

        if (hadSelection)
        {
            SetSelectionRangePreservingDirection (
                selectionWasForward,
                AdjustOffsetAfterRemovals (selectionStart, removals),
                AdjustOffsetAfterRemovals (selectionEnd, removals));
        }

        AnsiInputProcessorState.ClearPendingPrintableSuppression (App);

        return true;
    }

    private void IndentSelectedLines ()
    {
        List<DocumentLine> lines = GetSelectedLines ();

        if (lines.Count == 0)
        {
            return;
        }

        var indentText = GetIndentText ();
        var selectionWasForward = SelectionAnchorOffset <= CaretOffset;
        var selectionStart = SelectionStart;
        var selectionEnd = SelectionEnd;

        using (_document!.RunUpdate ())
        {
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                _document.Insert (lines[i].Offset, indentText);
            }
        }

        SetSelectionRangePreservingDirection (
            selectionWasForward,
            selectionStart + indentText.Length,
            selectionEnd + indentText.Length * lines.Count);
    }

    private bool TryDeleteIndentationLeft ()
    {
        return TryDeleteIndentationLeftAt (CaretOffset);
    }

    /// <summary>
    ///     Attempts smart-indentation backspace at the given <paramref name="offset" />. If the offset
    ///     sits exactly at the end of leading whitespace and aligns to an indentation boundary, the last
    ///     complete indentation unit is removed. Returns <see langword="true" /> if handled.
    /// </summary>
    private bool TryDeleteIndentationLeftAt (int offset)
    {
        if (!TryGetIndentationRemovalAt (offset, out (int offset, int length) removal))
        {
            return false;
        }

        _document!.Remove (removal.offset, removal.length);

        return true;
    }

    private bool TryGetIndentationRemovalAt (int offset, out (int offset, int length) removal)
    {
        removal = default;

        if (_document is null || offset == 0)
        {
            return false;
        }

        DocumentLine line = _document.GetLineByOffset (offset);
        ISegment leadingWhitespace = TextUtilities.GetLeadingWhitespace (_document, line);

        if (leadingWhitespace.Length == 0 || offset != leadingWhitespace.EndOffset)
        {
            return false;
        }

        var scanOffset = line.Offset;
        (int offset, int length) lastSegment = (0, 0);

        while (scanOffset < leadingWhitespace.EndOffset)
        {
            ISegment segment = TextUtilities.GetSingleIndentationSegment (_document, scanOffset, IndentationSize);

            if (segment.Length == 0 || scanOffset + segment.Length > offset)
            {
                break;
            }

            lastSegment = (segment.Offset, segment.Length);
            scanOffset += segment.Length;
        }

        if (scanOffset != offset || lastSegment.length == 0)
        {
            return false;
        }

        removal = lastSegment;

        return true;
    }

    private bool SelectionSpansMultipleLines ()
    {
        if (_document is null || !HasSelection)
        {
            return false;
        }

        DocumentLine firstLine = _document.GetLineByOffset (SelectionStart);
        var endOffset = Math.Max (SelectionStart, SelectionEnd - 1);
        DocumentLine lastLine = _document.GetLineByOffset (endOffset);

        return firstLine.LineNumber != lastLine.LineNumber;
    }

    private List<DocumentLine> GetSelectedLines ()
    {
        List<DocumentLine> lines = [];

        if (_document is null || !HasSelection)
        {
            return lines;
        }

        DocumentLine firstLine = _document.GetLineByOffset (SelectionStart);
        var endOffset = Math.Max (SelectionStart, SelectionEnd - 1);
        DocumentLine lastLine = _document.GetLineByOffset (endOffset);

        for (var lineNumber = firstLine.LineNumber; lineNumber <= lastLine.LineNumber; lineNumber++)
        {
            lines.Add (_document.GetLineByNumber (lineNumber));
        }

        return lines;
    }

    private string GetTabInsertionText (int offset)
    {
        if (!ConvertTabsToSpaces)
        {
            return "\t";
        }

        DocumentLine line = _document!.GetLineByOffset (offset);
        var visualColumn = GetOrBuildDefaultVisualLine (line).GetVisualColumn (offset - line.Offset);
        var spaces = VisualLineBuilder.GetTabExpansionWidth (visualColumn, IndentationSize);

        return new string (' ', spaces);
    }

    private string GetIndentText ()
    {
        return ConvertTabsToSpaces ? new string (' ', IndentationSize) : "\t";
    }

    private void SetSelectionRangePreservingDirection (bool forward, int start, int end)
    {
        _selectionAnchor = CreateSelectionAnchor (forward ? start : end);
        SetCaretOffset (forward ? end : start, true);
        RefreshSelectionAnchorMovement ();
        SelectionChanged?.Invoke (this, EventArgs.Empty);
        SetNeedsDraw ();
    }

    private static int AdjustOffsetAfterRemovals (int offset, IReadOnlyList<(int offset, int length)> removals)
    {
        var adjusted = offset;

        foreach (var (removeOffset, removeLength) in removals.OrderBy (static r => r.offset))
        {
            if (removeOffset + removeLength <= adjusted)
            {
                adjusted -= removeLength;

                continue;
            }

            if (removeOffset < adjusted)
            {
                adjusted = removeOffset;
            }
        }

        return adjusted;
    }
}
