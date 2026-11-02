using Terminal.Gui.Editor.Document;

namespace Terminal.Gui.Editor.Rendering;

/// <summary>A single document line projected into terminal-cell elements.</summary>
public sealed class CellVisualLine (DocumentLine documentLine)
{
    private readonly List<CellVisualLineElement> _elements = [];

    public DocumentLine DocumentLine { get; } = documentLine;

    public IReadOnlyList<CellVisualLineElement> Elements => _elements;

    public int VisualLength => _elements.Count == 0 ? 0 : _elements[^1].VisualEndColumn;

    public void AddElement (CellVisualLineElement element)
    {
        _elements.Add (element);
    }

    /// <summary>Replaces all elements with the given list. Used by transformers that restructure the line.</summary>
    public void ReplaceElements (IReadOnlyList<CellVisualLineElement> elements)
    {
        _elements.Clear ();
        _elements.AddRange (elements);
    }

    public int GetVisualColumn (int logicalColumn)
    {
        var clamped = Math.Clamp (logicalColumn, 0, DocumentLine.Length);

        foreach (CellVisualLineElement element in _elements)
        {
            var elementStart = element.DocumentOffset - DocumentLine.Offset;
            var elementEnd = element.DocumentEndOffset - DocumentLine.Offset;

            if (clamped <= elementStart)
            {
                return element.VisualColumn;
            }

            if (clamped <= elementEnd)
            {
                return element.VisualEndColumn;
            }
        }

        return VisualLength;
    }

    public int GetRelativeOffset (int visualColumn)
    {
        var clamped = Math.Max (0, visualColumn);

        foreach (CellVisualLineElement element in _elements)
        {
            var elementStart = element.DocumentOffset - DocumentLine.Offset;
            var elementEnd = element.DocumentEndOffset - DocumentLine.Offset;

            if (clamped < element.VisualEndColumn)
            {
                if (element is TabElement && clamped > element.VisualColumn)
                {
                    var distanceToStart = clamped - element.VisualColumn;
                    var distanceToEnd = element.VisualEndColumn - clamped;

                    return distanceToStart <= distanceToEnd ? elementStart : elementEnd;
                }

                if (clamped <= element.VisualColumn)
                {
                    return elementStart;
                }

                var beforeDistance = clamped - element.VisualColumn;
                var afterDistance = element.VisualEndColumn - clamped;

                return beforeDistance < afterDistance ? elementStart : elementEnd;
            }

            if (clamped == element.VisualEndColumn)
            {
                return elementEnd;
            }
        }

        return DocumentLine.Length;
    }
}
