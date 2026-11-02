// Adapted for Terminal.Gui from AvaloniaEdit d7a6b63

#nullable disable
// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Terminal.Gui.Editor.Highlighting.Xshd;

using Color = Terminal.Gui.Drawing.Color; // SharpOS: v1's Terminal.Gui.Color (an enum) would be found first

internal sealed class XmlHighlightingDefinition : IHighlightingDefinition
{
    private readonly Dictionary<string, HighlightingColor> _colorDict = new ();
    private readonly Dictionary<string, string> _propDict = new ();

    private readonly Dictionary<string, HighlightingRuleSet> _ruleSetDict = new ();

    public XmlHighlightingDefinition (XshdSyntaxDefinition xshd, IHighlightingDefinitionReferenceResolver resolver)
    {
        Name = xshd.Name;
        // Create HighlightingRuleSet instances
        RegisterNamedElementsVisitor rnev = new (this);
        xshd.AcceptElements (rnev);
        // Assign MainRuleSet so that references can be resolved
        foreach (XshdElement element in xshd.Elements)
        {
            if (element is XshdRuleSet xrs && xrs.Name == null)
            {
                if (MainRuleSet != null)
                {
                    throw Error (element, "Duplicate main RuleSet. There must be only one nameless RuleSet!");
                }

                MainRuleSet = rnev.RuleSets[xrs];
            }
        }

        if (MainRuleSet == null)
        {
            throw new HighlightingDefinitionInvalidException ("Could not find main RuleSet.");
        }

        // Translate elements within the rulesets (resolving references and processing imports)
        xshd.AcceptElements (new TranslateElementVisitor (this, rnev.RuleSets, resolver));

        foreach (XshdProperty p in xshd.Elements.OfType<XshdProperty> ())
        {
            _propDict.Add (p.Name, p.Value);
        }
    }

    public string Name { get; }

    public HighlightingRuleSet MainRuleSet { get; }

    public HighlightingRuleSet GetNamedRuleSet (string name)
    {
        if (string.IsNullOrEmpty (name))
        {
            return MainRuleSet;
        }

        return _ruleSetDict.GetValueOrDefault (name);
    }

    public HighlightingColor GetNamedColor (string name)
    {
        return _colorDict.GetValueOrDefault (name);
    }

    public IEnumerable<HighlightingColor> NamedHighlightingColors => _colorDict.Values;

    public IDictionary<string, string> Properties => _propDict;

    private static Exception Error (XshdElement element, string message)
    {
        if (element.LineNumber > 0)
        {
            return new HighlightingDefinitionInvalidException (
                $"Error at line {element.LineNumber}:\n{message}");
        }

        return new HighlightingDefinitionInvalidException (message);
    }

    public override string ToString ()
    {
        return Name;
    }

    #region RegisterNamedElements

    private sealed class RegisterNamedElementsVisitor : IXshdVisitor
    {
        internal readonly Dictionary<XshdRuleSet, HighlightingRuleSet> RuleSets = new ();

        private readonly XmlHighlightingDefinition _def;

        public RegisterNamedElementsVisitor (XmlHighlightingDefinition def)
        {
            _def = def;
        }

        public object VisitRuleSet (XshdRuleSet ruleSet)
        {
            HighlightingRuleSet hrs = new ();
            RuleSets.Add (ruleSet, hrs);
            if (ruleSet.Name != null)
            {
                if (ruleSet.Name.Length == 0)
                {
                    throw Error (ruleSet, "Name must not be the empty string");
                }

                if (_def._ruleSetDict.ContainsKey (ruleSet.Name))
                {
                    throw Error (ruleSet, "Duplicate rule set name '" + ruleSet.Name + "'.");
                }

                _def._ruleSetDict.Add (ruleSet.Name, hrs);
            }

            ruleSet.AcceptElements (this);
            return null;
        }

