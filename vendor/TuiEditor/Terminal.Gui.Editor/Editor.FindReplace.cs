using Terminal.Gui.Editor.Document.Search;
using Terminal.Gui.Editor.Rendering;

namespace Terminal.Gui.Editor;

public partial class Editor
{
    private SearchHitRenderer? _searchHitRenderer;

    /// <summary>
    ///     Gets or sets the active <see cref="ISearchStrategy" /> used by all Find / Replace operations.
    ///     The string-based convenience overloads (e.g. <see cref="FindNext(string,bool,bool)" />) replace this with a
    ///     <see cref="SearchMode.Normal" /> strategy built from their arguments before delegating to the property-driven
    ///     overloads. Callers that want regex, whole-word, or wildcard search assign their own strategy
    ///     (typically constructed via <see cref="SearchStrategyFactory.Create" />) and then call the no-argument
    ///     <see cref="FindNext(bool)" /> / <see cref="FindPrevious(bool)" /> / <see cref="ReplaceNext(string,bool)" /> /
    ///     <see cref="ReplaceAll(string)" /> overloads.
    /// </summary>
    public ISearchStrategy? SearchStrategy
    {
        get;
        set
        {
            if (Equals (field, value))
            {
                return;
            }

            field = value;
            UpdateSearchHitRenderer ();
            SetNeedsDraw ();
        }
    }

    /// <summary>
    ///     Raised when the user presses <c>Ctrl+F</c> (the Find keybinding). Consumers (e.g. ted) subscribe
    ///     to open their Find dialog.
    /// </summary>
    public event EventHandler? FindRequested;

    /// <summary>
    ///     Raised when the user presses <c>Ctrl+H</c> (the Replace keybinding). Consumers (e.g. ted) subscribe
    ///     to open their Find &amp; Replace dialog.
    /// </summary>
    public event EventHandler? ReplaceRequested;

    private void UpdateSearchHitRenderer ()
    {
        if (SearchStrategy is not null)
        {
            if (_searchHitRenderer is null)
            {
                _searchHitRenderer = new SearchHitRenderer ();
                BackgroundRenderers.Add (_searchHitRenderer);
            }
            else
            {
                _searchHitRenderer.Invalidate ();
            }
        }
        else
        {
            if (_searchHitRenderer is null)
            {
                return;
            }

            BackgroundRenderers.Remove (_searchHitRenderer);
            _searchHitRenderer = null;
        }
    }

    /// <summary>
    ///     Finds the next match of <see cref="SearchStrategy" /> starting at the current caret (or after the current
    ///     selection) and selects it.
    /// </summary>
    /// <returns><see langword="true" /> when a match is found; otherwise <see langword="false" />.</returns>
    public bool FindNext (bool wrapAround = true)
    {
        if (SearchStrategy is null || _document is null || _document.TextLength == 0)
        {
            return false;
        }

        var startOffset = HasSelection ? SelectionEnd : CaretOffset;
        ISearchResult? match = FindForward (startOffset);

        // Skip a zero-length match at the current search start to avoid an infinite no-op loop.
        // Mirrors .NET Regex.NextMatch()'s "advance by 1 after a zero-length match" convention.
        if (match is { Length: 0 } && match.Offset == startOffset)
        {
            match = FindForward (startOffset + 1);
        }

        if (match is null && wrapAround && startOffset > 0)
        {
            match = FindForward (0, startOffset);
        }

        if (match is null)
        {
            return false;
        }

        SelectSearchMatch (match.Offset, match.Length);

        return true;
    }

    /// <summary>
    ///     Convenience overload: builds a <see cref="SearchMode.Normal" /> strategy from <paramref name="searchText" />
    ///     and <paramref name="matchCase" />, assigns it to <see cref="SearchStrategy" />, and delegates to
    ///     <see cref="FindNext(bool)" />.
    /// </summary>
    /// <returns><see langword="true" /> when a match is found; otherwise <see langword="false" />.</returns>
    public bool FindNext (string searchText, bool matchCase = false, bool wrapAround = true)
    {
        if (string.IsNullOrEmpty (searchText))
        {
            return false;
        }

        SearchStrategy = SearchStrategyFactory.Create (searchText, !matchCase, false, SearchMode.Normal);

        return FindNext (wrapAround);
    }

