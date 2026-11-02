using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Editor.Document.Folding;
using Terminal.Gui.Editor.Highlighting;
using Terminal.Gui.Editor.Rendering;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Terminal.Gui.Editor;

public partial class Editor
{
    /// <summary>Cached visible-line mapping; cleared when folds change or the document changes.</summary>
    private List<int>? _cachedVisibleLineNumbers;

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="Editor" /> renders the document itself in <see cref="OnDrawingContent" />; the
    ///     base <see cref="View.Text" /> mirror kept by the <c>new Text</c> setter must not also be
    ///     drawn by the base text pass.
    /// </remarks>
    protected override bool OnDrawingText (DrawContext? context)
    {
        return true;
    }

    /// <inheritdoc />
    protected override bool OnDrawingContent (DrawContext? context)
    {
        if (_document is null)
        {
            return true;
        }

        Rectangle viewport = Viewport;
        Attribute normal = GetAttributeForRole (VisualRole.Normal);
        Attribute selected = GetAttributeForRole (VisualRole.Active);

        // Ensure the colorizer sees the current attribute (scheme may have changed since install).
        EnsureColorizerAttribute (normal);

        DrawVisibleLines (viewport, normal, selected);

        if (_maxWidthGrewDuringDraw)
        {
            // A rendered line was wider than the estimate; resize content (cheap — not _maxWidthDirty)
            // so the horizontal scrollbar reflects what's now on screen. Monotonic: once the widest
            // visible line is measured this stops firing, so no draw/layout loop.
            _maxWidthGrewDuringDraw = false;
            UpdateContentSize ();
        }

        SetAttribute (normal);
        UpdateCursor ();

        return true;
    }

    private void DrawVisibleLines (Rectangle viewport, Attribute normal, Attribute selected)
    {
        var hasSelection = HasSelection;
        var selStart = hasSelection ? SelectionStart : 0;
        var selEnd = hasSelection ? SelectionEnd : 0;
        var visibleStart = viewport.X;
        var visibleEnd = viewport.X + viewport.Width;

        if (!Multiline)
        {
            DrawSingleLineFlat (viewport, normal, selected, selStart, selEnd, visibleStart, visibleEnd);

            return;
        }

        if (WordWrap)
        {
            DrawWrappedLines (viewport, normal, selected, selStart, selEnd);

            return;
        }

        // Build a mapping from viewport row → document line number (1-based),
        // skipping lines hidden by collapsed folds.
        List<int> visibleLineNumbers = GetVisibleLineNumbers ();

        for (var row = 0; row < viewport.Height; row++)
        {
            var visibleIndex = viewport.Y + row;

            if (visibleIndex < 0 || visibleIndex >= visibleLineNumbers.Count)
            {
                break;
            }

            var lineNumber = visibleLineNumbers[visibleIndex];
            DocumentLine line = _document!.GetLineByNumber (lineNumber);

            DrawVisualLine (row, line, visibleStart, visibleEnd, null, normal, selected, selStart, selEnd);
        }
    }

    private void DrawWrappedLines (Rectangle viewport, Attribute normal, Attribute selected, int selStart, int selEnd)
    {
        List<WrapMapEntry> map = GetWrapMap ();
        var wrapColumn = GetWrapColumn ();

        for (var row = 0; row < viewport.Height; row++)
        {
            var visibleIndex = viewport.Y + row;

            if (visibleIndex < 0 || visibleIndex >= map.Count)
            {
                break;
            }

            WrapMapEntry entry = map[visibleIndex];
            DocumentLine line = _document!.GetLineByNumber (entry.LineNumber);
            var text = _document.GetText (line);
            IReadOnlyList<WrapSegment> segments =
                WordWrapStrategy.ComputeSegments (text, wrapColumn, IndentationSize);
            WrapSegment seg = segments[entry.SegmentIndex];

            var segText = text.Substring (seg.StartOffset, seg.Length);

            CellVisualLine visualLine = BuildWrappedSegmentVisualLine (
                line, seg.StartOffset, segText, normal, selected, selStart, selEnd);

            foreach (IBackgroundRenderer renderer in BackgroundRenderers)
            {
                renderer.Draw (this, visualLine, row, viewport);
            }

            foreach (CellVisualLineElement element in visualLine.Elements)
            {
                if (element.VisualColumn >= viewport.Width)
                {
                    break;
                }

                element.Draw (this, 0, row, 0, viewport.Width);
            }

            foreach (IOverlayRenderer renderer in OverlayRenderers)
            {
                renderer.Draw (this, visualLine, row, viewport);
            }
        }
    }