        public object VisitColor (XshdColor color)
        {
            if (color.Name != null)
            {
                if (color.Name.Length == 0)
                {
                    throw Error (color, "Name must not be the empty string");
                }

                if (_def._colorDict.ContainsKey (color.Name))
                {
                    throw Error (color, "Duplicate color name '" + color.Name + "'.");
                }

                _def._colorDict.Add (color.Name, new HighlightingColor ());
            }

            return null;
        }

        public object VisitKeywords (XshdKeywords keywords)
        {
            return keywords.ColorReference.AcceptVisitor (this);
        }

        public object VisitSpan (XshdSpan span)
        {
            span.BeginColorReference.AcceptVisitor (this);
            span.SpanColorReference.AcceptVisitor (this);
            span.EndColorReference.AcceptVisitor (this);
            return span.RuleSetReference.AcceptVisitor (this);
        }

        public object VisitImport (XshdImport import)
        {
            return import.RuleSetReference.AcceptVisitor (this);
        }

        public object VisitRule (XshdRule rule)
        {
            return rule.ColorReference.AcceptVisitor (this);
        }
    }

    #endregion

    #region TranslateElements

    private sealed class TranslateElementVisitor : IXshdVisitor
    {
        private readonly XmlHighlightingDefinition _def;
        private readonly HashSet<XshdRuleSet> _processedRuleSets = new ();
        private readonly HashSet<XshdRuleSet> _processingStartedRuleSets = new ();
        private readonly IHighlightingDefinitionReferenceResolver _resolver;
        private readonly Dictionary<HighlightingRuleSet, XshdRuleSet> _reverseRuleSetDict;
        private readonly Dictionary<XshdRuleSet, HighlightingRuleSet> _ruleSetDict;
        private bool _ignoreCase;

        public TranslateElementVisitor (XmlHighlightingDefinition def,
            Dictionary<XshdRuleSet, HighlightingRuleSet> ruleSetDict, IHighlightingDefinitionReferenceResolver resolver)
        {
            Debug.Assert (def != null);
            Debug.Assert (ruleSetDict != null);
            _def = def;
            _ruleSetDict = ruleSetDict;
            _resolver = resolver;
            _reverseRuleSetDict = new Dictionary<HighlightingRuleSet, XshdRuleSet> ();
            foreach (KeyValuePair<XshdRuleSet, HighlightingRuleSet> pair in ruleSetDict)
            {
                _reverseRuleSetDict.Add (pair.Value, pair.Key);
            }
        }

        public object VisitRuleSet (XshdRuleSet ruleSet)
        {
            HighlightingRuleSet rs = _ruleSetDict[ruleSet];
            if (_processedRuleSets.Contains (ruleSet))
            {
                return rs;
            }

            if (!_processingStartedRuleSets.Add (ruleSet))
            {
                throw Error (ruleSet, "RuleSet cannot be processed because it contains cyclic <Import>");
            }

            var oldIgnoreCase = _ignoreCase;
            if (ruleSet.IgnoreCase != null)
            {
                _ignoreCase = ruleSet.IgnoreCase.Value;
            }

            rs.Name = ruleSet.Name;

            foreach (XshdElement element in ruleSet.Elements)
            {
                var o = element.AcceptVisitor (this);
                if (o is HighlightingRuleSet elementRuleSet)
                {
                    Merge (rs, elementRuleSet);
                }
                else if (o is HighlightingSpan span)
                {
                    rs.Spans.Add (span);
                }
                else if (o is HighlightingRule elementRule)
                {
                    rs.Rules.Add (elementRule);
                }
            }

            _ignoreCase = oldIgnoreCase;
            _processedRuleSets.Add (ruleSet);

            return rs;
        }

        public object VisitColor (XshdColor color)
        {
            HighlightingColor c;
            if (color.Name != null)
            {
                c = _def._colorDict[color.Name];
            }
            else if (color.Foreground == null && color.Italic == null && color.Bold == null && color.Category == null)
            {
                return null;
            }
            else
            {
                c = new HighlightingColor ();
            }

