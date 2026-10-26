namespace Okojo.JavaScript.Parsing;

public sealed class JsIdentifierTable
{
    private readonly Dictionary<string, int> indices = new(StringComparer.Ordinal);
    private readonly List<string> literals = new();
#if SHARPOS
    // SharpOS cut: no Dictionary.AlternateLookup (it needs `allows ref struct`,
    // which ILC 8 here lacks) — span lookups go through a string.
    private Dictionary<string, int> alternateLookup => indices;
#else
    private Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>>? alternateLookup;
#endif

    public string GetIdentifierLiteral(int identifierId)
    {
        if ((uint)identifierId >= (uint)literals.Count)
            throw new InvalidOperationException("Token does not reference an identifier literal.");

        return literals[identifierId];
    }

    public bool TryGetIdentifierId(string value, out int identifierId)
    {
        return indices.TryGetValue(value, out identifierId);
    }

    public bool TryGetIdentifierId(ReadOnlySpan<char> value, out int identifierId)
    {
#if SHARPOS
        return alternateLookup.TryGetValue(value.ToString(), out identifierId);
#else
        var lookup = alternateLookup ??= indices.GetAlternateLookup<ReadOnlySpan<char>>();
        return lookup.TryGetValue(value, out identifierId);
#endif
    }

    public int AddIdentifierLiteral(string value)
    {
        if (indices.TryGetValue(value, out var existingIndex))
            return existingIndex;

        literals.Add(value);
        var index = literals.Count - 1;
        indices[value] = index;
        return index;
    }

    public int AddIdentifierLiteral(ReadOnlySpan<char> value)
    {
#if SHARPOS
        var text = value.ToString();
        if (alternateLookup.TryGetValue(text, out var existingIndex))
            return existingIndex;
#else
        var lookup = alternateLookup ??= indices.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(value, out var existingIndex))
            return existingIndex;

        var text = value.ToString();
#endif
        literals.Add(text);
        var index = literals.Count - 1;
        indices[text] = index;
        return index;
    }
}
