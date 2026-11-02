// Adapted for Terminal.Gui from AvaloniaEdit d7a6b63
// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Collections.ObjectModel;

namespace Terminal.Gui.Editor.Document.Folding;

/// <summary>
///     Stores a list of foldings for a specific TextDocument.
/// </summary>
public class FoldingManager
{
    private readonly TextSegmentCollection<FoldingSection> _foldings;
    private bool _isFirstUpdate = true;
    private bool _suppressFoldingChanged;

    #region Constructor

    /// <summary>
    ///     Creates a new FoldingManager instance.
    /// </summary>
    public FoldingManager (TextDocument document)
    {
        Document = document ?? throw new ArgumentNullException (nameof (document));
        _foldings = new TextSegmentCollection<FoldingSection> ();
        TextDocumentWeakEventManager.Changed.AddHandler (document, OnDocumentChanged);
    }

    #endregion

    /// <summary>Gets the document this folding manager is attached to.</summary>
    public TextDocument Document { get; }

    #region ReceiveWeakEvent

    private void OnDocumentChanged (object? sender, DocumentChangeEventArgs e)
    {
        var lineStructurePreservingChange = IsLineStructurePreservingChange (e);
        var hasFoldedSections = HasFoldedSections ();

        if (!hasFoldedSections && lineStructurePreservingChange)
        {
            _suppressFoldingChanged = true;

            try
            {
                _foldings.UpdateOffsets (e);
            }
            finally
            {
                _suppressFoldingChanged = false;
            }

            return;
        }

        _foldings.UpdateOffsets (e);

        var newEndOffset = e.Offset + e.InsertionLength;
        // extend end offset to the end of the line (including delimiter)
        DocumentLine endLine = Document.GetLineByOffset (newEndOffset);
        newEndOffset = endLine.Offset + endLine.TotalLength;
        foreach (FoldingSection affectedFolding in
                 _foldings.FindOverlappingSegments (e.Offset, newEndOffset - e.Offset))
        {
            if (affectedFolding.Length == 0)
            {
                RemoveFolding (affectedFolding);
            }
        }
    }

