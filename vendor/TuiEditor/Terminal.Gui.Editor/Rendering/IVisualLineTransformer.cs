namespace Terminal.Gui.Editor.Rendering;

/// <summary>Mutates visual-line elements before they are drawn.</summary>
public interface IVisualLineTransformer
{
    void Transform (CellVisualLine line);
}
