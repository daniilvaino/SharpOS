using System.Drawing;
using Terminal.Gui.ViewBase;

namespace Terminal.Gui.Editor.Rendering;

/// <summary>Draws cell backgrounds behind a visual line.</summary>
public interface IBackgroundRenderer
{
    void Draw (View host, CellVisualLine line, int row, Rectangle viewport);
}
