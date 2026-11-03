// SharpOS cut: FrozenDictionary is not in our std; two small tables here, plain Dictionary below.
using Meziantou.Framework.Language.InternalSyntax;
using GreenToken = Meziantou.Framework.Language.InternalSyntax.SyntaxToken;
using GreenTrivia = Meziantou.Framework.Language.InternalSyntax.SyntaxTrivia;

namespace Meziantou.Framework.Language.Shell.Syntax.InternalSyntax;

/// <summary>Builds the immutable shell tokens and trivia, reusing the ones whose text never varies.</summary>
/// <remarks>
/// A green node is position-independent, so a token like <c>|</c> with no trivia is the same node wherever it appears
/// and can be shared by every tree in the process. Interning the operators and keywords, and the handful of
/// whitespace runs that make up almost all indentation, is what keeps parsing from allocating a node per character
/// of layout.
/// </remarks>
internal static class SyntaxFactory
{
    private static readonly Dictionary<int, GreenToken> FixedTokens = BuildFixedTokens();

    private static readonly Dictionary<string, GreenTrivia> CommonTrivia = BuildCommonTrivia();

    private static Dictionary<string, GreenTrivia> BuildCommonTrivia()
    {
        var trivia = new Dictionary<string, GreenTrivia>(StringComparer.Ordinal);
        foreach (var text in new[] { " ", "  ", "    ", "      ", "        ", "\t", "\t\t" })
            trivia[text] = new GreenTrivia((int)SyntaxKind.WhitespaceTrivia, text);
        return trivia;
    }

    private static readonly GreenTrivia LineFeedTrivia = new((int)SyntaxKind.EndOfLineTrivia, "\n");
    private static readonly GreenTrivia CarriageReturnLineFeedTrivia = new((int)SyntaxKind.EndOfLineTrivia, "\r\n");

    /// <summary>Returns the shared token for a kind whose text never varies.</summary>
    public static GreenToken Token(SyntaxKind kind)
        => FixedTokens.TryGetValue((int)kind, out var token) ? token : new GreenToken((int)kind, SyntaxFacts.GetText(kind), leadingTrivia: null, trailingTrivia: null, isMissing: false);

    public static GreenToken Token(GreenNode? leading, SyntaxKind kind, GreenNode? trailing)
        => leading is null && trailing is null ? Token(kind) : new GreenToken((int)kind, SyntaxFacts.GetText(kind), leading, trailing, isMissing: false);

    /// <summary>Creates a token whose text is not fixed by its kind, such as a word or a here-document body.</summary>
    public static GreenToken Token(GreenNode? leading, SyntaxKind kind, string text, GreenNode? trailing)
        => new((int)kind, text, leading, trailing, isMissing: false);

    /// <summary>Creates a token whose text and meaning differ, such as a quoted word.</summary>
    public static GreenToken TokenWithValue(GreenNode? leading, SyntaxKind kind, string text, string valueText, GreenNode? trailing)
        => string.Equals(text, valueText, StringComparison.Ordinal)
            ? Token(leading, kind, text, trailing)
            : new SyntaxTokenWithValue<string>((int)kind, text, valueText, valueText, leading, trailing, isMissing: false);

    /// <summary>Creates a zero-width token standing in for one the source does not have.</summary>
    public static GreenToken MissingToken(SyntaxKind kind) => new((int)kind, "", leadingTrivia: null, trailingTrivia: null, isMissing: true);

    public static GreenToken MissingToken(GreenNode? leading, SyntaxKind kind) => new((int)kind, "", leading, trailingTrivia: null, isMissing: true);

    public static GreenToken BadToken(GreenNode? leading, string text, GreenNode? trailing)
        => (GreenToken)new GreenToken((int)SyntaxKind.BadToken, text, leading, trailing, isMissing: false).AsSkippedText();

    public static GreenTrivia Trivia(SyntaxKind kind, string text)
    {
        if (kind == SyntaxKind.WhitespaceTrivia && CommonTrivia.TryGetValue(text, out var whitespace))
            return whitespace;

        if (kind == SyntaxKind.EndOfLineTrivia)
        {
            if (string.Equals(text, "\n", StringComparison.Ordinal))
                return LineFeedTrivia;

            if (string.Equals(text, "\r\n", StringComparison.Ordinal))
                return CarriageReturnLineFeedTrivia;
        }

        return new GreenTrivia((int)kind, text);
    }

    public static GreenNode? List(ReadOnlySpan<GreenNode?> items) => Meziantou.Framework.Language.InternalSyntax.SyntaxList.List(items);

    /// <summary>Builds a list that can be projected into a red node even when it holds a single token.</summary>
    public static GreenNode? ListNode(ReadOnlySpan<GreenNode?> items) => Meziantou.Framework.Language.InternalSyntax.SyntaxList.ListNode(items);

    private static Dictionary<int, GreenToken> BuildFixedTokens()
    {
        var tokens = new Dictionary<int, GreenToken>();
        // SharpOS cut: Enum.GetValues needs metadata that is not here; the kinds run from None to the last one.
        for (var kind = SyntaxKind.None; kind <= SyntaxKind.SemicolonPipeToken; kind++)
        {
            var text = SyntaxFacts.GetText(kind);
            if (text.Length > 0)
            {
                tokens[(int)kind] = new GreenToken((int)kind, text, leadingTrivia: null, trailingTrivia: null, isMissing: false);
            }
        }

        tokens[(int)SyntaxKind.EndOfFileToken] = new GreenToken((int)SyntaxKind.EndOfFileToken, "", leadingTrivia: null, trailingTrivia: null, isMissing: false);

        return tokens;
    }
}
