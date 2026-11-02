using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Editor.Rendering;
using Terminal.Gui.Input;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Terminal.Gui.Editor;

public partial class Editor
{
    private Point _columnDragAnchor;

    private DragMode _dragMode;

    /// <inheritdoc />
    protected override bool OnMouseEvent (Mouse mouse)
    {
        // Completion popup click takes priority — when the popup is visible and the
        // user clicks in its area, accept the clicked item.
        if (HandleCompletionMouse (mouse))
        {
            return true;
        }

        if (_document is null)
        {
            return false;
        }

        if (mouse.Position is not { } pos)
        {
            return false;
        }

        // Right-click → show built-in context menu at the click position.
        // When ContextMenu is null the click is left unhandled so it can bubble.
        if (mouse.Flags.HasFlag (MouseFlags.RightButtonClicked))
        {
            if (ContextMenu is null)
            {
                return false;
            }

            ShowContextMenu (mouse.ScreenPosition);

            return true;
        }

        var shift = mouse.Flags.HasFlag (MouseFlags.Shift);

        if (mouse.Flags.HasFlag (MouseFlags.LeftButtonTripleClicked))
        {
            SelectLineAtOffset (MousePositionToOffset (pos));

            return true;
        }

        if (mouse.Flags.HasFlag (MouseFlags.LeftButtonDoubleClicked))
        {
            SelectWordAtOffset (MousePositionToOffset (pos));

            return true;
        }

        // Drag: left button held while position changes. Tested before the plain-press check
        // because PositionReport+LeftButtonPressed also satisfies it.
        if (mouse.Flags.FastHasFlags (MouseFlags.LeftButtonPressed | MouseFlags.PositionReport))
        {
            // A Ctrl-modified drag is never a primary move — it's part of a Ctrl+Click gesture.
            // Some terminals emit PositionReport+Ctrl before the matching LeftButtonPressed+Ctrl;
            // swallowing it here keeps the pre-press report from hijacking the primary caret.
            if (mouse.Flags.HasFlag (MouseFlags.Ctrl))
            {
                return true;
            }

            switch (_dragMode)
            {
                case DragMode.ColumnCarets:
                    SetVerticalCaretsFromViewRows (_columnDragAnchor.Y, pos.Y, _columnDragAnchor.X, pos.X);

                    return true;

                case DragMode.AddCaret:
                    return true;

                case DragMode.Select:
                default:
                    // Route through the selection helper so SelectionChanged fires only on real changes.
                    ExtendCaretTo (MousePositionToOffset (pos));

                    return true;
            }
        }

        // Press: focus, then classify the gesture by modifier.
        if (mouse.Flags.HasFlag (MouseFlags.LeftButtonPressed))
        {
            if (CanFocus && !HasFocus)
            {
                SetFocus ();
            }

            var ctrl = mouse.Flags.HasFlag (MouseFlags.Ctrl);
            var alt = mouse.Flags.HasFlag (MouseFlags.Alt);

            // Alt (not VS Code's Shift+Alt): Windows Terminal eats Shift+drag for its own
            // forced/block selection while the app has mouse mode on, so Shift+Alt+drag never
            // reaches the editor there. Alt+drag is forwarded. See DEC-006 / TG#4888.
            if (alt)
            {
                _dragMode = DragMode.ColumnCarets;
                _columnDragAnchor = pos;
                SetVerticalCaretsFromViewRows (pos.Y, pos.Y, pos.X, pos.X);
            }
            else if (ctrl)
            {
                _dragMode = DragMode.AddCaret;
                ToggleCaretAt (MousePositionToOffset (pos));
            }
            else if (shift)
            {
                _dragMode = DragMode.Select;
                ExtendCaretTo (MousePositionToOffset (pos));
            }
            else
            {
                _dragMode = DragMode.Select;
                ClearAdditionalCarets ();
                ClearSelection ();
                CaretOffset = MousePositionToOffset (pos);
            }

            // Grab the mouse so subsequent drag/release events route here even if the cursor leaves
            // this view's bounds — TG's default routing only delivers events to the view under the
            // pointer, which would break the drag gesture mid-stroke.
            App?.Mouse.GrabMouse (this);

            return true;
        }

        // Release: end the drag-grab so other views start receiving events again.
        if (!mouse.Flags.HasFlag (MouseFlags.LeftButtonReleased))
        {
            return false;
        }

        _dragMode = DragMode.Select;
        App?.Mouse.UngrabMouse ();

        return true;
    }

    /// <summary>
    ///     Converts a viewport-relative <see cref="Point" /> into a clamped document offset. Mouse events
    ///     past the end of a line snap to that line's end; clicks below the last line snap to the last line.
    /// </summary>
    private int MousePositionToOffset (Point viewPos)
    {
        if (_document is null || _document.LineCount == 0)
        {
            return 0;
        }

        var col = Math.Max (0, Viewport.X + viewPos.X);

        if (WordWrap)
        {
            return MousePositionToOffsetWrapped (viewPos, col);
        }

        if (!Multiline)
        {
            return MousePositionToOffsetFlat (col);
        }

        // Map viewport row to document line via visible-line list (respects folding).
        List<int> visibleLines = GetVisibleLineNumbers ();
        var visibleIndex = Math.Clamp (Viewport.Y + viewPos.Y, 0, visibleLines.Count - 1);
        var lineNumber = visibleLines[visibleIndex];
        DocumentLine line = _document.GetLineByNumber (lineNumber);
        var colInLine = GetOrBuildDefaultVisualLine (line).GetRelativeOffset (col);

        return line.Offset + colInLine;
    }

