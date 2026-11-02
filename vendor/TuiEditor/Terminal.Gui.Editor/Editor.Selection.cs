using Terminal.Gui.Editor.Document;
using Terminal.Gui.Input;

namespace Terminal.Gui.Editor;

/// <summary>
///     Selection state and operations. Internal model is two anchors: <c>_selectionAnchor</c> is
///     where Shift was first held; the caret anchor is the live end. Selection is the segment
///     between the two; when they coincide (or the selection anchor is null), there is no selection.
/// </summary>
public partial class Editor
{
    private TextAnchor? _selectionAnchor;

    /// <summary>True when there is a non-empty selection.</summary>
    public bool HasSelection => _selectionAnchor is { IsDeleted: false } a && a.Offset != CaretOffset;

    /// <summary>Start offset of the selection (inclusive). Equals <see cref="CaretOffset" /> when no selection.</summary>
    public int SelectionStart => HasSelection ? Math.Min (SelectionAnchorOffset, CaretOffset) : CaretOffset;

    /// <summary>End offset of the selection (exclusive). Equals <see cref="CaretOffset" /> when no selection.</summary>
    public int SelectionEnd => HasSelection ? Math.Max (SelectionAnchorOffset, CaretOffset) : CaretOffset;

    /// <summary>Length of the selection in characters; 0 when no selection.</summary>
    public int SelectionLength => SelectionEnd - SelectionStart;

    /// <summary>The selection as a <see cref="TextSegment" />, or <see langword="null" /> if no selection.</summary>
    public TextSegment? Selection =>
        !HasSelection ? null : new TextSegment { StartOffset = SelectionStart, Length = SelectionLength };

    /// <summary>The selected document text, or an empty string when there is no selection.</summary>
    public string SelectedText => HasSelection ? _document!.GetText (SelectionStart, SelectionLength) : string.Empty;

    private int SelectionAnchorOffset => _selectionAnchor is { IsDeleted: false } anchor ? anchor.Offset : CaretOffset;

    /// <summary>Raised whenever the selection range changes (created, extended, cleared).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Clears the selection without moving the caret.</summary>
    public void ClearSelection ()
    {
        if (_selectionAnchor is null)
        {
            return;
        }

        (int start, int end) before = SelectionTuple ();
        _selectionAnchor = null;
        RaiseSelectionChangedIfMoved (before);
        SetNeedsDraw ();
    }

    /// <summary>Selects the entire document. Caret moves to <c>TextLength</c>.</summary>
    public void SelectAll ()
    {
        SelectRange (0, _document!.TextLength);
    }

    /// <summary>Selects a document range and moves the caret to the range end.</summary>
    public void SelectRange (int startOffset, int length)
    {
        if (_document is null)
        {
            return;
        }

        var start = Math.Clamp (startOffset, 0, _document.TextLength);
        var end = (int)Math.Clamp (start + Math.Max (0L, length), 0L, _document.TextLength);

        (int start, int end) before = SelectionTuple ();
        _selectionAnchor = start == end ? null : CreateSelectionAnchor (start);
        CaretOffset = end;
        RefreshSelectionAnchorMovement ();
        RaiseSelectionChangedIfMoved (before);
        SetNeedsDraw ();
    }

    /// <summary>
    ///     Replaces the current selection with <paramref name="replacement" />. If there is no selection, this is a no-op.
    ///     The caret lands at <c>SelectionStart + replacement.Length</c>.
    /// </summary>
    public void ReplaceSelection (string replacement)
    {
        if (ReadOnly || !HasSelection)
        {
            return;
        }

        var start = SelectionStart;
        var len = SelectionLength;

        (int start, int end) before = SelectionTuple ();
        _selectionAnchor = null;
        _document!.Replace (start, len, replacement);
        SetCaretOffset (start + replacement.Length, true);
        RaiseSelectionChangedIfMoved (before);
    }

    /// <summary>
    ///     Begins (if needed) or extends the selection by moving the caret one grapheme cluster
    ///     in the direction indicated by <paramref name="delta" /> (positive = forward, negative = backward).
    /// </summary>
    private void ExtendCaretBy (int delta)
    {
        var graphemeDelta = delta > 0
            ? GetGraphemeLengthForward (CaretOffset)
            : GetGraphemeLengthBackward (CaretOffset);

        // If graphemeDelta is 0, we're at a line boundary — cross the full line delimiter (CRLF counts as 2).
        var step = graphemeDelta > 0 ? graphemeDelta : GetDelimiterStep (CaretOffset, delta);
        var newOffset = delta > 0 ? CaretOffset + step : CaretOffset - step;
        ExtendCaretTo (SnapOffsetPastDelimiter (newOffset, delta));
    }

