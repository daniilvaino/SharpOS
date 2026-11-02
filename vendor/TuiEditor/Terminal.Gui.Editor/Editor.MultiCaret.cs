using System.Drawing;
using System.Text;
using Terminal.Gui.Editor.Document;

namespace Terminal.Gui.Editor;

/// <summary>
///     Multi-caret state and operations. Additional carets are backed by <see cref="TextAnchor" />
///     instances (same as the primary caret) so they track document edits automatically. Each
///     additional caret carries its own selection anchor. Editing commands iterate all carets in
///     descending offset order inside a single <see cref="TextDocument.RunUpdate" /> scope so that
///     undo collapses the whole operation into one step.
/// </summary>
public partial class Editor
{
    private readonly List<CaretInfo> _additionalCarets = [];
    private int? _keyboardColumnSelectionAnchorOffset;
    private int _keyboardColumnSelectionActiveColumn;
    private int _keyboardColumnSelectionActiveRowDelta;
    private int _keyboardColumnSelectionAnchorColumn;
    private int _verticalCaretKeyboardDirection;

    /// <summary>Gets the offsets of all additional carets (excludes the primary).</summary>
    public IReadOnlyList<int> AdditionalCaretOffsets => _additionalCarets
        .Where (c => c.CaretAnchor is { IsDeleted: false })
        .Select (c => c.CaretAnchor!.Offset)
        .ToList ();

    /// <summary>True when there are additional carets beyond the primary.</summary>
    public bool HasMultipleCarets => _additionalCarets.Count > 0;

    /// <summary>
    ///     Adds another caret at the given <paramref name="offset" />, or removes the one
    ///     already there (toggle behavior for Ctrl+Click). The add path goes through
    ///     <see cref="AddAdditionalCaretAt" />; the toggle-off is an explicit, user-driven single
    ///     removal. The primary caret is never removed.
    /// </summary>
    public void ToggleCaretAt (int offset)
    {
        if (_document is null || !Multiline)
        {
            return;
        }

        _verticalCaretKeyboardDirection = 0;

        offset = Math.Clamp (offset, 0, _document.TextLength);

        if (offset == CaretOffset)
        {
            return;
        }

        for (var i = _additionalCarets.Count - 1; i >= 0; i--)
        {
            if (_additionalCarets[i].CaretAnchor is not { IsDeleted: false } anchor || anchor.Offset != offset)
            {
                continue;
            }

            _additionalCarets.RemoveAt (i);
            SetNeedsDraw ();

            return;
        }

        AddAdditionalCaretAt (offset);
    }

    private void AddAdditionalCaretAt (int offset, int? selectionAnchorOffset = null)
    {
        if (_document is null)
        {
            return;
        }

        offset = Math.Clamp (offset, 0, _document.TextLength);

        if (offset == CaretOffset)
        {
            return;
        }

        foreach (CaretInfo caret in _additionalCarets)
        {
            if (caret.CaretAnchor is { IsDeleted: false } anchor && anchor.Offset == offset)
            {
                return;
            }
        }

        TextAnchor? selectionAnchor = null;

        if (selectionAnchorOffset is { } anchorOffset)
        {
            anchorOffset = Math.Clamp (anchorOffset, 0, _document.TextLength);

            if (anchorOffset != offset)
            {
                selectionAnchor = CreateSelectionAnchor (anchorOffset);
                selectionAnchor.MovementType = anchorOffset <= offset
                    ? AnchorMovementType.AfterInsertion
                    : AnchorMovementType.BeforeInsertion;
            }
        }

        _additionalCarets.Add (
            new CaretInfo { CaretAnchor = CreateCaretAnchor (offset), SelectionAnchor = selectionAnchor });
        SetNeedsDraw ();
    }