    /// <summary>
    ///     Finds the previous match of <see cref="SearchStrategy" /> before the current caret (or before the current
    ///     selection start) and selects it.
    /// </summary>
    /// <returns><see langword="true" /> when a match is found; otherwise <see langword="false" />.</returns>
    public bool FindPrevious (bool wrapAround = true)
    {
        if (SearchStrategy is null || _document is null || _document.TextLength == 0)
        {
            return false;
        }

        var caretOrSelStart = HasSelection ? SelectionStart : CaretOffset;

        // Find the rightmost match whose start is strictly before the caret/selection-start.
        // We search the entire document and take the last match starting before caretOrSelStart
        // so that matches extending past the caret (i.e. the caret is inside a match) are included.
        ISearchResult? match = SearchStrategy.FindAll (_document, 0, _document.TextLength)
            .TakeWhile (r => r.Offset < caretOrSelStart)
            .LastOrDefault ();

        if (match is null && wrapAround)
        {
            match = SearchStrategy.FindAll (_document, 0, _document.TextLength).LastOrDefault ();
        }

        if (match is null)
        {
            return false;
        }

        SelectSearchMatch (match.Offset, match.Length);

        return true;
    }

    /// <summary>
    ///     Convenience overload mirroring <see cref="FindNext(string,bool,bool)" /> for backwards search.
    /// </summary>
    /// <returns><see langword="true" /> when a match is found; otherwise <see langword="false" />.</returns>
    public bool FindPrevious (string searchText, bool matchCase = false, bool wrapAround = true)
    {
        if (string.IsNullOrEmpty (searchText))
        {
            return false;
        }

        SearchStrategy = SearchStrategyFactory.Create (searchText, !matchCase, false, SearchMode.Normal);

        return FindPrevious (wrapAround);
    }

    /// <summary>
    ///     Replaces the current match — if the selection already corresponds to a fresh match of
    ///     <see cref="SearchStrategy" />, it is replaced in place; otherwise the next match is located and replaced.
    ///     Regex backreferences (<c>$1</c>, <c>$2</c>, ...) in <paramref name="replacement" /> are substituted from
    ///     the matched capture groups via <see cref="ISearchResult.ReplaceWith" />.
    /// </summary>
    /// <returns><see langword="true" /> when a replacement is made; otherwise <see langword="false" />.</returns>
    public bool ReplaceNext (string replacement, bool wrapAround = true)
    {
        ArgumentNullException.ThrowIfNull (replacement);

        if (ReadOnly || SearchStrategy is null || _document is null)
        {
            return false;
        }

        ISearchResult? match = TryMatchSelection ();

        if (match is null)
        {
            if (!FindNext (wrapAround))
            {
                return false;
            }

            match = TryMatchSelection ();

            if (match is null)
            {
                return false;
            }
        }

        var replacementText = match.ReplaceWith (replacement);

        if (match.Length == 0)
        {
            // Zero-length match (regex anchor): insert at the caret position.
            var insertPos = CaretOffset;
            _document.Insert (insertPos, replacementText);

            // The caret anchor (AfterInsertion) already moved past the inserted text.
            // Force a refresh of virtual column state.
            SetCaretOffset (insertPos + replacementText.Length, true);
        }
        else
        {
            ReplaceSelection (replacementText);
        }

        return true;
    }

    /// <summary>
    ///     Convenience overload: builds a <see cref="SearchMode.Normal" /> strategy and delegates to
    ///     <see cref="ReplaceNext(string,bool)" />.
    /// </summary>
    public bool ReplaceNext (string searchText, string replacement, bool matchCase = false, bool wrapAround = true)
    {
        if (string.IsNullOrEmpty (searchText))
        {
            return false;
        }

        SearchStrategy = SearchStrategyFactory.Create (searchText, !matchCase, false, SearchMode.Normal);

        return ReplaceNext (replacement, wrapAround);
    }

