// Adapted for Terminal.Gui from AvaloniaEdit d7a6b63
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
using System.Text;
using System.Text.RegularExpressions;

namespace Terminal.Gui.Editor.Document.Search
{
    /// <summary>
    /// Provides factory methods for ISearchStrategies.
    /// </summary>
    public static class SearchStrategyFactory
    {
        /// <summary>
        /// Creates a default ISearchStrategy with the given parameters.
        /// </summary>
        public static ISearchStrategy Create(string searchPattern, bool ignoreCase, bool matchWholeWords, SearchMode mode)
        {
            if (searchPattern == null)
                throw new ArgumentNullException(nameof(searchPattern));
            // Terminal.Gui deviation: upstream accepts an empty pattern, which compiles to a
            // regex that matches at every position (TextLength+1 results) — a DoS in FindAll /
            // ReplaceAll. Reject up front. Whitespace patterns remain legitimate (they match
            // literal whitespace in Normal mode and match the same in Regex mode).
            // Logged in third_party/AvaloniaEdit/UPSTREAM.md.
            if (searchPattern.Length == 0)
                throw new ArgumentException("Search pattern must not be empty.", nameof(searchPattern));
            var options = RegexOptions.Multiline;
            if (ignoreCase)
                options |= RegexOptions.IgnoreCase;

            switch (mode)
            {
                case SearchMode.Normal:
                    searchPattern = Regex.Escape(searchPattern);
                    break;
                case SearchMode.Wildcard:
                    searchPattern = ConvertWildcardsToRegex(searchPattern);
                    break;
            }

            try
            {
                var pattern = new Regex(searchPattern, options);
                return new RegexSearchStrategy(pattern, matchWholeWords);
            }
            catch (ArgumentException ex)
            {
                throw new SearchPatternException(ex.Message, ex);
            }
        }

        private static string ConvertWildcardsToRegex(string searchPattern)
        {
            if (string.IsNullOrEmpty(searchPattern))
                return "";

            var builder = new StringBuilder();

            foreach (var ch in searchPattern)
            {
                switch (ch)
                {
                    case '?':
                        builder.Append('.');
                        break;
                    case '*':
                        builder.Append(".*");
                        break;
                    default:
                        builder.Append(Regex.Escape(ch.ToString()));
                        break;
                }
            }

            return builder.ToString();
        }
    }
}
