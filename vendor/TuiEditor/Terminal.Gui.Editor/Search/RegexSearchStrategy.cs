// Adapted for Terminal.Gui from AvaloniaEdit d7a6b63
#nullable disable
﻿// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Terminal.Gui.Editor.Document;

namespace Terminal.Gui.Editor.Document.Search
{
    internal class RegexSearchStrategy : ISearchStrategy
	{
	    private readonly Regex _searchPattern;
	    private readonly bool _matchWholeWords;

		public RegexSearchStrategy(Regex searchPattern, bool matchWholeWords)
		{
		    _searchPattern = searchPattern ?? throw new ArgumentNullException(nameof(searchPattern));
			_matchWholeWords = matchWholeWords;
		}

		public IEnumerable<ISearchResult> FindAll(ITextSource document, int offset, int length)
		{
			// Terminal.Gui deviation from AvaloniaEdit d7a6b63 (tui-cs/Editor#82): use Regex.Match(text, startat)
			// to begin scanning at `offset` instead of `_searchPattern.Matches(text)` which always starts at
			// index 0 and then post-filters. For repeated FindNext at increasing offsets (e.g. F3 advancing
			// through a document) this eliminates O(offset) wasted regex work per call. The .NET Regex engine
			// preserves RegexOptions.Multiline `^` / `$` semantics correctly across `startat` — anchoring at
			// the start position only when it is 0 or follows a newline. Matches still arrive in order, so
			// we can short-circuit once `result.Index >= endOffset`. Logged in
			// third_party/AvaloniaEdit/UPSTREAM.md.
			int endOffset = offset + length;
			string text = document.Text;
			Match result = _searchPattern.Match(text, offset);
			while (result.Success) {
				int resultEndOffset = result.Length + result.Index;
				// Matches arrive in ascending order of Index, so once Index passes endOffset there
				// can be no more in-range results. Use `>` rather than `>=` so zero-length matches
				// exactly at endOffset are still yielded (upstream's `endOffset < resultEndOffset`
				// post-filter keeps them).
				if (result.Index > endOffset)
					yield break;
				if (resultEndOffset > endOffset) {
					result = result.NextMatch();
					continue;
				}
				if (_matchWholeWords && (!IsWordBorder(document, result.Index) || !IsWordBorder(document, resultEndOffset))) {
					result = result.NextMatch();
					continue;
				}
				yield return new SearchResult { StartOffset = result.Index, Length = result.Length, Data = result };
				result = result.NextMatch();
			}
		}

	    private static bool IsWordBorder(ITextSource document, int offset)
		{
			return TextUtilities.GetNextCaretPosition(document, offset - 1, LogicalDirection.Forward, CaretPositioningMode.WordBorder) == offset;
		}

		public ISearchResult FindNext(ITextSource document, int offset, int length)
		{
			return FindAll(document, offset, length).FirstOrDefault();
		}

		public bool Equals(ISearchStrategy other)
		{
			var strategy = other as RegexSearchStrategy;
			return strategy != null &&
				strategy._searchPattern.ToString() == _searchPattern.ToString() &&
				strategy._searchPattern.Options == _searchPattern.Options &&
				strategy._searchPattern.RightToLeft == _searchPattern.RightToLeft &&
				// Terminal.Gui deviation: upstream omits _matchWholeWords from equality, so two
				// strategies that differ only by whole-word matching compare equal — breaks
				// consumer caching/dedup. Logged in third_party/AvaloniaEdit/UPSTREAM.md.
				strategy._matchWholeWords == _matchWholeWords;
		}
	}

    internal class SearchResult : TextSegment, ISearchResult
	{
		public Match Data { get; set; }

		public string ReplaceWith(string replacement)
		{
			return Data.Result(replacement);
		}
	}
}