    /// <summary>
    ///     Replaces all matches of <see cref="SearchStrategy" /> with <paramref name="replacement" /> (with regex
    ///     backreferences resolved). All replacements collapse into a single undo step (R5).
    /// </summary>
    /// <returns>The number of replacements performed.</returns>
    public int ReplaceAll (string replacement)
    {
        ArgumentNullException.ThrowIfNull (replacement);

        if (ReadOnly || SearchStrategy is null || _document is null)
        {
            return 0;
        }

        // Materialize all matches up front: the rope-materializing cost in RegexSearchStrategy.FindAll is paid
        // exactly once here, instead of once per match as the bespoke IndexOf engine did. Then replace in reverse
        // so the offsets of earlier matches stay valid as later matches shrink/grow the document.
        List<ISearchResult> matches = SearchStrategy.FindAll (_document, 0, _document.TextLength).ToList ();

        if (matches.Count == 0)
        {
            return 0;
        }

        // R5: collapse every replacement into one undo step.
        using IDisposable scope = _document.RunUpdate ();

        for (var i = matches.Count - 1; i >= 0; i--)
        {
            ISearchResult match = matches[i];
            _document.Replace (match.Offset, match.Length, match.ReplaceWith (replacement));
        }

        return matches.Count;
    }

    /// <summary>
    ///     Convenience overload: builds a <see cref="SearchMode.Normal" /> strategy and delegates to
    ///     <see cref="ReplaceAll(string)" />.
    /// </summary>
    /// <returns>The number of replacements performed.</returns>
    public int ReplaceAll (string searchText, string replacement, bool matchCase = false)
    {
        if (string.IsNullOrEmpty (searchText))
        {
            return 0;
        }

        SearchStrategy = SearchStrategyFactory.Create (searchText, !matchCase, false, SearchMode.Normal);

        return ReplaceAll (replacement);
    }

    private ISearchResult? FindForward (int startOffset)
    {
        return FindForward (startOffset, (_document?.TextLength ?? 0) - startOffset);
    }

    private ISearchResult? FindForward (int startOffset, int length)
    {
        if (SearchStrategy is null || _document is null)
        {
            return null;
        }

        if (startOffset < 0 || startOffset > _document.TextLength)
        {
            return null;
        }

        length = Math.Min (length, _document.TextLength - startOffset);

        return length < 0 ? null : SearchStrategy.FindNext (_document, startOffset, length);
    }

    /// <summary>
    ///     Returns the current strategy's match exactly covering the current selection, or <see langword="null" /> when
    ///     the selection does not correspond to a fresh match. Used by <see cref="ReplaceNext(string,bool)" /> so we can
    ///     reach the matched <see cref="ISearchResult" /> (and its capture groups) without re-running the search.
    ///     For zero-length matches (regex anchors like <c>^</c>, <c>$</c>, <c>\b</c>), returns a match at the caret
    ///     position when there is no selection and the caret sits at a zero-length match.
    /// </summary>
    private ISearchResult? TryMatchSelection ()
    {
        if (SearchStrategy is null || _document is null)
        {
            return null;
        }

        // Zero-length match: no selection, but the caret sits at a zero-length match position.
        if (!HasSelection)
        {
            var caretPos = CaretOffset;

            if (caretPos < 0 || caretPos > _document.TextLength)
            {
                return null;
            }

            // Search a 1-char window (or 0 at end) that could contain a zero-length match at caretPos.
            var searchLen = Math.Min (1, _document.TextLength - caretPos);
            ISearchResult zeroMatch = SearchStrategy.FindNext (_document, caretPos, searchLen);

            if (zeroMatch is { Length: 0 } && zeroMatch.Offset == caretPos)
            {
                return zeroMatch;
            }

            return null;
        }

        if (SelectionStart < 0 || SelectionLength <= 0 || SelectionEnd > _document.TextLength)
        {
            return null;
        }

        ISearchResult? match = SearchStrategy.FindNext (_document, SelectionStart, SelectionLength);

        if (match is null || match.Offset != SelectionStart || match.Length != SelectionLength)
        {
            return null;
        }

        return match;
    }

    private void SelectSearchMatch (int startOffset, int length)
    {
        if (_document is null || length < 0)
        {
            return;
        }

        var start = Math.Clamp (startOffset, 0, _document.TextLength);
        var end = Math.Clamp (start + Math.Max (0, length), start, _document.TextLength);

        _selectionAnchor = start == end ? null : CreateSelectionAnchor (start);
        CaretOffset = end;
        RefreshSelectionAnchorMovement ();
        SelectionChanged?.Invoke (this, EventArgs.Empty);
        SetNeedsDraw ();
    }
}
