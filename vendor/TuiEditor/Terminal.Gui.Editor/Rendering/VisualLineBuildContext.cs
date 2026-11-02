using Terminal.Gui.Drawing;
using Terminal.Gui.Editor.Document;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Terminal.Gui.Editor.Rendering;

/// <summary>Options and styling inputs used to build a visual line.</summary>
public sealed class VisualLineBuildContext (
    TextDocument document,
    int indentationSize,
    bool showTabs,
    Attribute normalAttribute,
    Attribute selectedAttribute,
    IReadOnlyList<StyledSegment>? styledSegments,
    int selectionStart,
    int selectionEnd,
    IEnumerable<IVisualLineTransformer> lineTransformers)
{
    public TextDocument Document { get; } = document;

    public int IndentationSize { get; } = indentationSize;

    public bool ShowTabs { get; } = showTabs;

    public Attribute NormalAttribute { get; } = normalAttribute;

    public Attribute SelectedAttribute { get; } = selectedAttribute;

    public IReadOnlyList<StyledSegment>? StyledSegments { get; } = styledSegments;

    public int SelectionStart { get; } = selectionStart;

    public int SelectionEnd { get; } = selectionEnd;

    public IEnumerable<IVisualLineTransformer> LineTransformers { get; } = lineTransformers;

    public bool HasSelection => SelectionStart < SelectionEnd;
}