    /// <summary>
    ///     Re-establishes the multi-caret invariant: drops any additional caret whose anchor was
    ///     deleted, any that coincides with the primary, and collapses duplicates at the same
    ///     offset. Called after every primary-caret move and every document change (before the
    ///     next edit applies) — the single invariant-trim path that mutates
    ///     <see cref="_additionalCarets" />.
    /// </summary>
    private void NormalizeAdditionalCarets ()
    {
        if (_additionalCarets.Count == 0)
        {
            _verticalCaretKeyboardDirection = 0;

            return;
        }

        HashSet<int> seen = [CaretOffset];
        var removed = false;

        for (var i = _additionalCarets.Count - 1; i >= 0; i--)
        {
            if (_additionalCarets[i].CaretAnchor is { IsDeleted: false } anchor && seen.Add (anchor.Offset))
            {
                continue;
            }

            _additionalCarets.RemoveAt (i);
            removed = true;
        }

        if (!removed)
        {
            return;
        }

        if (_additionalCarets.Count == 0)
        {
            _verticalCaretKeyboardDirection = 0;
        }

        SetNeedsDraw ();
    }

    /// <summary>
    ///     Extends the vertical caret block one line above (<paramref name="delta" /> &lt; 0) the
    ///     topmost caret or below (&gt; 0) the bottommost, landing on the sticky visual column.
    ///     Crossing the top/bottom document bound is a no-op. Bound to
    ///     <c>Ctrl+Alt+CursorUp</c> / <c>Ctrl+Alt+CursorDown</c> via the configurable
    ///     <see cref="DefaultKeyBindings" />.
    /// </summary>
    private bool? AddCaretVertically (int delta)
    {
        if (_document is null || !Multiline)
        {
            return true;
        }

        if (HasMultipleCarets
            && _verticalCaretKeyboardDirection != 0
            && delta != _verticalCaretKeyboardDirection
            && TryRemoveEdgeCaret (_verticalCaretKeyboardDirection))
        {
            if (!HasMultipleCarets)
            {
                _verticalCaretKeyboardDirection = 0;
            }

            return true;
        }

        // The sticky column is captured once, when the block is first created, from the primary
        // caret's column — then preserved across extensions (single-caret virtual-column
        // behavior, reused rather than re-derived).
        if (!HasMultipleCarets)
        {
            _virtualCaretColumn = GetVisualColumnForOffset (CaretOffset);
        }

        // Reference = the extreme caret in the requested direction: topmost for up, bottommost
        // for down. The block grows past that edge.
        var reference = AdditionalCaretOffsets.Aggregate (CaretOffset,
            (current, offset) => delta < 0 ? Math.Min (current, offset) : Math.Max (current, offset));

        if (!TryGetVerticalOffset (reference, delta, _virtualCaretColumn, out var target))
        {
            return true;
        }

        AddAdditionalCaretAt (target);
        _verticalCaretKeyboardDirection = delta;

        return true;
    }

    /// <summary>
    ///     Builds a column of carets from the <paramref name="anchorViewRow" /> (which hosts the
    ///     primary) through the <paramref name="activeViewRow" />. Used by the <c>Shift+Alt</c> column-drag. Rebuilt from
    ///     scratch on every drag event so the end state is identical to a single press at the
    ///     final position.
    /// </summary>
    private void SetVerticalCaretsFromViewRows (int anchorViewRow, int activeViewRow, int anchorViewColumn,
        int activeViewColumn)
    {
        if (_document is null || !Multiline)
        {
            return;
        }

        var primaryAnchorOffset = MousePositionToOffset (new Point (anchorViewColumn, anchorViewRow));
        var primaryOffset = MousePositionToOffset (new Point (activeViewColumn, anchorViewRow));
        _verticalCaretKeyboardDirection = 0;

        ClearSelection ();
        ClearAdditionalCarets ();

        SetPrimaryColumnSelection (primaryAnchorOffset, primaryOffset);

        var top = Math.Min (anchorViewRow, activeViewRow);
        var bottom = Math.Max (anchorViewRow, activeViewRow);

        for (var row = top; row <= bottom; row++)
        {
            if (row == anchorViewRow)
            {
                continue;
            }

            var rowAnchorOffset = MousePositionToOffset (new Point (anchorViewColumn, row));
            var rowActiveOffset = MousePositionToOffset (new Point (activeViewColumn, row));

            AddAdditionalCaretAt (rowActiveOffset, rowAnchorOffset);
        }

        SetNeedsDraw ();
    }