    /// <summary>
    ///     Draws all document content on a single visual row with newline characters rendered as
    ///     visible glyphs. Used when <see cref="Multiline" /> is <see langword="false" />.
    /// </summary>
    private void DrawSingleLineFlat (
        Rectangle viewport,
        Attribute normal,
        Attribute selected,
        int selStart,
        int selEnd,
        int visibleStart,
        int visibleEnd)
    {
        // Build a composite visual line from visible document lines (respecting folds),
        // inserting newline glyph elements between them. Uses the first visible line as the owner.
        List<int> visibleLines = GetVisibleLineNumbers ();

        if (visibleLines.Count == 0)
        {
            return;
        }

        DocumentLine firstLine = _document!.GetLineByNumber (visibleLines[0]);
        CellVisualLine composite = new (firstLine);
        var flatColumn = 0;

        for (var idx = 0; idx < visibleLines.Count; idx++)
        {
            var lineNum = visibleLines[idx];
            DocumentLine line = _document.GetLineByNumber (lineNum);
            CellVisualLine lineVisual = GetOrBuildDrawVisualLine (line, null, normal, selected, selStart, selEnd);

            foreach (CellVisualLineElement element in lineVisual.Elements)
            {
                CellVisualLineElement shifted = ShiftElement (element, flatColumn);
                composite.AddElement (shifted);
            }

            flatColumn += lineVisual.VisualLength;

            if (idx >= visibleLines.Count - 1)
            {
                continue;
            }

            // The newline delimiter occupies document offsets at the end of the line.
            var nlOffset = line.Offset + line.Length;
            var nlLength = line.DelimiterLength;

            // Determine the newline glyph attribute: use selection attribute if within selection.
            Attribute nlAttr = selStart < selEnd && nlOffset < selEnd && nlOffset + nlLength > selStart
                ? selected
                : normal;

            composite.AddElement (new NewlineGlyphElement (nlOffset, nlLength, flatColumn, nlAttr));
            flatColumn += 1;
        }

        foreach (IBackgroundRenderer renderer in BackgroundRenderers)
        {
            renderer.Draw (this, composite, 0, viewport);
        }

        foreach (CellVisualLineElement element in composite.Elements)
        {
            if (element.VisualColumn >= visibleEnd)
            {
                break;
            }

            if (element.VisualEndColumn <= visibleStart)
            {
                continue;
            }

            element.Draw (this, 0, 0, visibleStart, visibleEnd);
        }

        foreach (IOverlayRenderer renderer in OverlayRenderers)
        {
            renderer.Draw (this, composite, 0, viewport);
        }
    }

    /// <summary>
    ///     Creates a copy of an element shifted by a flat column offset. Used to compose
    ///     elements from multiple document lines onto a single visual row.
    /// </summary>
    private static CellVisualLineElement ShiftElement (CellVisualLineElement element, int columnOffset)
    {
        if (columnOffset == 0)
        {
            return element;
        }

        return element switch
        {
            TabElement tab => new TabElement (
                tab.DocumentOffset, tab.VisualColumn + columnOffset, tab.VisualLength, tab.ShowTabs, tab.Attribute),
            FoldingMarkerElement fold => new FoldingMarkerElement (
                fold.DocumentOffset, fold.DocumentLength, fold.VisualColumn + columnOffset, fold.Attribute, fold.Title),
            TextRunElement text => new TextRunElement (
                text.DocumentOffset, text.DocumentLength, text.VisualColumn + columnOffset, text.Text, text.Attribute),
            _ => element // Unknown element type — leave as-is (shouldn't happen).
        };
    }

