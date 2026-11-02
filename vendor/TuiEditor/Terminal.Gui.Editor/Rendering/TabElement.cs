using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Terminal.Gui.Editor.Rendering;

/// <summary>A tab character expanded to the next indentation stop at render time.</summary>
public sealed class TabElement (
    int documentOffset,
    int visualColumn,
    int visualLength,
    bool showTabs,
    Attribute attribute)
    : CellVisualLineElement (documentOffset, 1, visualColumn, visualLength, attribute)
{
    private const string TabGlyph = "→";

    public bool ShowTabs { get; } = showTabs;

    public override void Draw (View host, int x, int y, int visibleStart, int visibleEnd)
    {
        if (VisualLength <= 0 || VisualEndColumn <= visibleStart || VisualColumn >= visibleEnd)
        {
            return;
        }

        var drawStart = Math.Max (VisualColumn, visibleStart);
        var drawEnd = Math.Min (VisualEndColumn, visibleEnd);

        if (drawEnd <= drawStart)
        {
            return;
        }

        var text = ShowTabs ? TabGlyph + new string (' ', VisualLength - 1) : new string (' ', VisualLength);
        var visibleText = text[(drawStart - VisualColumn)..(drawEnd - VisualColumn)];

        host.SetAttribute (Attribute);
        host.AddStr (x + drawStart - visibleStart, y, visibleText);
    }
}