    /// <summary>
    ///     Single-line flat mode: resolves a flat visual column to a document offset by walking
    ///     visible lines and their newline glyphs. Ensures clicks on the ⏎ glyph snap to the
    ///     start of the delimiter (end of line text).
    /// </summary>
    private int MousePositionToOffsetFlat (int flatCol)
    {
        List<int> visibleLines = GetVisibleLineNumbers ();
        var accumulatedCol = 0;

        for (var idx = 0; idx < visibleLines.Count; idx++)
        {
            DocumentLine line = _document!.GetLineByNumber (visibleLines[idx]);
            CellVisualLine lineVisual = GetOrBuildDefaultVisualLine (line);
            var lineVisualEnd = accumulatedCol + lineVisual.VisualLength;

            if (flatCol < lineVisualEnd || idx == visibleLines.Count - 1)
            {
                // The click is within this line's visual span (or this is the last line).
                var colInLine = lineVisual.GetRelativeOffset (flatCol - accumulatedCol);

                return line.Offset + colInLine;
            }

            // Check if the click is on the newline glyph (1 cell after the line's visual end).
            if (flatCol < lineVisualEnd + 1)
            {
                // Snap to end of line text (before the delimiter) — the caret sits before the ⏎.
                return line.Offset + line.Length;
            }

            accumulatedCol = lineVisualEnd + 1; // line visual length + 1 for the ⏎ glyph
        }

        return _document!.TextLength;
    }

    /// <summary>
    ///     Word-wrap-aware variant of <see cref="MousePositionToOffset" />. Uses the wrap map to
    ///     resolve the visual row to a specific segment within a document line, then computes the
    ///     offset within that segment.
    /// </summary>
    private int MousePositionToOffsetWrapped (Point viewPos, int col)
    {
        List<WrapMapEntry> map = GetWrapMap ();

        if (map.Count == 0)
        {
            return 0;
        }

        var visibleIndex = Math.Clamp (Viewport.Y + viewPos.Y, 0, map.Count - 1);
        WrapMapEntry entry = map[visibleIndex];
        DocumentLine line = _document!.GetLineByNumber (entry.LineNumber);
        var text = _document.GetText (line);
        IReadOnlyList<WrapSegment> segments =
            WordWrapStrategy.ComputeSegments (text, GetWrapColumn (), IndentationSize);
        WrapSegment seg = segments[entry.SegmentIndex];
        var segText = text.Substring (seg.StartOffset, seg.Length);

        // Build a visual line for just this segment so GetRelativeOffset works on segment-local columns.
        // GetRelativeOffset returns an offset relative to DocumentLine.Offset (already includes
        // seg.StartOffset), and falls back to DocumentLine.Length for past-end clicks — clamp to
        // the segment boundary instead.
        CellVisualLine visualLine = BuildVisualLineForSegment (line, seg.StartOffset, segText);
        var offsetInLine = Math.Min (visualLine.GetRelativeOffset (col), seg.StartOffset + seg.Length);

        return line.Offset + offsetInLine;
    }

    /// <summary>
    ///     Builds a <see cref="CellVisualLine" /> for a single word-wrap segment of a document line.
    ///     Used by the mouse-hit-testing path — only element geometry matters, not attributes.
    /// </summary>
    private CellVisualLine BuildVisualLineForSegment (DocumentLine documentLine, int segmentStartOffset,
        string segmentText)
    {
        CellVisualLine visualLine = new (documentLine);
        var visualColumn = 0;
        Attribute attr = default;

        for (var i = 0; i < segmentText.Length; i++)
        {
            var documentOffset = documentLine.Offset + segmentStartOffset + i;

            if (segmentText[i] == '\t')
            {
                var width = VisualLineBuilder.GetTabExpansionWidth (visualColumn, IndentationSize);
                visualLine.AddElement (new TabElement (documentOffset, visualColumn, width, false, attr));
                visualColumn += width;
            }
            else if (segmentText[i] <= 127)
            {
                TextRunElement element = new (documentOffset, 1, visualColumn, segmentText.Substring (i, 1), attr);
                visualLine.AddElement (element);
                visualColumn += 1;
            }
            else
            {
                var remaining = segmentText[i..];

                foreach (var grapheme in GraphemeHelper.GetGraphemes (remaining))
                {
                    var gDocOffset = documentLine.Offset + segmentStartOffset + i;
                    TextRunElement gElement = new (gDocOffset, grapheme.Length, visualColumn, grapheme, attr);
                    visualLine.AddElement (gElement);
                    visualColumn += gElement.VisualLength;
                    i += grapheme.Length;
                }

                i--;

                break;
            }
        }

        return visualLine;
    }

    /// <summary>
    ///     Which gesture the in-progress left-button drag belongs to. One state instead of a set
    ///     of fighting "suppress…UntilRelease" booleans: the press classifies the gesture, every
    ///     subsequent drag/release event dispatches on it. Reset to <see cref="DragMode.Select" />
    ///     (the neutral default) on release.
    /// </summary>
    private enum DragMode
    {
        /// <summary>Plain or Shift drag: extend the primary selection to the drag point.</summary>
        Select,

        /// <summary>Ctrl+Click add-caret: swallow drag events so they don't move the primary.</summary>
        AddCaret,

        /// <summary>
        ///     Alt drag: build a vertical column of carets from press row to drag row. Alt (not
        ///     VS Code's Shift+Alt) because Windows Terminal reserves Shift+drag for its own
        ///     forced/block text selection while an app has mouse mode on — see
        ///     specs/decisions.md DEC-006 and tui-cs/Terminal.Gui#4888.
        /// </summary>
        ColumnCarets
    }
}
