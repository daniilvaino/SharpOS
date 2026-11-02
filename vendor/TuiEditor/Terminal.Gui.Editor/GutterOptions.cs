namespace Terminal.Gui.Editor;

/// <summary>
///     Specifies which elements the <see cref="Gutter" /> displays.
///     Combine flags to show multiple elements: <c>GutterOptions.LineNumbers | GutterOptions.Folding</c>.
/// </summary>
[Flags]
public enum GutterOptions
{
    /// <summary>No gutter elements are displayed.</summary>
    None = 0,

    /// <summary>Display one-based line numbers in the gutter.</summary>
    LineNumbers = 1,

    /// <summary>Display fold indicators (▸/▾/│) in the gutter when a <see cref="Editor.FoldingManager" /> is set.</summary>
    Folding = 2
}
