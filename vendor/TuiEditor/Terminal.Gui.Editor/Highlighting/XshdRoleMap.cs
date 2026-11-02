using System.Collections.Frozen;
using Terminal.Gui.Drawing;

namespace Terminal.Gui.Editor.Highlighting;

/// <summary>
///     Bridges xshd <c>&lt;Color name="..."&gt;</c> names to Terminal.Gui code-token
///     <see cref="Drawing.VisualRole" />s. This is the grammar-specific half of the syntax-theme layer:
///     the colorizer asks for a <see cref="Drawing.VisualRole" />, the active <see cref="Drawing.Scheme" /> resolves
///     the actual <see cref="System.Attribute" />. Names absent from the built-in table (and without a
///     per-color <c>category=</c> override) have no role and fall back to the xshd-declared color.
/// </summary>
/// <remarks>
///     A future TextMate-grammar bridge will be a sibling map producing the same
///     <see cref="Drawing.VisualRole" />s; the colorizer's resolution logic does not branch on grammar.
/// </remarks>
internal static class XshdRoleMap
{
    /// <summary>
    ///     Built-in xshd-name → <see cref="Drawing.VisualRole" /> table covering the common cross-language
    ///     names across the bundled <c>.xshd</c> definitions. Keys are the literal
    ///     <c>&lt;Color name="..."&gt;</c> values and are matched case-sensitively (xshd convention).
    ///     Private and frozen: this is process-wide shared state, so it must not be mutable or
    ///     reachable (even via <c>InternalsVisibleTo</c>) for accidental reassignment.
    /// </summary>
    private static readonly FrozenDictionary<string, VisualRole> Defaults =
        new Dictionary<string, VisualRole> (StringComparer.Ordinal)
        {
            // CodeComment
            ["Comment"] = VisualRole.CodeComment,
            ["DocComment"] = VisualRole.CodeComment,
            ["CommentTags"] = VisualRole.CodeComment,
            ["XmlDoc"] = VisualRole.CodeComment,

            // CodeKeyword
            ["Keyword"] = VisualRole.CodeKeyword,
            ["Keywords"] = VisualRole.CodeKeyword,
            ["ControlFlow"] = VisualRole.CodeKeyword,
            ["AccessKeywords"] = VisualRole.CodeKeyword,
            ["ContextKeywords"] = VisualRole.CodeKeyword,
            ["ExceptionKeywords"] = VisualRole.CodeKeyword,
            ["GotoKeywords"] = VisualRole.CodeKeyword,
            ["OperatorKeywords"] = VisualRole.CodeKeyword,
            ["ParameterModifiers"] = VisualRole.CodeKeyword,
            ["Modifiers"] = VisualRole.CodeKeyword,
            ["AccessModifiers"] = VisualRole.CodeKeyword,
            ["Visibility"] = VisualRole.CodeKeyword,
            ["NamespaceKeywords"] = VisualRole.CodeKeyword,
            ["JumpStatements"] = VisualRole.CodeKeyword,
            ["JumpKeywords"] = VisualRole.CodeKeyword,
            ["LoopKeywords"] = VisualRole.CodeKeyword,
            ["IterationStatements"] = VisualRole.CodeKeyword,
            ["SelectionStatements"] = VisualRole.CodeKeyword,
            ["ExceptionHandling"] = VisualRole.CodeKeyword,
            ["ExceptionHandlingStatements"] = VisualRole.CodeKeyword,
            ["SemanticKeywords"] = VisualRole.CodeKeyword,
            ["CheckedKeyword"] = VisualRole.CodeKeyword,
            ["UnsafeKeywords"] = VisualRole.CodeKeyword,
            ["CompoundKeywords"] = VisualRole.CodeKeyword,
            ["FunctionKeywords"] = VisualRole.CodeKeyword,
            ["Package"] = VisualRole.CodeKeyword,
            ["Friend"] = VisualRole.CodeKeyword,
            ["This"] = VisualRole.CodeKeyword,
            ["ThisOrBaseReference"] = VisualRole.CodeKeyword,
            ["Void"] = VisualRole.CodeKeyword,
            ["JavaScriptKeyWords"] = VisualRole.CodeKeyword,
            ["JavaScriptIntrinsics"] = VisualRole.CodeKeyword,

            // CodeString
            ["String"] = VisualRole.CodeString,
            ["Char"] = VisualRole.CodeString,
            ["Character"] = VisualRole.CodeString,
            ["StringInterpolation"] = VisualRole.CodeString,
            ["Regex"] = VisualRole.CodeString,

            // CodeNumber
            ["Number"] = VisualRole.CodeNumber,
            ["NumberLiteral"] = VisualRole.CodeNumber,
            ["Digits"] = VisualRole.CodeNumber,
            ["DateLiteral"] = VisualRole.CodeNumber,
            ["Literals"] = VisualRole.CodeNumber,

            // CodeConstant
            ["Bool"] = VisualRole.CodeConstant,
            ["Null"] = VisualRole.CodeConstant,
            ["NullOrValueKeywords"] = VisualRole.CodeConstant,
            ["TrueFalse"] = VisualRole.CodeConstant,
            ["BooleanConstants"] = VisualRole.CodeConstant,
            ["Constants"] = VisualRole.CodeConstant,

            // CodeOperator
            ["Operators"] = VisualRole.CodeOperator,

            // CodeType
            ["ValueTypeKeywords"] = VisualRole.CodeType,
            ["ReferenceTypeKeywords"] = VisualRole.CodeType,
            ["DataTypes"] = VisualRole.CodeType,
            ["ValueTypes"] = VisualRole.CodeType,
            ["ReferenceTypes"] = VisualRole.CodeType,
            ["TypeKeywords"] = VisualRole.CodeType,

            // CodePreprocessor
            ["Preprocessor"] = VisualRole.CodePreprocessor,

            // CodePunctuation
            ["Punctuation"] = VisualRole.CodePunctuation,
            ["CurlyBraces"] = VisualRole.CodePunctuation,
            ["Colon"] = VisualRole.CodePunctuation,
            ["Slash"] = VisualRole.CodePunctuation,
            ["Assignment"] = VisualRole.CodePunctuation,
            ["XmlPunctuation"] = VisualRole.CodePunctuation,

            // CodeFunctionName
            ["MethodCall"] = VisualRole.CodeFunctionName,
            ["MethodName"] = VisualRole.CodeFunctionName,
            ["Command"] = VisualRole.CodeFunctionName,

            // CodeIdentifier
            ["FieldName"] = VisualRole.CodeIdentifier,
            ["Variable"] = VisualRole.CodeIdentifier,
            ["Property"] = VisualRole.CodeIdentifier,
            ["Value"] = VisualRole.CodeIdentifier,
            ["Selector"] = VisualRole.CodeIdentifier,
            ["Class"] = VisualRole.CodeIdentifier,
            ["Namespace"] = VisualRole.CodeIdentifier,

            // CodeAttribute
            ["JavaDocTags"] = VisualRole.CodeAttribute,
            ["KnownDocTags"] = VisualRole.CodeAttribute,
            ["Attributes"] = VisualRole.CodeAttribute,
            ["EntityReference"] = VisualRole.CodeAttribute,
            ["Entities"] = VisualRole.CodeAttribute,
            ["Tags"] = VisualRole.CodeAttribute,
            ["HtmlTag"] = VisualRole.CodeAttribute,
            ["ScriptTag"] = VisualRole.CodeAttribute,
            ["JavaScriptTag"] = VisualRole.CodeAttribute,
            ["JScriptTag"] = VisualRole.CodeAttribute,
            ["VBScriptTag"] = VisualRole.CodeAttribute,
            ["UnknownScriptTag"] = VisualRole.CodeAttribute,
            ["UnknownAttribute"] = VisualRole.CodeAttribute
        }.ToFrozenDictionary (StringComparer.Ordinal);

