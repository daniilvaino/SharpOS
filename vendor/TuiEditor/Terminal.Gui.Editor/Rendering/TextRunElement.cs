using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Terminal.Gui.Editor.Rendering;

/// <summary>A grapheme cluster rendered as document text.</summary>
public sealed class TextRunElement (
    int documentOffset,
    int documentLength,
    int visualColumn,
    string text,
    Attribute attribute)
    : CellVisualLineElement (documentOffset, documentLength, visualColumn, Math.Max (0, text.GetColumns ()),
        attribute)
{
    public string Text { get; } = text;

    public override void Draw (View host, int x, int y, int visibleStart, int visibleEnd)
    {
        if (VisualLength <= 0 || VisualEndColumn <= visibleStart || VisualColumn >= visibleEnd)
        {
            return;
        }

        host.SetAttribute (Attribute);

        if (VisualColumn >= visibleStart && VisualEndColumn <= visibleEnd)
        {
            host.AddStr (x + VisualColumn - visibleStart, y, Text);

            return;
        }

        // Wide grapheme straddles the viewport edge — AddStr rejects it because the full
        // cluster doesn't fit. Write directly to the driver content buffer so at least the
        // visible leading cell renders.
        if (host.App?.Driver?.Contents is not { } contents)
        {
            return;
        }

        var drawColumn = Math.Max (VisualColumn, visibleStart);
        Point screen = host.ViewportToScreen (new Point (x + drawColumn - visibleStart, y));

        if (screen.Y < 0 || screen.Y >= contents.GetLength (0)
                         || screen.X < 0 || screen.X >= contents.GetLength (1))
        {
            return;
        }

        contents[screen.Y, screen.X] = new Cell
        {
            Attribute = Attribute,
            Grapheme = Text,
            IsDirty = true
        };
    }
}
