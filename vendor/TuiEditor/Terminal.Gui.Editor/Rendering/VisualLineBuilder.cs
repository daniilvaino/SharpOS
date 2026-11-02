using Terminal.Gui.Drawing;
using Terminal.Gui.Editor.Document;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Terminal.Gui.Editor.Rendering;

/// <summary>Builds terminal-cell visual lines from document lines.</summary>
public sealed class VisualLineBuilder
{
    public CellVisualLine Build (DocumentLine documentLine, VisualLineBuildContext context)
    {
        var text = context.Document.GetText (documentLine);
        CellVisualLine visualLine = new (documentLine);

        if (IsAsciiOnly (text))
        {
            BuildAsciiFastPath (documentLine, context, text, visualLine);
        }
        else
        {
            BuildGraphemePath (documentLine, context, text, visualLine);
        }

        foreach (IVisualLineTransformer transformer in context.LineTransformers)
        {
            transformer.Transform (visualLine);
        }

        // Apply selection AFTER transformers so highlighting colors don't overwrite selection.
        if (context.HasSelection)
        {
            ApplySelection (visualLine, context);
        }

        return visualLine;
    }

    /// <summary>
    ///     Overwrites <see cref="CellVisualLineElement.Attribute" /> with the selected attribute
    ///     for elements that fall within the selection range. Runs after transformers so that
    ///     highlighting colors don't override selection.
    /// </summary>
    private static void ApplySelection (CellVisualLine visualLine, VisualLineBuildContext context)
    {
        foreach (CellVisualLineElement element in visualLine.Elements)
        {
            if (element.DocumentOffset < context.SelectionEnd
                && element.DocumentEndOffset > context.SelectionStart)
            {
                element.Attribute = context.SelectedAttribute;
            }
        }
    }

    /// <summary>
    ///     Fast path for pure-ASCII lines. Avoids <c>GraphemeHelper.GetGraphemes</c>
    ///     allocation — each byte is one grapheme, one column (tabs expand as usual).
    /// </summary>
    private static void BuildAsciiFastPath (
        DocumentLine documentLine,
        VisualLineBuildContext context,
        string text,
        CellVisualLine visualLine)
    {
        var segmentIndex = 0;
        var segmentEnd = GetSegmentEnd (context.StyledSegments, segmentIndex);
        var visualColumn = 0;

        for (var i = 0; i < text.Length; i++)
        {
            while (context.StyledSegments is { Count: > 0 }
                   && i >= segmentEnd
                   && segmentIndex + 1 < context.StyledSegments.Count)
            {
                segmentIndex++;
                segmentEnd += context.StyledSegments[segmentIndex].Text.Length;
            }

            Attribute attribute = GetAttribute (context, segmentIndex);
            var documentOffset = documentLine.Offset + i;

            if (text[i] == '\t')
            {
                var width = GetTabExpansionWidth (visualColumn, context.IndentationSize);
                visualLine.AddElement (
                    new TabElement (documentOffset, visualColumn, width, context.ShowTabs, attribute));
                visualColumn += width;
            }
            else
            {
                TextRunElement element = new (documentOffset, 1, visualColumn, text.Substring (i, 1), attribute);
                visualLine.AddElement (element);
                visualColumn += 1;
            }
        }
    }

    private static void BuildGraphemePath (
        DocumentLine documentLine,
        VisualLineBuildContext context,
        string text,
        CellVisualLine visualLine)
    {
        var logicalColumn = 0;
        var visualColumn = 0;
        var segmentIndex = 0;
        var segmentEnd = GetSegmentEnd (context.StyledSegments, segmentIndex);

        foreach (var grapheme in GraphemeHelper.GetGraphemes (text))
        {
            while (context.StyledSegments is { Count: > 0 }
                   && logicalColumn >= segmentEnd
                   && segmentIndex + 1 < context.StyledSegments.Count)
            {
                segmentIndex++;
                segmentEnd += context.StyledSegments[segmentIndex].Text.Length;
            }

            Attribute attribute = GetAttribute (context, segmentIndex);
            var documentOffset = documentLine.Offset + logicalColumn;
            var documentLength = grapheme.Length;

            if (grapheme == "\t")
            {
                var width = GetTabExpansionWidth (visualColumn, context.IndentationSize);
                visualLine.AddElement (
                    new TabElement (documentOffset, visualColumn, width, context.ShowTabs, attribute));
                visualColumn += width;
            }
            else
            {
                TextRunElement element = new (documentOffset, documentLength, visualColumn, grapheme, attribute);
                visualLine.AddElement (element);
                visualColumn += element.VisualLength;
            }

            logicalColumn += documentLength;
        }
    }

    /// <summary>Checks if all characters are ASCII (no multi-byte graphemes, no surrogates).</summary>
    private static bool IsAsciiOnly (string text)
    {
        foreach (var c in text)
        {
            if (c > 127)
            {
                return false;
            }
        }

        return true;
    }

    public static int GetTabExpansionWidth (int visualColumn, int indentationSize)
    {
        var remainder = visualColumn % indentationSize;

        return remainder == 0 ? indentationSize : indentationSize - remainder;
    }

    private static Attribute GetAttribute (VisualLineBuildContext context, int segmentIndex)
    {
        if (context.StyledSegments is not { Count: > 0 })
        {
            return context.NormalAttribute;
        }

        Attribute segmentAttribute =
            context.StyledSegments[Math.Min (segmentIndex, context.StyledSegments.Count - 1)].Attribute
            ?? context.NormalAttribute;

        return segmentAttribute;
    }

    private static void BuildAsciiSegment (
        DocumentLine documentLine,
        VisualLineBuildContext context,
        string text,
        int textOffset,
        CellVisualLine visualLine)
    {
        var visualColumn = 0;

        for (var i = 0; i < text.Length; i++)
        {
            Attribute attribute = context.NormalAttribute;
            var documentOffset = documentLine.Offset + textOffset + i;

            if (text[i] == '\t')
            {
                var width = GetTabExpansionWidth (visualColumn, context.IndentationSize);
                visualLine.AddElement (
                    new TabElement (documentOffset, visualColumn, width, context.ShowTabs, attribute));
                visualColumn += width;
            }
            else
            {
                TextRunElement element = new (documentOffset, 1, visualColumn, text.Substring (i, 1), attribute);
                visualLine.AddElement (element);
                visualColumn += 1;
            }
        }
    }

    private static void BuildGraphemeSegment (
        DocumentLine documentLine,
        VisualLineBuildContext context,
        string text,
        int textOffset,
        CellVisualLine visualLine)
    {
        var logicalColumn = 0;
        var visualColumn = 0;

        foreach (var grapheme in GraphemeHelper.GetGraphemes (text))
        {
            Attribute attribute = context.NormalAttribute;
            var documentOffset = documentLine.Offset + textOffset + logicalColumn;
            var documentLength = grapheme.Length;

            if (grapheme == "\t")
            {
                var width = GetTabExpansionWidth (visualColumn, context.IndentationSize);
                visualLine.AddElement (
                    new TabElement (documentOffset, visualColumn, width, context.ShowTabs, attribute));
                visualColumn += width;
            }
            else
            {
                TextRunElement element = new (documentOffset, documentLength, visualColumn, grapheme, attribute);
                visualLine.AddElement (element);
                visualColumn += element.VisualLength;
            }

            logicalColumn += documentLength;
        }
    }

    private static int GetSegmentEnd (IReadOnlyList<StyledSegment>? segments, int segmentIndex)
    {
        return segments is { Count: > 0 } ? segments[segmentIndex].Text.Length : int.MaxValue;
    }
}