    /// <summary>
    ///     Looks up the <see cref="VisualRole" /> for an xshd color name, or <see langword="null" />
    ///     if the name has no mapping (caller falls back to the xshd-declared color).
    /// </summary>
    internal static VisualRole? TryGetRole (string? xshdColorName)
    {
        if (!string.IsNullOrEmpty (xshdColorName) && Defaults.TryGetValue (xshdColorName, out VisualRole role))
        {
            return role;
        }

        return null;
    }

    /// <summary>
    ///     Resolves the <see cref="VisualRole" /> for an xshd color. A per-color <c>category=</c>
    ///     attribute (parsed as a defined <see cref="VisualRole" /> name, case-insensitive) wins
    ///     over the built-in <paramref name="name" /> table. A <paramref name="category" /> that is
    ///     not a defined role name — including numeric strings, which <see cref="Enum.TryParse{T}(string,bool,out T)" />
    ///     would otherwise accept — falls through to the name table; an unmapped name yields
    ///     <see langword="null" />.
    /// </summary>
    internal static VisualRole? ResolveRole (string? name, string? category)
    {
        if (!string.IsNullOrEmpty (category)
            && Enum.TryParse (category, true, out VisualRole parsed)
            && Enum.IsDefined (parsed))
        {
            return parsed;
        }

        return TryGetRole (name);
    }
}
