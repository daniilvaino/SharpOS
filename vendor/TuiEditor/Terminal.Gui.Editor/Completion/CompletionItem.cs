namespace Terminal.Gui.Editor.Completion;

/// <summary>
///     A single completion suggestion returned by an <see cref="IEditorCompletionProvider" />.
///     Modelled after the LSP <c>CompletionItem</c> shape — label + insertText — but kept
///     minimal for the terminal context.
/// </summary>
public sealed class CompletionItem
{
    /// <summary>
    ///     The display text shown in the completion popup. This is the primary string the user sees
    ///     when filtering suggestions.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    ///     The text inserted into the document when this item is accepted. When <see langword="null" />,
    ///     <see cref="Label" /> is inserted instead.
    /// </summary>
    public string? InsertText { get; init; }

    /// <summary>The text that will actually be inserted: <see cref="InsertText" /> ?? <see cref="Label" />.</summary>
    internal string TextToInsert => InsertText ?? Label;
}