    private CellVisualLine BuildWrappedSegmentVisualLine (
        DocumentLine documentLine,
        int segmentStartOffset,
        string segmentText,
        Attribute normal,
        Attribute selected,
        int selStart,
        int selEnd)
    {
        CellVisualLine visualLine = new (documentLine);
        var visualColumn = 0;

        for (var i = 0; i < segmentText.Length; i++)
        {
            var documentOffset = documentLine.Offset + segmentStartOffset + i;

            if (segmentText[i] == '\t')
            {
                var width = VisualLineBuilder.GetTabExpansionWidth (visualColumn, IndentationSize);
                visualLine.AddElement (
                    new TabElement (documentOffset, visualColumn, width, ShowTabs, normal));
                visualColumn += width;
            }
            else if (segmentText[i] <= 127)
            {
                TextRunElement element = new (documentOffset, 1, visualColumn, segmentText.Substring (i, 1), normal);
                visualLine.AddElement (element);
                visualColumn += 1;
            }
            else
            {
                // Handle multibyte graphemes — fall through to grapheme path for remainder.
                var remaining = segmentText[i..];

                foreach (var grapheme in GraphemeHelper.GetGraphemes (remaining))
                {
                    var gDocOffset = documentLine.Offset + segmentStartOffset + i;
                    TextRunElement gElement = new (gDocOffset, grapheme.Length, visualColumn, grapheme, normal);
                    visualLine.AddElement (gElement);
                    visualColumn += gElement.VisualLength;
                    i += grapheme.Length;
                }

                i--; // Compensate for the for-loop increment.

                break;
            }
        }

        // Apply transformers BEFORE selection so highlighting doesn't overwrite selection.
        foreach (IVisualLineTransformer transformer in LineTransformers)
        {
            transformer.Transform (visualLine);
        }

        // Apply selection AFTER transformers (same order as the non-wrapped path).
        if (selStart < selEnd)
        {
            foreach (CellVisualLineElement element in visualLine.Elements)
            {
                if (element.DocumentOffset < selEnd && element.DocumentEndOffset > selStart)
                {
                    element.Attribute = selected;
                }
            }
        }

        ApplyAdditionalCaretSelections (visualLine, selected);

        return visualLine;
    }

    /// <summary>
    ///     Returns a list of 1-based document line numbers that are visible (not hidden by folds),
    ///     in order. Cached until folds change.
    /// </summary>
    internal List<int> GetVisibleLineNumbers ()
    {
        if (_cachedVisibleLineNumbers is not null)
        {
            return _cachedVisibleLineNumbers;
        }

        List<int> result = new ();

        if (_document is null)
        {
            _cachedVisibleLineNumbers = result;

            return result;
        }

        FoldingManager? fm = FoldingManager;
        var lineNumber = 1;

        while (lineNumber <= _document.LineCount)
        {
            result.Add (lineNumber);

            if (fm is not null)
            {
                // If there are folded sections starting on this line, skip past the deepest one.
                var maxEndLine = lineNumber;

                foreach (FoldingSection fs in fm.AllFoldings)
                {
                    if (!fs.IsFolded)
                    {
                        continue;
                    }

                    DocumentLine startLine =
                        fm.Document.GetLineByOffset (Math.Clamp (fs.StartOffset, 0, fm.Document.TextLength));

                    if (startLine.LineNumber != lineNumber)
                    {
                        continue;
                    }

                    DocumentLine endLine =
                        fm.Document.GetLineByOffset (Math.Clamp (fs.EndOffset, 0, fm.Document.TextLength));

                    if (endLine.LineNumber > maxEndLine)
                    {
                        maxEndLine = endLine.LineNumber;
                    }
                }

                if (maxEndLine > lineNumber)
                {
                    lineNumber = maxEndLine + 1;

                    continue;
                }
            }

            lineNumber++;
        }

        _cachedVisibleLineNumbers = result;

        return result;
    }

