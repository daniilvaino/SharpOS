namespace Terminal.Gui.Editor.Document.Folding;

/// <summary>
///     Detects foldable regions based on matching brace pairs (<c>{}</c>). Useful as a
///     language-agnostic folding strategy for C-family languages, JSON, etc.
/// </summary>
public class BraceFoldingStrategy : IFoldingStrategy
{
    /// <summary>Gets or sets the opening brace character. Defaults to <c>{</c>.</summary>
    public char OpeningBrace { get; set; } = '{';

    /// <summary>Gets or sets the closing brace character. Defaults to <c>}</c>.</summary>
    public char ClosingBrace { get; set; } = '}';

    /// <inheritdoc />
    public void UpdateFoldings (FoldingManager manager, TextDocument document)
    {
        IEnumerable<NewFolding> foldings = CreateNewFoldings (document, out var firstErrorOffset);
        manager.UpdateFoldings (foldings, firstErrorOffset);
    }

    /// <inheritdoc />
    public bool ChangeMayAffectFoldings (DocumentChangeEventArgs e)
    {
        if (TryGetMappedStructuralChange (e, out var mappedStructuralChange))
        {
            return mappedStructuralChange;
        }

        return ContainsStructuralCharacter (e.InsertedText)
               || ContainsStructuralCharacter (e.RemovedText);
    }

    /// <summary>
    ///     Creates <see cref="NewFolding" />s for the specified document by scanning for brace pairs.
    /// </summary>
    public IEnumerable<NewFolding> CreateNewFoldings (TextDocument document, out int firstErrorOffset)
    {
        firstErrorOffset = -1;
        List<NewFolding> newFoldings = new ();
        Stack<int> startOffsets = new ();
        var lastNewLineOffset = 0;

        for (var i = 0; i < document.TextLength; i++)
        {
            var c = document.GetCharAt (i);

            if (c is '\n' or '\r')
            {
                lastNewLineOffset = i + 1;
            }
            else if (c == OpeningBrace)
            {
                startOffsets.Push (i);
            }
            else if (c == ClosingBrace && startOffsets.Count > 0)
            {
                var startOffset = startOffsets.Pop ();

                // Only create a folding if it spans multiple lines.
                if (startOffset < lastNewLineOffset)
                {
                    newFoldings.Add (new NewFolding (startOffset, i + 1));
                }
            }
        }

        newFoldings.Sort (static (a, b) => a.StartOffset.CompareTo (b.StartOffset));

        return newFoldings;
    }

    private bool TryGetMappedStructuralChange (DocumentChangeEventArgs e, out bool structuralChange)
    {
        structuralChange = false;
        OffsetChangeMap map = e.OffsetChangeMap;
        var hasInsertion = false;
        var hasRemoval = false;

        if (map.Count == 0)
        {
            return false;
        }

        foreach (OffsetChangeMapEntry entry in map)
        {
            hasInsertion |= entry.InsertionLength > 0;
            hasRemoval |= entry.RemovalLength > 0;

            if (entry is { InsertionLength: > 0, RemovalLength: > 0 })
            {
                return false;
            }
        }

        if (!hasInsertion && !hasRemoval)
        {
            return false;
        }

        if (hasInsertion && hasRemoval)
        {
            return false;
        }

        structuralChange = hasInsertion
            ? MappedInsertionsContainStructuralCharacter (map, e.InsertedText, e.Offset)
            : MappedRemovalsContainStructuralCharacter (map, e.RemovedText, e.Offset, e.RemovalLength);

        return true;
    }

    private bool MappedInsertionsContainStructuralCharacter (OffsetChangeMap map, ITextSource text, int baseOffset)
    {
        if (InsertionEntriesUseInsertedTextCoordinates (map, baseOffset, text.TextLength))
        {
            return MappedInsertionsContainStructuralCharacterWithoutShift (map, text, baseOffset);
        }

        var insertedShift = 0;

        foreach (OffsetChangeMapEntry entry in map)
        {
            if (entry.InsertionLength == 0)
            {
                continue;
            }

            var relativeOffset = entry.Offset - baseOffset + insertedShift;

            if (ContainsStructuralCharacter (text, relativeOffset, entry.InsertionLength))
            {
                return true;
            }

            insertedShift += entry.InsertionLength;
        }

        return false;
    }

    private bool MappedInsertionsContainStructuralCharacterWithoutShift (
        OffsetChangeMap map,
        ITextSource text,
        int baseOffset)
    {
        foreach (OffsetChangeMapEntry entry in map)
        {
            if (entry.InsertionLength == 0)
            {
                continue;
            }

            var relativeOffset = entry.Offset - baseOffset;

            if (ContainsStructuralCharacter (text, relativeOffset, entry.InsertionLength))
            {
                return true;
            }
        }

        return false;
    }

    private bool MappedRemovalsContainStructuralCharacter (
        OffsetChangeMap map,
        ITextSource text,
        int baseOffset,
        int removalLength)
    {
        if (!RemovalEntriesUseRemovedTextCoordinates (map, baseOffset, removalLength))
        {
            return MappedInsertionsContainStructuralCharacter (map.Invert (), text, baseOffset);
        }

        foreach (OffsetChangeMapEntry entry in map)
        {
            if (entry.RemovalLength == 0)
            {
                continue;
            }

            var relativeOffset = entry.Offset - baseOffset;

            if (ContainsStructuralCharacter (text, relativeOffset, entry.RemovalLength))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RemovalEntriesUseRemovedTextCoordinates (OffsetChangeMap map, int baseOffset, int removalLength)
    {
        return map.Count > 0
               && map[0].RemovalLength > 0
               && map[0].Offset + map[0].RemovalLength == baseOffset + removalLength;
    }

    private static bool InsertionEntriesUseInsertedTextCoordinates (OffsetChangeMap map, int baseOffset,
        int insertionLength)
    {
        return map.Count > 0
               && map[^1].InsertionLength > 0
               && map[^1].Offset + map[^1].InsertionLength == baseOffset + insertionLength;
    }

    private bool ContainsStructuralCharacter (ITextSource text)
    {
        return ContainsStructuralCharacter (text, 0, text.TextLength);
    }

    private bool ContainsStructuralCharacter (ITextSource text, int offset, int length)
    {
        for (var i = offset; i < offset + length; i++)
        {
            var ch = text.GetCharAt (i);

            if (ch == OpeningBrace
                || ch == ClosingBrace
                || ch is '\r' or '\n')
            {
                return true;
            }
        }

        return false;
    }
}