    private void ExtendCaretVertically (int delta)
    {
        (int start, int end) before = SelectionTupleWithEnsuredAnchor ();
        MoveCaretVertically (delta);
        RaiseSelectionChangedIfMoved (before);
        SetNeedsDraw ();
    }

    private void ExtendCaretTo (int newCaret)
    {
        (int start, int end) before = SelectionTupleWithEnsuredAnchor ();
        CaretOffset = newCaret;
        RaiseSelectionChangedIfMoved (before);
        SetNeedsDraw ();
    }

    private void EnsureSelectionAnchor ()
    {
        _selectionAnchor ??= CreateSelectionAnchor (CaretOffset);
        RefreshSelectionAnchorMovement ();
    }

    /// <summary>
    ///     Returns the current effective selection range as a (start, end) tuple. Callers compare a
    ///     before-snapshot to the post-mutation tuple via <see cref="RaiseSelectionChangedIfMoved" /> to
    ///     suppress no-op <see cref="SelectionChanged" /> firings.
    /// </summary>
    private (int start, int end) SelectionTuple ()
    {
        return (SelectionStart, SelectionEnd);
    }

    /// <summary>
    ///     Snapshot variant that primes the selection anchor first — used by extend-style operations
    ///     where setting the anchor is part of the operation but should not by itself count as a
    ///     selection-range change.
    /// </summary>
    private (int start, int end) SelectionTupleWithEnsuredAnchor ()
    {
        EnsureSelectionAnchor ();

        return SelectionTuple ();
    }

    private void RaiseSelectionChangedIfMoved ((int start, int end) before)
    {
        if (before == SelectionTuple ())
        {
            return;
        }

        SelectionChanged?.Invoke (this, EventArgs.Empty);
    }

    /// <summary>
    ///     Movement helper that respects an existing selection: plain (non-extending) cursor keys clear the selection
    ///     and snap to the appropriate end; otherwise the caret moves one grapheme cluster in the direction
    ///     indicated by <paramref name="delta" />.
    /// </summary>
    private void MoveCaretByCollapsingSelection (int delta)
    {
        if (HasSelection)
        {
            var target = delta < 0 ? SelectionStart : SelectionEnd;
            ClearSelection ();
            CaretOffset = SnapOffsetPastDelimiter (target, delta);

            return;
        }

        var graphemeDelta = delta > 0
            ? GetGraphemeLengthForward (CaretOffset)
            : GetGraphemeLengthBackward (CaretOffset);

        // If graphemeDelta is 0, we're at a line boundary — cross the full line delimiter (CRLF counts as 2).
        var step = graphemeDelta > 0 ? graphemeDelta : GetDelimiterStep (CaretOffset, delta);
        var newOffset = delta > 0 ? CaretOffset + step : CaretOffset - step;
        CaretOffset = SnapOffsetPastDelimiter (newOffset, delta);
    }

    private int GetDelimiterStep (int offset, int direction)
    {
        if (_document is null || direction == 0)
        {
            return 1;
        }

        offset = Math.Clamp (offset, 0, _document.TextLength);
        DocumentLine line = _document.GetLineByOffset (offset);

        if (direction > 0)
        {
            var delimiterStart = line.Offset + line.Length;
            var delimiterLen = line.DelimiterLength;

            if (delimiterLen == 0)
            {
                return 1;
            }

            // If already inside the delimiter (possible with older behavior), advance to the next line start.
            if (offset > delimiterStart && offset < delimiterStart + delimiterLen)
            {
                return delimiterStart + delimiterLen - offset;
            }

            return delimiterLen;
        }

        // direction < 0
        var lineStart = line.Offset;

        // If already inside this line's delimiter, step back to the end-of-line text.
        if (offset > lineStart + line.Length && offset <= lineStart + line.TotalLength)
        {
            return offset - (lineStart + line.Length);
        }

        DocumentLine? previous = line.PreviousLine;

        if (previous is null)
        {
            return 1;
        }

        return Math.Max (1, previous.DelimiterLength);
    }