            c.Name = color.Name;
            c.Foreground = color.Foreground;
            c.Background = color.Background;
            c.Underline = color.Underline;
            c.Strikethrough = color.Strikethrough;
            c.Italic = color.Italic;
            c.Bold = color.Bold;
            // SharpOS cut: c.Role = XshdRoleMap.ResolveRole (color.Name, color.Category);
            // — the role map resolves against Terminal.Gui v2's Scheme; an app on v1
            // takes the colour itself.
            return c;
        }

        public object VisitKeywords (XshdKeywords keywords)
        {
            if (keywords.Words.Count == 0)
            {
                return Error (keywords, "Keyword group must not be empty.");
            }

            foreach (var keyword in keywords.Words)
            {
                if (string.IsNullOrEmpty (keyword))
                {
                    throw Error (keywords, "Cannot use empty string as keyword");
                }
            }

            StringBuilder keyWordRegex = new ();
            // We can use "\b" only where the keyword starts/ends with a letter or digit, otherwise we don't
            // highlight correctly. (example: ILAsm-Mode.xshd with ".maxstack" keyword)
            if (keywords.Words.All (IsSimpleWord))
            {
                keyWordRegex.Append (@"\b(?>");
                // (?> = atomic group
                // atomic groups increase matching performance, but we
                // must ensure that the keywords are sorted correctly.
                // "\b(?>in|int)\b" does not match "int" because the atomic group captures "in".
                // To solve this, we are sorting the keywords by descending length.
                var i = 0;
                foreach (var keyword in keywords.Words.OrderByDescending (w => w.Length))
                {
                    if (i++ > 0)
                    {
                        keyWordRegex.Append ('|');
                    }

                    keyWordRegex.Append (Regex.Escape (keyword));
                }

                keyWordRegex.Append (@")\b");
            }
            else
            {
                keyWordRegex.Append ('(');
                var i = 0;
                foreach (var keyword in keywords.Words)
                {
                    if (i++ > 0)
                    {
                        keyWordRegex.Append ('|');
                    }

                    if (char.IsLetterOrDigit (keyword[0]))
                    {
                        keyWordRegex.Append (@"\b");
                    }

                    keyWordRegex.Append (Regex.Escape (keyword));
                    if (char.IsLetterOrDigit (keyword[keyword.Length - 1]))
                    {
                        keyWordRegex.Append (@"\b");
                    }
                }

                keyWordRegex.Append (')');
            }

            return new HighlightingRule
            {
                Color = GetColor (keywords, keywords.ColorReference),
                Regex = CreateRegex (keywords, keyWordRegex.ToString (), XshdRegexType.Default)
            };
        }

        public object VisitSpan (XshdSpan span)
        {
            var endRegex = span.EndRegex;
            if (string.IsNullOrEmpty (span.BeginRegex) && string.IsNullOrEmpty (span.EndRegex))
            {
                throw Error (span, "Span has no start/end regex.");
            }

            if (!span.Multiline)
            {
                if (endRegex == null)
                {
                    endRegex = "$";
                }
                else if (span.EndRegexType == XshdRegexType.IgnorePatternWhitespace)
                {
                    endRegex = $"($|{endRegex}\n)";
                }
                else
                {
                    endRegex = $"($|{endRegex})";
                }
            }

            HighlightingColor wholeSpanColor = GetColor (span, span.SpanColorReference);
            return new HighlightingSpan
            {
                StartExpression = CreateRegex (span, span.BeginRegex, span.BeginRegexType),
                EndExpression = CreateRegex (span, endRegex, span.EndRegexType),
                RuleSet = GetRuleSet (span, span.RuleSetReference),
                StartColor = GetColor (span, span.BeginColorReference),
                SpanColor = wholeSpanColor,
                EndColor = GetColor (span, span.EndColorReference),
                SpanColorIncludesStart = true,
                SpanColorIncludesEnd = true
            };
        }

