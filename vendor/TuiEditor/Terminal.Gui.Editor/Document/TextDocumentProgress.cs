namespace Terminal.Gui.Editor.Document;

/// <summary>Reports streaming document I/O progress.</summary>
/// <param name="CharactersProcessed">The number of decoded characters loaded or saved so far.</param>
/// <param name="TotalCharacters">The total character count, when known.</param>
/// <param name="BytesProcessed">The number of bytes consumed so far, when known.</param>
/// <param name="TotalBytes">The total byte count, when known.</param>
public readonly record struct TextDocumentProgress (
    long CharactersProcessed,
    long? TotalCharacters = null,
    long? BytesProcessed = null,
    long? TotalBytes = null)
{
    /// <summary>Gets the best available completion fraction, or <see langword="null" /> when no total is known.</summary>
    public double? Fraction =>
        TotalBytes is > 0 && BytesProcessed is { } bytesProcessed
            ? Math.Clamp ((double)bytesProcessed / TotalBytes.Value, 0, 1)
            : TotalCharacters is > 0
                ? Math.Clamp ((double)CharactersProcessed / TotalCharacters.Value, 0, 1)
                : null;
}
