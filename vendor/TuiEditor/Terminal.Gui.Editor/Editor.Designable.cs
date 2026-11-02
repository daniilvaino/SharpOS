using Terminal.Gui.Editor.Document;
using Terminal.Gui.Editor.Highlighting;
using Terminal.Gui.ViewBase;

namespace Terminal.Gui.Editor;

/// <summary>
///     Design-time support for <see cref="Editor" /> so designers can preview representative
///     content with highlighting and line numbers enabled.
/// </summary>
public partial class Editor : IDesignable
{
    /// <summary>
    ///     Enables design-time mode by loading representative sample content so the editor renders
    ///     meaningfully in TG's designer / UI Catalog. Activates C# syntax highlighting and line
    ///     numbers. Inert at runtime — calling this on a live editor replaces the document.
    /// </summary>
    /// <returns><see langword="true" />.</returns>
    public bool EnableForDesign ()
    {
        Document = new TextDocument (EditorDesignData.SampleCSharpCode);
        HighlightingDefinition = HighlightingManager.Instance.GetDefinition ("C#");
        GutterOptions = GutterOptions.LineNumbers | GutterOptions.Folding;

        return true;
    }

    /// <inheritdoc />
    bool IDesignable.EnableForDesign<TContext> (ref TContext targetView)
    {
        return EnableForDesign ();
    }
}
