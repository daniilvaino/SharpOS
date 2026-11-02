using Terminal.Gui.Editor.Document;
using Terminal.Gui.Input;

namespace Terminal.Gui.Editor.Completion;

/// <summary>
///     Provides completion suggestions for a document at a given caret position. Consumers implement
///     this interface and assign an instance to <see cref="Editor.CompletionProvider" /> to enable
///     in-editor completion.
/// </summary>
/// <remarks>
///     The provider is queried synchronously on every triggering keystroke. Keep
///     <see cref="GetCompletions" /> fast — pre-index if necessary.
/// </remarks>
public interface IEditorCompletionProvider
{
    /// <summary>
    ///     Returns completion items for the current caret position, or an empty list when no
    ///     suggestions are available. The <paramref name="prefix" /> is the word fragment
    ///     immediately before the caret that the editor extracted from the document.
    /// </summary>
    /// <param name="document">The document being edited.</param>
    /// <param name="caretOffset">Current caret offset in the document.</param>
    /// <param name="prefix">
    ///     The word fragment before the caret (letters/digits/underscores back to the nearest
    ///     non-word character or line start). Empty when the caret follows whitespace or punctuation.
    /// </param>
    /// <returns>An ordered list of suggestions. The first item is pre-selected in the popup.</returns>
    IReadOnlyList<CompletionItem> GetCompletions (TextDocument document, int caretOffset, string prefix);

    /// <summary>
    ///     Returns <see langword="true" /> when the given key should trigger the completion popup
    ///     (e.g. <c>Ctrl+Space</c>). The editor calls this before normal key dispatch; if the
    ///     provider claims the key, the popup opens (or re-filters).
    /// </summary>
    bool ShouldTrigger (Key key);
}