    private bool HasFoldedSections ()
    {
        foreach (FoldingSection fs in _foldings)
        {
            if (fs.IsFolded)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLineStructurePreservingChange (DocumentChangeEventArgs e)
    {
        OffsetChangeMap map = e.OffsetChangeMap;
        var hasInsertion = false;
        var hasRemoval = false;

        foreach (OffsetChangeMapEntry entry in map)
        {
            hasInsertion |= entry.InsertionLength > 0;
            hasRemoval |= entry.RemovalLength > 0;

            if (entry.InsertionLength > 0 && entry.RemovalLength > 0)
            {
                return false;
            }
        }

        if (!hasInsertion && !hasRemoval)
        {
            return false;
        }

        if (hasInsertion && hasRemoval)
        {
            return false;
        }

        if (hasInsertion)
        {
            return !MappedInsertionsContainNewLines (map, e.InsertedText, e.Offset);
        }

        return !MappedRemovalsContainNewLines (map, e.RemovedText, e.Offset, e.RemovalLength);
    }

    private static bool MappedInsertionsContainNewLines (OffsetChangeMap map, ITextSource text, int baseOffset)
    {
        if (InsertionEntriesUseInsertedTextCoordinates (map, baseOffset, text.TextLength))
        {
            return MappedInsertionsContainNewLinesWithoutShift (map, text, baseOffset);
        }

        var insertedShift = 0;

        foreach (OffsetChangeMapEntry entry in map)
        {
            if (entry.InsertionLength == 0)
            {
                continue;
            }

            var relativeOffset = entry.Offset - baseOffset + insertedShift;

            for (var i = 0; i < entry.InsertionLength; i++)
            {
                var ch = text.GetCharAt (relativeOffset + i);

                if (ch is '\r' or '\n')
                {
                    return true;
                }
            }

            insertedShift += entry.InsertionLength;
        }

        return false;
    }

    private static bool MappedInsertionsContainNewLinesWithoutShift (OffsetChangeMap map, ITextSource text,
        int baseOffset)
    {
        foreach (OffsetChangeMapEntry entry in map)
        {
            if (entry.InsertionLength == 0)
            {
                continue;
            }

            var relativeOffset = entry.Offset - baseOffset;

            for (var i = 0; i < entry.InsertionLength; i++)
            {
                var ch = text.GetCharAt (relativeOffset + i);

                if (ch is '\r' or '\n')
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool MappedRemovalsContainNewLines (
        OffsetChangeMap map,
        ITextSource text,
        int baseOffset,
        int removalLength)
    {
        if (!RemovalEntriesUseRemovedTextCoordinates (map, baseOffset, removalLength))
        {
            return MappedInsertionsContainNewLines (map.Invert (), text, baseOffset);
        }

        foreach (OffsetChangeMapEntry entry in map)
        {
            if (entry.RemovalLength == 0)
            {
                continue;
            }

            var relativeOffset = entry.Offset - baseOffset;

            for (var i = 0; i < entry.RemovalLength; i++)
            {
                var ch = text.GetCharAt (relativeOffset + i);

                if (ch is '\r' or '\n')
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool RemovalEntriesUseRemovedTextCoordinates (OffsetChangeMap map, int baseOffset, int removalLength)
    {
        return map.Count > 0
               && map[0].RemovalLength > 0
               && map[0].Offset + map[0].RemovalLength == baseOffset + removalLength;
    }

    private static bool InsertionEntriesUseInsertedTextCoordinates (OffsetChangeMap map, int baseOffset,
        int insertionLength)
    {
        return map.Count > 0
               && map[^1].InsertionLength > 0
               && map[^1].Offset + map[^1].InsertionLength == baseOffset + insertionLength;
    }

    #endregion

    #region UpdateFoldings

    /// <summary>
    ///     Updates the foldings in this <see cref="FoldingManager" /> using the given new foldings.
    ///     This method will try to detect which new foldings correspond to which existing foldings; and will keep the state
    ///     (<see cref="FoldingSection.IsFolded" />) for existing foldings.
    /// </summary>
    /// <param name="newFoldings">The new set of foldings. These must be sorted by starting offset.</param>
    /// <param name="firstErrorOffset">
    ///     The first position of a parse error. Existing foldings starting after
    ///     this offset will be kept even if they don't appear in <paramref name="newFoldings" />.
    ///     Use -1 for this parameter if there were no parse errors.
    /// </param>
    public void UpdateFoldings (IEnumerable<NewFolding> newFoldings, int firstErrorOffset)
    {
        if (newFoldings == null)
        {
            throw new ArgumentNullException (nameof (newFoldings));
        }

        if (firstErrorOffset < 0)
        {
            firstErrorOffset = int.MaxValue;
        }

        FoldingSection[] oldFoldings = AllFoldings.ToArray ();
        var oldFoldingIndex = 0;
        var previousStartOffset = 0;
        // merge new foldings into old foldings so that sections keep being collapsed
        // both oldFoldings and newFoldings are sorted by start offset
        foreach (NewFolding newFolding in newFoldings)
        {
            // ensure newFoldings are sorted correctly
            if (newFolding.StartOffset < previousStartOffset)
            {
                throw new ArgumentException ("newFoldings must be sorted by start offset");
            }

            previousStartOffset = newFolding.StartOffset;

            if (newFolding.StartOffset == newFolding.EndOffset)
            {
                continue; // ignore zero-length foldings
            }

            // remove old foldings that were skipped
            while (oldFoldingIndex < oldFoldings.Length &&
                   newFolding.StartOffset > oldFoldings[oldFoldingIndex].StartOffset)
            {
                RemoveFolding (oldFoldings[oldFoldingIndex++]);
            }

            FoldingSection section;
            // reuse current folding if its matching:
            if (oldFoldingIndex < oldFoldings.Length &&
                newFolding.StartOffset == oldFoldings[oldFoldingIndex].StartOffset)
            {
                section = oldFoldings[oldFoldingIndex++];
                section.Length = newFolding.EndOffset - newFolding.StartOffset;
            }
            else
            {
                // no matching current folding; create a new one:
                section = CreateFolding (newFolding.StartOffset, newFolding.EndOffset);
                // auto-close #regions only when opening the document
                if (_isFirstUpdate)
                {
                    section.IsFolded = newFolding.DefaultClosed;
                }

                section.Tag = newFolding;
            }

            section.Title = newFolding.Name;
        }

        _isFirstUpdate = false;
        // remove all outstanding old foldings:
        while (oldFoldingIndex < oldFoldings.Length)
        {
            FoldingSection oldSection = oldFoldings[oldFoldingIndex++];
            if (oldSection.StartOffset >= firstErrorOffset)
            {
                break;
            }

            RemoveFolding (oldSection);
        }
    }

    #endregion

    #region Events

    /// <summary>
    ///     Raised when any folding section changes (created, removed, folded/unfolded, or moved by edits).
    ///     The Editor subscribes to this to invalidate visual state.
    /// </summary>
    public event EventHandler? FoldingChanged;

    internal void RaiseFoldingChanged ()
    {
        if (_suppressFoldingChanged)
        {
            return;
        }

        FoldingChanged?.Invoke (this, EventArgs.Empty);
    }

    #endregion

    #region Create / Remove / Clear

    /// <summary>
    ///     Creates a folding for the specified text section.
    /// </summary>
    public FoldingSection CreateFolding (int startOffset, int endOffset)
    {
        if (startOffset >= endOffset)
        {
            throw new ArgumentException ("startOffset must be less than endOffset");
        }

        if (startOffset < 0 || endOffset > Document.TextLength)
        {
            throw new ArgumentException ("Folding must be within document boundary");
        }

        FoldingSection fs = new (this, startOffset, endOffset);
        _foldings.Add (fs);
        RaiseFoldingChanged ();
        return fs;
    }

    /// <summary>
    ///     Removes a folding section from this manager.
    /// </summary>
    public void RemoveFolding (FoldingSection fs)
    {
        if (fs == null)
        {
            throw new ArgumentNullException (nameof (fs));
        }

        fs.IsFolded = false;
        _foldings.Remove (fs);
        RaiseFoldingChanged ();
    }

    /// <summary>
    ///     Removes all folding sections.
    /// </summary>
    public void Clear ()
    {
        foreach (FoldingSection s in _foldings)
        {
            s.IsFolded = false;
        }

        _foldings.Clear ();
        RaiseFoldingChanged ();
    }

    #endregion

    #region Get...Folding

    /// <summary>
    ///     Gets all foldings in this manager.
    ///     The foldings are returned sorted by start offset;
    ///     for multiple foldings at the same offset the order is undefined.
    /// </summary>
    public IEnumerable<FoldingSection> AllFoldings => _foldings;

    /// <summary>
    ///     Gets the first offset greater or equal to <paramref name="startOffset" /> where a folded folding starts.
    ///     Returns -1 if there are no foldings after <paramref name="startOffset" />.
    /// </summary>
    public int GetNextFoldedFoldingStart (int startOffset)
    {
        FoldingSection? fs = _foldings.FindFirstSegmentWithStartAfter (startOffset);
        while (fs != null && !fs.IsFolded)
        {
            fs = _foldings.GetNextSegment (fs);
        }

        return fs?.StartOffset ?? -1;
    }

    /// <summary>
    ///     Gets the first folding with a <see cref="TextSegment.StartOffset" /> greater or equal to
    ///     <paramref name="startOffset" />.
    ///     Returns null if there are no foldings after <paramref name="startOffset" />.
    /// </summary>
    public FoldingSection GetNextFolding (int startOffset)
    {
        return _foldings.FindFirstSegmentWithStartAfter (startOffset);
    }

    /// <summary>
    ///     Gets all foldings that start exactly at <paramref name="startOffset" />.
    /// </summary>
    public ReadOnlyCollection<FoldingSection> GetFoldingsAt (int startOffset)
    {
        List<FoldingSection> result = new ();
        FoldingSection? fs = _foldings.FindFirstSegmentWithStartAfter (startOffset);
        while (fs != null && fs.StartOffset == startOffset)
        {
            result.Add (fs);
            fs = _foldings.GetNextSegment (fs);
        }

        return new ReadOnlyCollection<FoldingSection> (result);
    }

    /// <summary>
    ///     Gets all foldings that contain <paramref name="offset" />.
    /// </summary>
    public ReadOnlyCollection<FoldingSection> GetFoldingsContaining (int offset)
    {
        return _foldings.FindSegmentsContaining (offset);
    }

    #endregion

    #region Helpers

    /// <summary>
    ///     Returns the total number of document lines hidden by collapsed folds,
    ///     correctly computing the union of hidden ranges to avoid double-counting nested folds.
    /// </summary>
    public int GetHiddenLineCount ()
    {
        List<(int start, int end)> ranges = [];

        foreach (FoldingSection fs in _foldings)
        {
            if (!fs.IsFolded)
            {
                continue;
            }

            DocumentLine startLine =
                Document.GetLineByOffset (Math.Clamp (fs.StartOffset, 0, Document.TextLength));
            DocumentLine endLine = Document.GetLineByOffset (Math.Clamp (fs.EndOffset, 0, Document.TextLength));
            var hiddenStart = startLine.LineNumber + 1;
            var hiddenEnd = endLine.LineNumber;

            if (hiddenEnd >= hiddenStart)
            {
                ranges.Add ((hiddenStart, hiddenEnd));
            }
        }

        if (ranges.Count == 0)
        {
            return 0;
        }

        // Sort and merge overlapping/adjacent ranges to avoid double-counting nested folds.
        ranges.Sort ((a, b) => a.start != b.start ? a.start.CompareTo (b.start) : a.end.CompareTo (b.end));
        var hidden = 0;
        (int start, int end) current = ranges[0];

        for (var i = 1; i < ranges.Count; i++)
        {
            if (ranges[i].start <= current.end + 1)
            {
                current.end = Math.Max (current.end, ranges[i].end);
            }
            else
            {
                hidden += current.end - current.start + 1;
                current = ranges[i];
            }
        }

        hidden += current.end - current.start + 1;

        return hidden;
    }

    /// <summary>
    ///     Returns whether the given 1-based line number is hidden by a collapsed fold.
    /// </summary>
    public bool IsLineHidden (int lineNumber)
    {
        foreach (FoldingSection fs in _foldings)
        {
            if (!fs.IsFolded)
            {
                continue;
            }

            DocumentLine startLine =
                Document.GetLineByOffset (Math.Clamp (fs.StartOffset, 0, Document.TextLength));
            DocumentLine endLine = Document.GetLineByOffset (Math.Clamp (fs.EndOffset, 0, Document.TextLength));
            if (lineNumber > startLine.LineNumber && lineNumber <= endLine.LineNumber)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Gets the folding section that starts on the given 1-based line number, if any.
    /// </summary>
    public FoldingSection? GetFoldingAtLine (int lineNumber)
    {
        if (lineNumber < 1 || lineNumber > Document.LineCount)
        {
            return null;
        }

        DocumentLine line = Document.GetLineByNumber (lineNumber);
        foreach (FoldingSection fs in _foldings.FindSegmentsContaining (line.Offset))
        {
            DocumentLine startLine = Document.GetLineByOffset (fs.StartOffset);
            if (startLine.LineNumber == lineNumber)
            {
                return fs;
            }
        }

        // Also check segments starting within the line
        foreach (FoldingSection fs in _foldings.FindOverlappingSegments (line.Offset, line.Length))
        {
            DocumentLine startLine = Document.GetLineByOffset (fs.StartOffset);
            if (startLine.LineNumber == lineNumber)
            {
                return fs;
            }
        }

        return null;
    }

    /// <summary>
    ///     Returns the next visible 1-based line number after a folded region.
    ///     If the line is not hidden, returns it unchanged.
    /// </summary>
    public int GetNextVisibleLineNumber (int lineNumber)
    {
        foreach (FoldingSection fs in _foldings)
        {
            if (!fs.IsFolded)
            {
                continue;
            }

            DocumentLine startLine =
                Document.GetLineByOffset (Math.Clamp (fs.StartOffset, 0, Document.TextLength));
            DocumentLine endLine = Document.GetLineByOffset (Math.Clamp (fs.EndOffset, 0, Document.TextLength));
            if (lineNumber > startLine.LineNumber && lineNumber <= endLine.LineNumber)
            {
                return endLine.LineNumber + 1;
            }
        }

        return lineNumber;
    }

    #endregion
}
