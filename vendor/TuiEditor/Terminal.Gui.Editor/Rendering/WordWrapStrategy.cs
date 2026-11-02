using Terminal.Gui.Drawing;
using Terminal.Gui.Text;

namespace Terminal.Gui.Editor.Rendering;

/// <summary>A segment produced by word wrapping a single document line.</summary>
/// <param name="StartOffset">Character offset into the document line text where this segment begins.</param>
/// <param name="Length">Number of characters in this segment.</param>
/// <param name="LeadingIndent">Visual leading indent (cells) for continuation lines. 0 for the first segment.</param>
public readonly record struct WrapSegment (int StartOffset, int Length, int LeadingIndent);

/// <summary>
///     Walks grapheme clusters and breaks a line into wrap segments at whitespace boundaries.
///     When no whitespace is found before the wrap column, a hard break is applied.
/// </summary>
public static class WordWrapStrategy
{
    /// <summary>
    ///     Computes wrap segments for a single document line's text.
    /// </summary>
    /// <param name="text">The text of the document line (no line terminator).</param>
    /// <param name="wrapColumn">The maximum visual column width before wrapping.</param>
    /// <param name="indentationSize">Tab stop width for tab expansion.</param>
    /// <returns>A list of wrap segments. A line shorter than <paramref name="wrapColumn" /> returns a single segment.</returns>
    public static IReadOnlyList<WrapSegment> ComputeSegments (string text, int wrapColumn, int indentationSize)
    {
        if (wrapColumn <= 0)
        {
            return [new WrapSegment (0, text.Length, 0)];
        }

        List<WrapSegment> segments = [];
        var segmentStart = 0;

        while (segmentStart < text.Length)
        {
            var visualColumn = 0;
            var lastWhitespaceOffset = -1;
            var currentOffset = segmentStart;
            var reachedEnd = false;

            // Walk grapheme clusters from segmentStart.
            foreach (var grapheme in EnumerateFrom (text, segmentStart))
            {
                int width;

                if (grapheme == "\t")
                {
                    var remainder = visualColumn % indentationSize;
                    width = remainder == 0 ? indentationSize : indentationSize - remainder;
                }
                else
                {
                    width = Math.Max (0, grapheme.GetColumns ());
                }

                // Check if adding this grapheme would exceed the wrap column.
                if (visualColumn + width > wrapColumn && currentOffset > segmentStart)
                {
                    // We need to break here.
                    if (lastWhitespaceOffset >= 0)
                    {
                        // Break after the last whitespace.
                        var segLength = lastWhitespaceOffset - segmentStart;
                        segments.Add (new WrapSegment (segmentStart, segLength, 0));
                        segmentStart = lastWhitespaceOffset;
                        // Skip whitespace at the start of the next segment.
                        segmentStart = SkipWhitespace (text, segmentStart);
                    }
                    else
                    {
                        // Hard break — no whitespace found before the wrap column.
                        var segLength = currentOffset - segmentStart;
                        segments.Add (new WrapSegment (segmentStart, segLength, 0));
                        segmentStart = currentOffset;
                    }

                    reachedEnd = false;

                    break;
                }

                // Track whitespace positions for breaking.
                if (IsWhitespace (grapheme) && currentOffset > segmentStart)
                {
                    // The break point is right after this whitespace character.
                    lastWhitespaceOffset = currentOffset + grapheme.Length;
                }

                visualColumn += width;
                currentOffset += grapheme.Length;
                reachedEnd = true;
            }

            if (reachedEnd || currentOffset >= text.Length)
            {
                // The rest of the text fits within the wrap column.
                var remaining = text.Length - segmentStart;

                if (remaining > 0)
                {
                    segments.Add (new WrapSegment (segmentStart, remaining, 0));
                }

                break;
            }
        }

        if (segments.Count == 0)
        {
            segments.Add (new WrapSegment (0, text.Length, 0));
        }

        return segments;
    }

    private static IEnumerable<string> EnumerateFrom (string text, int startOffset)
    {
        var sub = text[startOffset..];

        foreach (var grapheme in GraphemeHelper.GetGraphemes (sub))
        {
            yield return grapheme;
        }
    }

    private static bool IsWhitespace (string grapheme)
    {
        return grapheme.Length == 1 && char.IsWhiteSpace (grapheme[0]);
    }

    private static int SkipWhitespace (string text, int offset)
    {
        while (offset < text.Length && char.IsWhiteSpace (text[offset]))
        {
            offset++;
        }

        return offset;
    }
}