    private void ColumnSelectByKeyboard (int rowDelta, int columnDelta)
    {
        if (_document is null)
        {
            return;
        }

        if (_keyboardColumnSelectionAnchorOffset is null)
        {
            _keyboardColumnSelectionAnchorOffset = CaretOffset;
            _keyboardColumnSelectionAnchorColumn = GetVisualColumnForOffset (CaretOffset);
            _keyboardColumnSelectionActiveColumn = _keyboardColumnSelectionAnchorColumn;
            _keyboardColumnSelectionActiveRowDelta = 0;
        }

        var nextRowDelta = _keyboardColumnSelectionActiveRowDelta + rowDelta;
        var nextActiveColumn = Math.Max (0, _keyboardColumnSelectionActiveColumn + columnDelta);
        var anchorOffset = _keyboardColumnSelectionAnchorOffset.Value;

        if (!TryGetVerticalOffset (anchorOffset, nextRowDelta, nextActiveColumn, out _))
        {
            return;
        }

        _keyboardColumnSelectionActiveRowDelta = nextRowDelta;
        _keyboardColumnSelectionActiveColumn = nextActiveColumn;
        SetVerticalSelectionsFromAnchorOffset (
            anchorOffset,
            _keyboardColumnSelectionActiveRowDelta,
            _keyboardColumnSelectionAnchorColumn,
            _keyboardColumnSelectionActiveColumn);
        _keyboardColumnSelectionAnchorOffset = anchorOffset;
    }

    private void SetVerticalSelectionsFromAnchorOffset (
        int anchorOffset,
        int activeRowDelta,
        int anchorColumn,
        int activeColumn)
    {
        if (_document is null)
        {
            return;
        }

        _verticalCaretKeyboardDirection = 0;
        ClearSelection ();
        ClearAdditionalCarets ();

        if (!TryGetVerticalOffset (anchorOffset, 0, anchorColumn, out var primaryAnchorOffset)
            || !TryGetVerticalOffset (anchorOffset, 0, activeColumn, out var primaryActiveOffset))
        {
            return;
        }

        SetPrimaryColumnSelection (primaryAnchorOffset, primaryActiveOffset);

        var firstDelta = Math.Min (0, activeRowDelta);
        var lastDelta = Math.Max (0, activeRowDelta);

        for (var delta = firstDelta; delta <= lastDelta; delta++)
        {
            if (delta == 0)
            {
                continue;
            }

            if (TryGetVerticalOffset (anchorOffset, delta, anchorColumn, out var rowAnchorOffset)
                && TryGetVerticalOffset (anchorOffset, delta, activeColumn, out var rowActiveOffset))
            {
                AddAdditionalCaretAt (rowActiveOffset, rowAnchorOffset);
            }
        }

        SetNeedsDraw ();
    }

    private void SetPrimaryColumnSelection (int anchorOffset, int activeOffset)
    {
        CaretOffset = activeOffset;

        if (anchorOffset == activeOffset)
        {
            ClearSelection ();

            return;
        }

        _selectionAnchor = CreateSelectionAnchor (anchorOffset);
        RefreshSelectionAnchorMovement ();
        SelectionChanged?.Invoke (this, EventArgs.Empty);
        SetNeedsDraw ();
    }

    /// <summary>Removes all additional carets, leaving only the primary.</summary>
    public void ClearAdditionalCarets ()
    {
        var had = _additionalCarets.Count > 0;
        _verticalCaretKeyboardDirection = 0;
        _keyboardColumnSelectionAnchorOffset = null;

        if (had)
        {
            _additionalCarets.Clear ();
        }

        // Refresh the sticky virtual column to the primary's current column so vertical
        // navigation resumes freely from where the primary actually is — not wherever the
        // dismissed block left it. (specs/vertical-multi-caret: "Esc clears multi-caret and
        // refreshes sticky column".)
        if (_document is not null)
        {
            _virtualCaretColumn = GetCaretColumn ();
        }

        if (had)
        {
            SetNeedsDraw ();
        }
    }