        public object VisitImport (XshdImport import)
        {
            HighlightingRuleSet hrs = GetRuleSet (import, import.RuleSetReference);
            if (_reverseRuleSetDict.TryGetValue (hrs, out XshdRuleSet inputRuleSet))
            {
                // ensure the ruleset is processed before importing its members
                if (VisitRuleSet (inputRuleSet) != hrs)
                {
                    Debug.Assert (false, "this shouldn't happen");
                }
            }

            return hrs;
        }

        public object VisitRule (XshdRule rule)
        {
            return new HighlightingRule
            {
                Color = GetColor (rule, rule.ColorReference),
                Regex = CreateRegex (rule, rule.Regex, rule.RegexType)
            };
        }

        private static void Merge (HighlightingRuleSet target, HighlightingRuleSet source)
        {
            foreach (HighlightingRule rule in source.Rules)
            {
                target.Rules.Add (rule);
            }

            foreach (HighlightingSpan span in source.Spans)
            {
                target.Spans.Add (span);
            }
        }

        private static bool IsSimpleWord (string word)
        {
            return char.IsLetterOrDigit (word[0]) && char.IsLetterOrDigit (word, word.Length - 1);
        }

        private Regex CreateRegex (XshdElement position, string regex, XshdRegexType regexType)
        {
            if (regex == null)
            {
                throw Error (position, "Regex missing");
            }

            RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;
            if (regexType == XshdRegexType.IgnorePatternWhitespace)
            {
                options |= RegexOptions.IgnorePatternWhitespace;
            }

            if (_ignoreCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            try
            {
                return new Regex (regex, options);
            }
            catch (ArgumentException ex)
            {
                throw Error (position, ex.Message);
            }
        }

        private HighlightingColor GetColor (XshdElement position, XshdReference<XshdColor> colorReference)
        {
            if (colorReference.InlineElement != null)
            {
                return (HighlightingColor)colorReference.InlineElement.AcceptVisitor (this);
            }

            if (colorReference.ReferencedElement != null)
            {
                IHighlightingDefinition definition = GetDefinition (position, colorReference.ReferencedDefinition);
                HighlightingColor color = definition.GetNamedColor (colorReference.ReferencedElement);
                if (color == null)
                {
                    throw Error (position, $"Could not find color named '{colorReference.ReferencedElement}'.");
                }

                return color;
            }

            return null;
        }

        private IHighlightingDefinition GetDefinition (XshdElement position, string definitionName)
        {
            if (definitionName == null)
            {
                return _def;
            }

            if (_resolver == null)
            {
                throw Error (position,
                    "Resolving references to other syntax definitions is not possible because the IHighlightingDefinitionReferenceResolver is null.");
            }

            IHighlightingDefinition d = _resolver.GetDefinition (definitionName);
            if (d == null)
            {
                throw Error (position, $"Could not find definition with name '{definitionName}'.");
            }

            return d;
        }

        private HighlightingRuleSet GetRuleSet (XshdElement position, XshdReference<XshdRuleSet> ruleSetReference)
        {
            if (ruleSetReference.InlineElement != null)
            {
                return (HighlightingRuleSet)ruleSetReference.InlineElement.AcceptVisitor (this);
            }

            if (ruleSetReference.ReferencedElement != null)
            {
                IHighlightingDefinition definition = GetDefinition (position, ruleSetReference.ReferencedDefinition);
                HighlightingRuleSet ruleSet = definition.GetNamedRuleSet (ruleSetReference.ReferencedElement);
                if (ruleSet == null)
                {
                    throw Error (position, $"Could not find rule set named '{ruleSetReference.ReferencedElement}'.");
                }

                return ruleSet;
            }

            return null;
        }
    }

    #endregion
}