    /// <summary>
    ///     In single-line flat mode, prevents the caret from landing inside a multi-char delimiter
    ///     (e.g. CRLF). If <paramref name="offset" /> is inside a delimiter, snaps forward (to the
    ///     next line start) when <paramref name="direction" /> is zero or positive, or backward (to
    ///     the end of line text) when <paramref name="direction" /> is negative.
    /// </summary>
    private int SnapOffsetPastDelimiter (int offset, int direction)
    {
        if (Multiline || _document is null)
        {
            return offset;
        }

        offset = Math.Clamp (offset, 0, _document.TextLength);
        DocumentLine line = _document.GetLineByOffset (offset);
        var offsetInLine = offset - line.Offset;

        // The delimiter spans offsets [line.Length .. line.TotalLength-1]. If the caret is strictly
        // inside (past the first byte but before the next line), snap to an edge.
        if (offsetInLine > line.Length && offsetInLine < line.TotalLength)
        {
            return direction >= 0
                ? line.Offset + line.TotalLength
                : line.Offset + line.Length;
        }

        return offset;
    }

    /// <summary>
    ///     Vertical movement helper: clears any selection then runs the standard line-up/line-down with sticky-column.
    /// </summary>
    private void MoveCaretVerticallyCollapsingSelection (int delta)
    {
        ClearSelection ();
        MoveCaretVertically (delta);
    }

    private void SelectWordAtOffset (int offset)
    {
        if (_document is null)
        {
            return;
        }

        var originalOffset = Math.Clamp (offset, 0, _document.TextLength);
        var wordOffset = originalOffset;

        if (wordOffset == _document.TextLength || !IsIdentifierWordCharAt (wordOffset))
        {
            if (wordOffset == 0 || !IsIdentifierWordCharAt (wordOffset - 1))
            {
                ClearSelection ();
                CaretOffset = originalOffset;

                return;
            }

            wordOffset--;
        }

        var start = wordOffset;

        while (start > 0 && IsIdentifierWordCharAt (start - 1))
        {
            start--;
        }

        var end = wordOffset + 1;

        while (end < _document.TextLength && IsIdentifierWordCharAt (end))
        {
            end++;
        }

        SelectRange (start, end - start);
    }

    private void SelectLineAtOffset (int offset)
    {
        if (_document is null)
        {
            return;
        }

        DocumentLine line = _document.GetLineByOffset (Math.Clamp (offset, 0, _document.TextLength));
        SelectRange (line.Offset, line.TotalLength);
    }

    internal void SelectLineAtViewRow (int row)
    {
        if (_document is null || _document.LineCount == 0)
        {
            return;
        }

        var lineNumber = ViewRowToLineNumber (row);
        SelectLines (lineNumber, lineNumber);
    }

    internal void SelectLines (int anchorLineNumber, int activeLineNumber)
    {
        if (_document is null || _document.LineCount == 0)
        {
            return;
        }

        var startLineNumber = Math.Clamp (Math.Min (anchorLineNumber, activeLineNumber), 1, _document.LineCount);
        var endLineNumber = Math.Clamp (Math.Max (anchorLineNumber, activeLineNumber), 1, _document.LineCount);
        DocumentLine startLine = _document.GetLineByNumber (startLineNumber);
        DocumentLine endLine = _document.GetLineByNumber (endLineNumber);
        SelectRange (startLine.Offset, endLine.Offset + endLine.TotalLength - startLine.Offset);
    }

    internal int ViewRowToLineNumber (int row)
    {
        if (_document is null || _document.LineCount == 0)
        {
            return 1;
        }

        if (WordWrap)
        {
            List<WrapMapEntry> map = GetWrapMap ();
            var visibleIndex = Viewport.Y + row;

            if (visibleIndex < 0 || map.Count == 0)
            {
                return 1;
            }

            return visibleIndex >= map.Count ? map[^1].LineNumber : map[visibleIndex].LineNumber;
        }

        List<int> visibleLines = GetVisibleLineNumbers ();
        var idx = Viewport.Y + row;

        if (idx < 0 || visibleLines.Count == 0)
        {
            return 1;
        }

        return idx >= visibleLines.Count ? visibleLines[^1] : visibleLines[idx];
    }