    /// <summary>
    ///     Returns all caret offsets (primary + additional) sorted in descending order.
    ///     Used by editing commands to process from high to low offset so earlier edits don't
    ///     invalidate later positions.
    /// </summary>
    private List<CaretEditInfo> GetAllCaretsDescending ()
    {
        List<CaretEditInfo> result = [new () { Offset = CaretOffset, IsPrimary = true }];

        foreach (CaretInfo caret in _additionalCarets)
        {
            if (caret.CaretAnchor is { IsDeleted: false } anchor)
            {
                result.Add (new CaretEditInfo
                {
                    Offset = anchor.Offset,
                    IsPrimary = false,
                    SelectionAnchor = caret.SelectionAnchor
                });
            }
        }

        result.Sort ((a, b) => b.Offset.CompareTo (a.Offset));

        return result;
    }

    /// <summary>
    ///     Executes a multi-caret insert. Each caret has <paramref name="text" /> inserted at its
    ///     position. Wrapped in a single undo scope.
    /// </summary>
    private void MultiCaretInsert (string text)
    {
        if (ReadOnly || _document is null)
        {
            return;
        }

        if (!HasMultipleCarets)
        {
            InsertOrReplace (text);
            return;
        }

        using (_document.RunUpdate ())
        {
            List<CaretEditInfo> carets = GetAllCaretsDescending ();

            if (TryMultiCaretPlainInsertAsSingleChange (text, carets))
            {
                return;
            }

            foreach (CaretEditInfo caret in carets)
            {
                if (caret.IsPrimary)
                {
                    if (HasSelection)
                    {
                        ReplaceSelection (text);
                    }
                    else if (OverwriteMode)
                    {
                        OverwriteAtCaret (text);
                    }
                    else
                    {
                        _document.Insert (CaretOffset, text);
                    }
                }
                else
                {
                    if (caret.SelectionAnchor is { IsDeleted: false } selAnchor)
                    {
                        var selStart = Math.Min (selAnchor.Offset, caret.Offset);
                        var selEnd = Math.Max (selAnchor.Offset, caret.Offset);

                        if (selEnd > selStart)
                        {
                            _document.Replace (selStart, selEnd - selStart, text);

                            continue;
                        }
                    }

                    if (OverwriteMode)
                    {
                        OverwriteAtOffset (caret.Offset, text);
                    }
                    else
                    {
                        _document.Insert (caret.Offset, text);
                    }
                }
            }
        }

        // Clear per-caret selections after edit.
        // Editing collapses per-caret selections, matching single-caret behavior.
        ClearAdditionalCaretSelections ();
    }

    private bool TryMultiCaretPlainInsertAsSingleChange (string text, List<CaretEditInfo> carets)
    {
        if (_document is null || OverwriteMode || HasSelection)
        {
            return false;
        }

        foreach (CaretEditInfo caret in carets)
        {
            if (caret.SelectionAnchor is { IsDeleted: false } selAnchor && selAnchor.Offset != caret.Offset)
            {
                return false;
            }
        }

        carets.Sort ((a, b) => a.Offset.CompareTo (b.Offset));

        var firstOffset = carets[0].Offset;
        var lastOffset = carets[^1].Offset;
        var replaceLength = lastOffset - firstOffset;

        if (replaceLength > 200_000)
        {
            carets.Sort ((a, b) => b.Offset.CompareTo (a.Offset));

            return false;
        }

        var original = _document.GetText (firstOffset, replaceLength);
        StringBuilder replacement = new (original.Length + text.Length * carets.Count);
        OffsetChangeMap map = new ();
        var copied = 0;

        foreach (CaretEditInfo caret in carets)
        {
            var relativeOffset = caret.Offset - firstOffset;
            replacement.Append (original, copied, relativeOffset - copied);
            replacement.Append (text);
            copied = relativeOffset;
            map.Add (new OffsetChangeMapEntry (caret.Offset, 0, text.Length));
        }

        replacement.Append (original, copied, original.Length - copied);
        _document.Replace (firstOffset, replaceLength, replacement.ToString (), map);
        ClearAdditionalCaretSelections ();

        return true;
    }