    /// <summary>
    ///     Rebuilds the <see cref="HighlightingColorizer" /> if the editor's normal attribute
    ///     has changed (e.g. after a scheme swap or focus change). Keeps the same underlying
    ///     <see cref="DocumentHighlighter" />.
    /// </summary>
    private void EnsureColorizerAttribute (Attribute normal)
    {
        if (_highlightingColorizer is null)
        {
            return;
        }

        HighlightingColorizer replacement = _highlightingColorizer.WithDefaultAttribute (normal);

        if (ReferenceEquals (replacement, _highlightingColorizer))
        {
            return;
        }

        var index = LineTransformers.IndexOf (_highlightingColorizer);

        if (index >= 0)
        {
            LineTransformers[index] = replacement;
        }
        else
        {
            LineTransformers.Insert (0, replacement);
        }

        _highlightingColorizer = replacement;
    }

    private void DrawVisualLine (
        int row,
        DocumentLine line,
        int visibleStart,
        int visibleEnd,
        IReadOnlyList<StyledSegment>? segments,
        Attribute normal,
        Attribute selected,
        int selStart,
        int selEnd)
    {
        // GetOrBuildDrawVisualLine caches when segments == null && no selection && no transformers,
        // i.e. plain-text scrolling without a highlighter. The caret-path cache is separate so the
        // two don't thrash each other's entries (they use different attribute sets).
        CellVisualLine visualLine = GetOrBuildDrawVisualLine (line, segments, normal, selected, selStart, selEnd);

        // A line we actually render gives us its exact width for free. If the running max was an
        // estimate (large document) and this visible line is wider, grow the extent — reconciled
        // once after the draw in OnDrawingContent so the horizontal scrollbar tracks visible content.
        if (!WordWrap && visualLine.VisualLength > _maxVisualWidth)
        {
            _maxVisualWidth = visualLine.VisualLength;
            _maxWidthLineNumber = line.LineNumber;
            _maxWidthGrewDuringDraw = true;
        }

        foreach (IBackgroundRenderer renderer in BackgroundRenderers)
        {
            renderer.Draw (this, visualLine, row, Viewport);
        }

        foreach (CellVisualLineElement element in visualLine.Elements)
        {
            // Elements are ordered by visual column. Once we pass the visible end,
            // all remaining elements are off-screen — skip them entirely.
            if (element.VisualColumn >= visibleEnd)
            {
                break;
            }

            if (element.VisualEndColumn <= visibleStart)
            {
                continue;
            }

            element.Draw (this, 0, row, visibleStart, visibleEnd);
        }

        foreach (IOverlayRenderer renderer in OverlayRenderers)
        {
            renderer.Draw (this, visualLine, row, Viewport);
        }
    }

    private void UpdateCursor ()
    {
        if (!HasFocus || _document is null)
        {
            Cursor = new Cursor ();

            return;
        }

        Rectangle viewport = Viewport;
        var caretLine = GetCaretVisibleLineIndex ();
        var caretCol = GetCaretColumn ();
        var row = caretLine - viewport.Y;
        var col = caretCol - viewport.X;

        if (row < 0 || row >= viewport.Height || col < 0 || col >= viewport.Width)
        {
            Cursor = new Cursor ();

            return;
        }

        Point screen = ViewportToScreen (new Point (col, row));
        CursorStyle style;

        if (OverwriteMode)
        {
            style = CursorStyle.SteadyBlock;
        }
        else if (Cursor.Style == CursorStyle.Hidden)
        {
            style = CursorStyle.Default;
        }
        else
        {
            style = Cursor.Style;
        }

        Cursor = Cursor with
        {
            Position = screen,
            Style = style
        };
    }

    private void ApplyAdditionalCaretSelections (CellVisualLine visualLine, Attribute selected)
    {
        if (!HasAdditionalCaretSelections ())
        {
            return;
        }

        foreach ((int start, int end) selection in AdditionalCaretSelectionRanges ())
        {
            foreach (CellVisualLineElement element in visualLine.Elements)
            {
                if (element.DocumentOffset < selection.end && element.DocumentEndOffset > selection.start)
                {
                    element.Attribute = selected;
                }
            }
        }
    }
}