    /// <summary>
    ///     Returns <see langword="true" /> when the given view row is the first wrap segment of its
    ///     document line (segment index 0), or when word wrap is disabled. The line-number gutter uses
    ///     this to show blank for continuation rows rather than repeating the line number.
    /// </summary>
    internal bool IsViewRowFirstWrapSegment (int row)
    {
        if (!WordWrap || _document is null || _document.LineCount == 0)
        {
            return true;
        }

        List<WrapMapEntry> map = GetWrapMap ();
        var visibleIndex = Viewport.Y + row;

        if (visibleIndex < 0 || visibleIndex >= map.Count || map.Count == 0)
        {
            return true;
        }

        return map[visibleIndex].SegmentIndex == 0;
    }

    /// <summary>
    ///     Returns the total number of visual rows. When word wrap is on this is the wrap map size;
    ///     otherwise it is the number of visible (non-folded) lines. Used by the gutter to detect
    ///     rows past the end of the document.
    /// </summary>
    internal int GetTotalVisualRows ()
    {
        if (_document is null || _document.LineCount == 0)
        {
            return 0;
        }

        return WordWrap ? GetWrapMap ().Count : GetVisibleLineNumbers ().Count;
    }

    /// <summary>
    ///     Moves the caret to the previous word start (backward) or the next word start (forward),
    ///     collapsing any existing selection. Used by <see cref="Command.WordLeft" /> and
    ///     <see cref="Command.WordRight" />.
    /// </summary>
    private void MoveCaretToWordBoundary (bool forward)
    {
        if (_document is null)
        {
            return;
        }

        if (HasSelection)
        {
            var collapseTarget = forward ? SelectionEnd : SelectionStart;
            ClearSelection ();
            CaretOffset = collapseTarget;
        }

        CaretOffset = GetWordBoundaryOffset (CaretOffset, forward);
    }

    /// <summary>
    ///     Deletes text between the nearest word boundary and the caret. On <c>forward = true</c>
    ///     removes from the caret to the next word start; on <c>false</c> removes from the previous
    ///     word start to the caret. Respects <see cref="ReadOnly" /> and replaces any active
    ///     selection first (matching <see cref="DeleteLeft" /> / <see cref="DeleteRight" /> behavior).
    /// </summary>
    private void KillToWordBoundary (bool forward)
    {
        if (ReadOnly || _document is null)
        {
            return;
        }

        if (HasSelection)
        {
            ReplaceSelection (string.Empty);

            return;
        }

        var boundary = GetWordBoundaryOffset (CaretOffset, forward);
        var start = Math.Min (CaretOffset, boundary);
        var length = Math.Abs (boundary - CaretOffset);

        if (length == 0)
        {
            return;
        }

        using (_document.RunUpdate ())
        {
            _document.Remove (start, length);
        }
    }

    /// <summary>
    ///     Returns the offset of the nearest word boundary from <paramref name="offset" /> in the
    ///     given direction. Uses <see cref="CaretPositioningMode.WordStartOrSymbol" /> which matches
    ///     the Ctrl+Left / Ctrl+Right semantics in AvaloniaEdit and Terminal.Gui's own TextView:
    ///     stops at word starts <em>and</em> between adjacent punctuation/symbol runs, giving
    ///     intuitive jumps across operators and brackets.
    /// </summary>
    private int GetWordBoundaryOffset (int offset, bool forward)
    {
        LogicalDirection direction = forward ? LogicalDirection.Forward : LogicalDirection.Backward;
        var next = TextUtilities.GetNextCaretPosition (_document!, offset, direction,
            CaretPositioningMode.WordStartOrSymbol);

        if (next < 0)
        {
            return forward ? _document!.TextLength : 0;
        }

        return next;
    }

    private bool IsIdentifierWordCharAt (int offset)
    {
        var ch = _document!.GetCharAt (offset);

        return char.IsLetterOrDigit (ch) || ch == '_';
    }

    private TextAnchor CreateSelectionAnchor (int offset)
    {
        TextAnchor anchor = _document!.CreateAnchor (offset);
        anchor.SurviveDeletion = true;

        return anchor;
    }

    private void RefreshSelectionAnchorMovement ()
    {
        if (_selectionAnchor is not { IsDeleted: false } anchor)
        {
            return;
        }

        anchor.MovementType = anchor.Offset <= CaretOffset
            ? AnchorMovementType.AfterInsertion
            : AnchorMovementType.BeforeInsertion;
    }
}