    /// <summary>
    ///     Executes a multi-caret delete-left (backspace). Each caret deletes one indentation unit
    ///     (when at a leading-whitespace boundary) or one character to its left, matching single-caret
    ///     smart-backspace behavior. Wrapped in a single undo scope.
    /// </summary>
    private bool? MultiCaretDeleteLeft ()
    {
        if (ReadOnly || _document is null)
        {
            return true;
        }

        if (!HasMultipleCarets)
        {
            return DeleteLeft ();
        }

        using (_document.RunUpdate ())
        {
            List<CaretEditInfo> carets = GetAllCaretsDescending ();

            if (TryMultiCaretPlainDeleteLeftAsSingleChange (carets))
            {
                return true;
            }

            foreach (CaretEditInfo caret in carets)
            {
                if (caret.IsPrimary)
                {
                    if (HasSelection)
                    {
                        ReplaceSelection (string.Empty);
                    }
                    else if (!TryDeleteIndentationLeftAt (CaretOffset) && CaretOffset > 0)
                    {
                        _document.Remove (CaretOffset - 1, 1);
                    }
                }
                else
                {
                    if (caret.SelectionAnchor is { IsDeleted: false } selAnchor)
                    {
                        var selStart = Math.Min (selAnchor.Offset, caret.Offset);
                        var selEnd = Math.Max (selAnchor.Offset, caret.Offset);

                        if (selEnd > selStart)
                        {
                            _document.Replace (selStart, selEnd - selStart, string.Empty);

                            continue;
                        }
                    }

                    if (!TryDeleteIndentationLeftAt (caret.Offset) && caret.Offset > 0)
                    {
                        _document.Remove (caret.Offset - 1, 1);
                    }
                }
            }
        }

        ClearAdditionalCaretSelections ();

        return true;
    }

    private bool TryMultiCaretPlainDeleteLeftAsSingleChange (List<CaretEditInfo> carets)
    {
        if (_document is null || HasSelection)
        {
            return false;
        }

        foreach (CaretEditInfo caret in carets)
        {
            if (caret.Offset == 0
                || (caret.SelectionAnchor is { IsDeleted: false } selAnchor && selAnchor.Offset != caret.Offset)
                || TryGetIndentationRemovalAt (caret.Offset, out _))
            {
                return false;
            }

            var deletedChar = _document.GetCharAt (caret.Offset - 1);

            if (deletedChar is '\r' or '\n')
            {
                return false;
            }
        }

        carets.Sort ((a, b) => a.Offset.CompareTo (b.Offset));

        var firstOffset = carets[0].Offset - 1;
        var lastOffset = carets[^1].Offset;
        var replaceLength = lastOffset - firstOffset;

        if (replaceLength > 200_000)
        {
            carets.Sort ((a, b) => b.Offset.CompareTo (a.Offset));

            return false;
        }

        var original = _document.GetText (firstOffset, replaceLength);
        StringBuilder replacement = new (original.Length - carets.Count);
        List<OffsetChangeMapEntry> removals = new (carets.Count);
        var copied = 0;

        foreach (CaretEditInfo caret in carets)
        {
            var relativeRemovalOffset = caret.Offset - 1 - firstOffset;
            replacement.Append (original, copied, relativeRemovalOffset - copied);
            copied = relativeRemovalOffset + 1;
            removals.Add (new OffsetChangeMapEntry (caret.Offset - 1, 1, 0));
        }

        replacement.Append (original, copied, original.Length - copied);

        OffsetChangeMap map = new ();

        for (var i = removals.Count - 1; i >= 0; i--)
        {
            map.Add (removals[i]);
        }

        _document.Replace (firstOffset, replaceLength, replacement.ToString (), map);
        ClearAdditionalCaretSelections ();

        return true;
    }

