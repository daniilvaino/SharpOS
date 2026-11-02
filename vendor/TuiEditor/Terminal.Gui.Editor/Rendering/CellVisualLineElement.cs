using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Terminal.Gui.Editor.Rendering;

/// <summary>A drawable span in a terminal-cell visual line.</summary>
public abstract class CellVisualLineElement (
    int documentOffset,
    int documentLength,
    int visualColumn,
    int visualLength,
    Attribute attribute)
{
    public int DocumentOffset { get; } = documentOffset;

    public int DocumentLength { get; } = documentLength;

    public int DocumentEndOffset => DocumentOffset + DocumentLength;

    public int VisualColumn { get; } = visualColumn;

    public int VisualLength { get; } = visualLength;

    public int VisualEndColumn => VisualColumn + VisualLength;

    /// <summary>Mutable: visual-line transformers may override this before draw.</summary>
    public Attribute Attribute { get; set; } = attribute;

    public abstract void Draw (View host, int x, int y, int visibleStart, int visibleEnd);
}
