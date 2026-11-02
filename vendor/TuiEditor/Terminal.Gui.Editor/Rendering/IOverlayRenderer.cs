using System.Drawing;
using Terminal.Gui.ViewBase;

namespace Terminal.Gui.Editor.Rendering;

/// <summary>Draws cell overlays on top of (after) a visual line's elements.</summary>
public interface IOverlayRenderer
{
    void Draw (View host, CellVisualLine line, int row, Rectangle viewport);
}