    /// <summary>
    ///     Executes a multi-caret delete-right (delete key). Each caret deletes one character to its right.
    ///     Wrapped in a single undo scope.
    /// </summary>
    private bool? MultiCaretDeleteRight ()
    {
        if (ReadOnly || _document is null)
        {
            return true;
        }

        if (!HasMultipleCarets)
        {
            return DeleteRight ();
        }

        using (_document.RunUpdate ())
        {
            List<CaretEditInfo> carets = GetAllCaretsDescending ();

            foreach (CaretEditInfo caret in carets)
            {
                if (caret.IsPrimary)
                {
                    if (HasSelection)
                    {
                        ReplaceSelection (string.Empty);
                    }
                    else if (CaretOffset < _document.TextLength)
                    {
                        _document.Remove (CaretOffset, 1);
                    }
                }
                else
                {
                    if (caret.SelectionAnchor is { IsDeleted: false } selAnchor)
                    {
                        var selStart = Math.Min (selAnchor.Offset, caret.Offset);
                        var selEnd = Math.Max (selAnchor.Offset, caret.Offset);

                        if (selEnd > selStart)
                        {
                            _document.Replace (selStart, selEnd - selStart, string.Empty);

                            continue;
                        }
                    }

                    if (caret.Offset < _document.TextLength)
                    {
                        _document.Remove (caret.Offset, 1);
                    }
                }
            }
        }

        ClearAdditionalCaretSelections ();

        return true;
    }

    /// <summary>
    ///     Multi-caret newline insert. Each caret gets a newline followed by auto-indent
    ///     (when <see cref="IndentationStrategy" /> is set), matching single-caret Enter behavior.
    /// </summary>
    private bool? MultiCaretNewLine ()
    {
        if (ReadOnly || !Multiline || _document is null)
        {
            return true;
        }

        if (!HasMultipleCarets)
        {
            return InsertNewLineWithAutoIndent ();
        }

        using (_document.RunUpdate ())
        {
            List<CaretEditInfo> carets = GetAllCaretsDescending ();

            foreach (CaretEditInfo caret in carets)
            {
                if (caret.IsPrimary)
                {
                    if (HasSelection)
                    {
                        ReplaceSelection ("\n");
                    }
                    else
                    {
                        _document.Insert (CaretOffset, "\n");
                    }

                    if (IndentationStrategy is not { } strategy)
                    {
                        continue;
                    }

                    DocumentLine newLine = _document.GetLineByOffset (CaretOffset);
                    strategy.IndentLine (_document, newLine);
                }
                else
                {
                    if (caret.SelectionAnchor is { IsDeleted: false } selAnchor)
                    {
                        var selStart = Math.Min (selAnchor.Offset, caret.Offset);
                        var selEnd = Math.Max (selAnchor.Offset, caret.Offset);

                        if (selEnd > selStart)
                        {
                            _document.Replace (selStart, selEnd - selStart, "\n");

                            // Apply indentation to the new line after selection replacement.
                            if (IndentationStrategy is { } selStrategy)
                            {
                                DocumentLine newLine = _document.GetLineByOffset (selStart + 1);
                                selStrategy.IndentLine (_document, newLine);
                            }

                            continue;
                        }
                    }

                    _document.Insert (caret.Offset, "\n");

                    if (IndentationStrategy is { } caretStrategy)
                    {
                        DocumentLine newLine = _document.GetLineByOffset (caret.Offset + 1);
                        caretStrategy.IndentLine (_document, newLine);
                    }
                }
            }
        }

        ClearAdditionalCaretSelections ();

        return true;
    }

