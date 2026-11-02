namespace Terminal.Gui.Editor.Document.Folding;

/// <summary>
///     Strategy for computing folding regions and determining whether a document
///     change may affect folding structure.
/// </summary>
public interface IFoldingStrategy
{
    /// <summary>Recompute all foldings for the given document.</summary>
    void UpdateFoldings (FoldingManager manager, TextDocument document);

    /// <summary>
    ///     Returns <see langword="true" /> if the given change may have introduced or
    ///     removed folding-structural characters (braces, newlines, etc.).
    ///     The orchestrator uses this to skip expensive re-scans on plain text edits.
    /// </summary>
    bool ChangeMayAffectFoldings (DocumentChangeEventArgs e);
}
