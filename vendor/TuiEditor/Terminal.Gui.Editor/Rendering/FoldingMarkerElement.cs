using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Terminal.Gui.Editor.Rendering;

/// <summary>
///     A visual-line element that renders the fold marker <c>"⋯"</c> in place of collapsed text.
/// </summary>
public sealed class FoldingMarkerElement (
    int documentOffset,
    int documentLength,
    int visualColumn,
    Attribute attribute,
    string title = "⋯")
    : CellVisualLineElement (documentOffset, documentLength, visualColumn, Math.Max (1, title.Length), attribute)
{
    /// <summary>The text shown for the collapsed fold (defaults to <c>"⋯"</c>).</summary>
    public string Title { get; } = title;

    /// <inheritdoc />
    public override void Draw (View host, int x, int y, int visibleStart, int visibleEnd)
    {
        if (VisualLength <= 0 || VisualEndColumn <= visibleStart || VisualColumn >= visibleEnd)
        {
            return;
        }

        host.SetAttribute (Attribute);

        var drawStart = Math.Max (VisualColumn, visibleStart);
        var drawEnd = Math.Min (VisualEndColumn, visibleEnd);

        if (drawEnd <= drawStart)
        {
            return;
        }

        var text = Title;

        if (drawStart > VisualColumn || drawEnd < VisualEndColumn)
        {
            text = text[(drawStart - VisualColumn)..(drawEnd - VisualColumn)];
        }

        host.AddStr (x + drawStart - visibleStart, y, text);
    }
}