    /// <summary>
    ///     Resolves the selection range for a caret. The primary caret uses the editor's
    ///     selection; an additional caret uses its own anchor + offset. Returns
    ///     <see langword="false" /> (and a zero-width range at the caret) when there is no
    ///     selection.
    /// </summary>
    private bool TryGetCaretSelectionRange (CaretEditInfo caret, out int start, out int end)
    {
        if (caret.IsPrimary)
        {
            if (HasSelection)
            {
                start = SelectionStart;
                end = SelectionEnd;

                return end > start;
            }
        }
        else if (caret.SelectionAnchor is { IsDeleted: false } anchor)
        {
            start = Math.Min (anchor.Offset, caret.Offset);
            end = Math.Max (anchor.Offset, caret.Offset);

            return end > start;
        }

        start = end = caret.Offset;

        return false;
    }

    private bool RangeSpansMultipleLines (int start, int end)
    {
        DocumentLine first = _document!.GetLineByOffset (start);
        DocumentLine last = _document.GetLineByOffset (Math.Max (start, end - 1));

        return first.LineNumber != last.LineNumber;
    }

    private List<DocumentLine> LinesInRange (int start, int end)
    {
        DocumentLine first = _document!.GetLineByOffset (start);
        DocumentLine last = _document.GetLineByOffset (Math.Max (start, end - 1));
        List<DocumentLine> lines = [];

        for (var lineNumber = first.LineNumber; lineNumber <= last.LineNumber; lineNumber++)
        {
            lines.Add (_document.GetLineByNumber (lineNumber));
        }

        return lines;
    }

    /// <summary>
    ///     Tab at every caret, one undo scope. Per caret: any selection
    ///     <em>block-indents</em> every line it touches and preserves the selection (never
    ///     replace/delete it — that was the Codex P1 data-loss bug and the column-selection
    ///     follow-up); a caret with no selection gets a tab inserted at its own visual column via
    ///     <see cref="GetTabInsertionText" /> so the column stays aligned across repeated
    ///     presses. Block-indent lines are deduped; every edit is applied strictly
    ///     high-offset-first so an earlier edit doesn't shift a not-yet-applied offset. Caller
    ///     (<see cref="InsertTab" />) guarantees a non-null, writable document and
    ///     <see cref="HasMultipleCarets" />. See specs/vertical-multi-caret/spec.md
    ///     (<i>Tab with a multi-line selection plus an extra caret</i>).
    /// </summary>
    private bool MultiCaretInsertTab ()
    {
        HashSet<int> indentLineOffsets = [];
        List<(int offset, int length, string text)> edits = [];

        foreach (CaretEditInfo caret in GetAllCaretsDescending ())
        {
            if (TryGetCaretSelectionRange (caret, out var selStart, out var selEnd))
            {
                foreach (DocumentLine line in LinesInRange (selStart, selEnd))
                {
                    indentLineOffsets.Add (line.Offset);
                }

                continue;
            }

            edits.Add ((caret.Offset, 0, GetTabInsertionText (caret.Offset)));
        }

        var indentText = GetIndentText ();

        foreach (var lineOffset in indentLineOffsets)
        {
            edits.Add ((lineOffset, 0, indentText));
        }

        using (_document!.RunUpdate ())
        {
            foreach ((int offset, int length, string text) edit in edits.OrderByDescending (static e => e.offset))
            {
                if (edit.length > 0)
                {
                    _document.Replace (edit.offset, edit.length, edit.text);
                }
                else
                {
                    _document.Insert (edit.offset, edit.text);
                }
            }
        }

        return true;
    }

