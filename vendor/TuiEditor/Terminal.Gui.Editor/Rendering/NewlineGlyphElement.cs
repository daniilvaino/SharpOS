using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Terminal.Gui.Editor.Rendering;

/// <summary>A newline character rendered as a visible glyph in single-line mode.</summary>
public sealed class NewlineGlyphElement (
    int documentOffset,
    int documentLength,
    int visualColumn,
    Attribute attribute)
    : CellVisualLineElement (documentOffset, documentLength, visualColumn, 1, attribute)
{
    /// <summary>The glyph used to represent a newline character.</summary>
    internal const string NewlineGlyph = "⏎";

    public override void Draw (View host, int x, int y, int visibleStart, int visibleEnd)
    {
        if (VisualEndColumn <= visibleStart || VisualColumn >= visibleEnd)
        {
            return;
        }

        host.SetAttribute (Attribute);
        host.AddStr (x + VisualColumn - visibleStart, y, NewlineGlyph);
    }
}