    /// <summary>
    ///     Shift+Tab at every caret, one undo scope. Per caret: a selection that spans multiple
    ///     lines block-unindents <em>every</em> line it touches (not just the caret's line — that
    ///     gap was the related Codex P2); any other caret unindents its own line. Lines are
    ///     deduped (two carets / overlapping selections unindent a line once) and removals run
    ///     high-offset-first so earlier removals don't shift later ones. Carets are anchors, so
    ///     they follow the removals automatically. Caller (<see cref="Unindent" />) guarantees a
    ///     non-null, writable document and <see cref="HasMultipleCarets" />.
    /// </summary>
    private bool MultiCaretUnindent ()
    {
        HashSet<int> seenLineOffsets = [];
        List<(int offset, int length)> removals = [];

        foreach (CaretEditInfo caret in GetAllCaretsDescending ())
        {
            List<DocumentLine> lines =
                TryGetCaretSelectionRange (caret, out var selStart, out var selEnd) &&
                RangeSpansMultipleLines (selStart, selEnd)
                    ? LinesInRange (selStart, selEnd)
                    : [_document!.GetLineByOffset (caret.Offset)];

            foreach (DocumentLine line in lines)
            {
                if (!seenLineOffsets.Add (line.Offset))
                {
                    continue;
                }

                ISegment segment = TextUtilities.GetSingleIndentationSegment (_document!, line.Offset, IndentationSize);

                if (segment.Length > 0)
                {
                    removals.Add ((segment.Offset, segment.Length));
                }
            }
        }

        if (removals.Count == 0)
        {
            return true;
        }

        using (_document!.RunUpdate ())
        {
            foreach ((int offset, int length) removal in removals.OrderByDescending (static r => r.offset))
            {
                _document.Remove (removal.offset, removal.length);
            }
        }

        return true;
    }

    internal bool HasAdditionalCaretSelections ()
    {
        foreach (CaretInfo caret in _additionalCarets)
        {
            if (caret is
                {
                    CaretAnchor: { IsDeleted: false } caretAnchor, SelectionAnchor: { IsDeleted: false } selectionAnchor
                }
                && caretAnchor.Offset != selectionAnchor.Offset)
            {
                return true;
            }
        }

        return false;
    }

    internal IReadOnlyList<(int start, int end)> AdditionalCaretSelectionRanges ()
    {
        List<(int start, int end)> ranges = [];

        foreach (CaretInfo caret in _additionalCarets)
        {
            if (caret.CaretAnchor is not { IsDeleted: false } caretAnchor
                || caret.SelectionAnchor is not { IsDeleted: false } selectionAnchor
                || caretAnchor.Offset == selectionAnchor.Offset)
            {
                continue;
            }

            ranges.Add ((Math.Min (caretAnchor.Offset, selectionAnchor.Offset),
                Math.Max (caretAnchor.Offset, selectionAnchor.Offset)));
        }

        return ranges;
    }

    private bool TryRemoveEdgeCaret (int direction)
    {
        var candidateIndex = -1;
        var candidateOffset = direction < 0 ? int.MaxValue : int.MinValue;
        Func<int, int, bool> isBetter = direction < 0
            ? static (offset, current) => offset < current
            : static (offset, current) => offset > current;

        for (var i = 0; i < _additionalCarets.Count; i++)
        {
            if (_additionalCarets[i].CaretAnchor is not { IsDeleted: false } anchor)
            {
                continue;
            }

            if (!isBetter (anchor.Offset, candidateOffset))
            {
                continue;
            }

            candidateOffset = anchor.Offset;
            candidateIndex = i;
        }

        if (candidateIndex < 0)
        {
            return false;
        }

        _additionalCarets.RemoveAt (candidateIndex);
        SetNeedsDraw ();

        return true;
    }

    private void ClearAdditionalCaretSelections ()
    {
        foreach (CaretInfo caret in _additionalCarets)
        {
            caret.SelectionAnchor = null;
        }

        // An edit that collapses per-caret selections also ends the in-flight keyboard
        // column-select gesture. Without this, the next Ctrl+Shift+Alt+Arrow would resume
        // from the stale pre-edit anchor offset/column/row-delta instead of re-capturing
        // from the caret's new position. (Esc already resets this via ClearAdditionalCarets.)
        _keyboardColumnSelectionAnchorOffset = null;
    }

    /// <summary>Holds anchor state for one additional caret.</summary>
    private sealed class CaretInfo
    {
        public TextAnchor? CaretAnchor { get; init; }
        public TextAnchor? SelectionAnchor { get; set; }
    }

    /// <summary>Transient struct used during multi-caret edit iteration.</summary>
    private readonly struct CaretEditInfo
    {
        public required int Offset { get; init; }
        public required bool IsPrimary { get; init; }
        public TextAnchor? SelectionAnchor { get; init; }
    }
}
